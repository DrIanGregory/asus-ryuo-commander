using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Puts a custom looping video on the Ryuo IV panel — the same three steps ASUS Info Hub
/// performs (verified by USB capture):
/// <list type="number">
/// <item><b>Transcode</b> the source video to the panel's format with ffmpeg
/// (1920×960 — the decoder's width limit, matching ASUS's stock videos —
/// H.264 High/yuv420p, 30 fps, no audio, mp4).</item>
/// <item><b>Push</b> the file to <c>/sdcard/pcMedia</c> over adb (the file bytes travel on the
/// device's ADB interface — MI_01 — not the HID control channel).</item>
/// <item><b>Activate</b> it with a HID <c>waterBlockScreenId</c> config
/// (<see cref="BacklightService.SetPanelVideo"/>), which makes HomeUI loop it full-screen.</item>
/// </list>
/// ffmpeg is located next to the app (or on PATH); adb is ASUS's bundled adb.exe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaService
{
    private const string DeviceMediaDir = "/sdcard/pcMedia";

    private readonly ILogger _log;
    private readonly BacklightService _backlight;

    public MediaService(ILogger log, BacklightService backlight)
    {
        _log = log.ForContext<MediaService>();
        _backlight = backlight;
    }

    public bool FfmpegAvailable => FindFfmpeg() is not null;
    public bool AdbAvailable => FindAdb() is not null;

    /// <summary>
    /// Transcode <paramref name="sourceVideoPath"/>, upload it, and set it as the panel video.
    /// <paramref name="progress"/> receives short status lines. Runs on a background thread.
    /// On success, <c>DeviceFileName</c> is the on-device name — persist it so the video can
    /// be re-asserted when the panel forgets its screen config (it does, on every reboot).
    /// </summary>
    public async Task<(bool Ok, string Message, string? DeviceFileName)> SetPanelVideoAsync(
        string sourceVideoPath, VideoScaleMode scaleMode = VideoScaleMode.Fill,
        string?[]? sysinfoDisplay = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() => SetPanelVideo(sourceVideoPath, scaleMode, sysinfoDisplay, progress, ct), ct);
    }

    private (bool Ok, string Message, string? DeviceFileName) SetPanelVideo(
        string sourceVideoPath, VideoScaleMode scaleMode, string?[]? sysinfoDisplay,
        IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(sourceVideoPath))
                return (false, "Source video not found: " + sourceVideoPath, null);

            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null)
                return (false, "ffmpeg not found. Place ffmpeg.exe next to the app (see tools\\fetch-ffmpeg.ps1).", null);

            var adb = FindAdb();
            if (adb is null)
                return (false, "adb not found (install 'ASUS Info Hub - ROG RYUO IV').", null);

            // Device file name: timestamp, like Info Hub (avoids collisions / stale caching).
            string deviceName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture) + ".mp4";
            string tempOut = Path.Combine(Path.GetTempPath(), "ryuo_" + deviceName);

            // 1) Transcode ------------------------------------------------------------
            progress?.Report($"Transcoding video ({scaleMode})…");
            ct.ThrowIfCancellationRequested();
            var (tcOk, tcMsg) = Transcode(ffmpeg, sourceVideoPath, tempOut, scaleMode, ct);
            if (!tcOk) return (false, tcMsg, null);

            try
            {
                // 2) Push over adb ---------------------------------------------------
                progress?.Report("Uploading to the panel…");
                ct.ThrowIfCancellationRequested();
                var (pOk, pMsg) = Push(adb, tempOut, deviceName);
                if (!pOk) return (false, pMsg, null);

                // 3) Activate over HID ----------------------------------------------
                progress?.Report("Activating…");
                ct.ThrowIfCancellationRequested();
                var (aOk, aMsg) = _backlight.SetPanelVideo(deviceName, sysinfoDisplay);
                if (!aOk) return (false, aMsg, null);

                progress?.Report("Done — the panel is now playing your video.");
                return (true, $"Panel video set ({deviceName}).", deviceName);
            }
            finally
            {
                try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { /* temp cleanup */ }
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled.", null);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SetPanelVideo failed.");
            return (false, "Error: " + ex.Message, null);
        }
    }

    // ---------------------------------------------------------------- steps

    private (bool Ok, string Message) Transcode(
        string ffmpeg, string src, string dst, VideoScaleMode scaleMode, CancellationToken ct)
    {
        // Target 1920×960 — the panel is 2240×1080 but its RK3562 hardware decoder rejects
        // anything wider than 1920 ("isCodecSupport error: support width = 1920" → black
        // screen). ASUS's own stock videos are 1920×960 H.264 High yuv420p 30fps and the
        // panel stretches the received frame across the whole screen; match that exactly.
        // The scale mode decides how the source lands in that frame — bars baked into the
        // pixels (Fit) are the only reason a video ever shows smaller than the LCD.
        string shape = scaleMode switch
        {
            VideoScaleMode.Fill => "scale=1920:960:force_original_aspect_ratio=increase," +
                                   "crop=1920:960",
            VideoScaleMode.Stretch => "scale=1920:960,setsar=1",
            _ => "scale=1920:960:force_original_aspect_ratio=decrease," +
                 "pad=1920:960:(ow-iw)/2:(oh-ih)/2:color=black",
        };
        string vf = shape + ",fps=30,format=yuv420p";
        var args = new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", src, "-an",
            "-vf", vf,
            "-c:v", "libx264", "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-b:v", "4000k", "-maxrate", "4000k", "-bufsize", "8000k",
            "-movflags", "+faststart",
            dst,
        };
        _log.Information("Transcoding {Src} -> {Dst}", src, dst);
        var (exit, _, err) = Run(ffmpeg, args, null, TimeSpan.FromMinutes(10), ct);
        if (exit != 0 || !File.Exists(dst) || new FileInfo(dst).Length == 0)
            return (false, "Transcode failed: " + (err.Trim().Length > 0 ? Trim(err) : $"ffmpeg exit {exit}"));
        _log.Information("Transcoded OK ({Bytes} bytes).", new FileInfo(dst).Length);
        return (true, "ok");
    }

    private (bool Ok, string Message) Push(string adb, string localFile, string deviceName)
    {
        var workDir = AdbWorkingDir(adb);
        // Ensure the target dir exists, then push.
        Run(adb, new[] { "shell", "mkdir", "-p", DeviceMediaDir }, workDir, TimeSpan.FromSeconds(15), default);
        string remote = DeviceMediaDir + "/" + deviceName;
        _log.Information("adb push {Local} -> {Remote}", localFile, remote);
        var (exit, outp, err) = Run(adb, new[] { "push", localFile, remote }, workDir, TimeSpan.FromMinutes(5), default);
        if (exit != 0)
            return (false, "Upload (adb push) failed: " + (err.Trim().Length > 0 ? Trim(err) : Trim(outp)));
        return (true, "ok");
    }

    // ---------------------------------------------------------------- discovery

    private static string? FindFfmpeg()
    {
        foreach (var p in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                     Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
                 })
        {
            if (File.Exists(p)) return p;
        }
        return FromPath("ffmpeg.exe");
    }

    private static string? FindAdb()
    {
        foreach (var p in new[]
                 {
                     @"C:\Program Files\ASUS Info Hub - ROG RYUO IV\bin\adb.exe",
                     @"C:\Program Files (x86)\ASUS Info Hub - ROG RYUO IV\bin\adb.exe",
                     Path.Combine(AppContext.BaseDirectory, "adb.exe"),
                 })
        {
            if (File.Exists(p)) return p;
        }
        return FromPath("adb.exe");
    }

    // adb.exe loads AdbWinApi.dll from its own folder's parent; run with that as the cwd.
    private static string AdbWorkingDir(string adb)
        => Directory.GetParent(Path.GetDirectoryName(adb)!)?.FullName ?? Path.GetDirectoryName(adb)!;

    private static string? FromPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    // ---------------------------------------------------------------- process helper

    private (int ExitCode, string StdOut, string StdErr) Run(
        string exe, IReadOnlyList<string> args, string? workingDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "", "timed out");
            }
            return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Process failed: {Exe}", exe);
            return (-1, "", ex.Message);
        }
    }

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}
