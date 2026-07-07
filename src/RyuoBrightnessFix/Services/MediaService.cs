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
    private const string DevicePresetDir = "/sdcard/pcMediaPreset";
    // Videos + their .png thumbnails both live in the cache; size for a ~12-entry playlist.
    private const int CacheKeepCount = 24;

    private readonly ILogger _log;
    private readonly BacklightService _backlight;

    public MediaService(ILogger log, BacklightService backlight)
    {
        _log = log.ForContext<MediaService>();
        _backlight = backlight;
    }

    /// <summary>%APPDATA%\RyuoBrightnessFix\videocache — local copies of on-device videos, so the
    /// Video tab can always show what the LCD is playing.</summary>
    public static string CacheDir => Path.Combine(AppConstants.AppDataDir, "videocache");

    /// <summary>
    /// A local playable copy of an on-device video: the cached transcode if we set it, else
    /// pulled back over adb (pcMedia first, then the stock preset dir). Null when the file
    /// can't be obtained (no adb / file gone). Runs on a background thread; safe to call
    /// repeatedly — cache hits return instantly.
    /// </summary>
    public async Task<string?> GetLocalCopyAsync(string deviceFileName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string local = Path.Combine(CacheDir, deviceFileName);
                if (File.Exists(local) && new FileInfo(local).Length > 0) return local;

                var adb = FindAdb();
                if (adb is null)
                {
                    _log.Debug("No adb — cannot pull {File} for the preview.", deviceFileName);
                    return null;
                }
                var workDir = AdbWorkingDir(adb);
                foreach (var remoteDir in new[] { DeviceMediaDir, DevicePresetDir })
                {
                    ct.ThrowIfCancellationRequested();
                    var (exit, _, _) = Run(adb, new[] { "pull", remoteDir + "/" + deviceFileName, local },
                        workDir, TimeSpan.FromMinutes(2), ct);
                    if (exit == 0 && File.Exists(local) && new FileInfo(local).Length > 0)
                    {
                        _log.Information("Pulled {File} from the panel for the preview.", deviceFileName);
                        return local;
                    }
                }
                try { if (File.Exists(local)) File.Delete(local); } catch { }
                _log.Warning("Could not pull {File} from the panel (not found on device?).", deviceFileName);
                return null;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                _log.Warning(ex, "Getting a local copy of {File} failed.", deviceFileName);
                return null;
            }
        }, ct);
    }

    /// <summary>
    /// A thumbnail (PNG, 192px wide) for an on-device video, extracted with ffmpeg from the
    /// local copy (which is pulled back over adb if needed). Cached beside the video; returns
    /// null when neither the video nor ffmpeg is obtainable.
    /// </summary>
    public async Task<string?> GetThumbnailAsync(string deviceFileName, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string thumb = Path.Combine(CacheDir, deviceFileName + ".png");
            if (File.Exists(thumb) && new FileInfo(thumb).Length > 0) return thumb;

            string? local = await GetLocalCopyAsync(deviceFileName, ct);
            if (local is null) return null;
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null) return null;

            var (exit, _, err) = Run(ffmpeg, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-ss", "1", "-i", local,
                "-frames:v", "1", "-vf", "scale=192:-2",
                thumb,
            }, null, TimeSpan.FromSeconds(30), ct);
            if (exit == 0 && File.Exists(thumb) && new FileInfo(thumb).Length > 0) return thumb;

            // Very short clips can have no frame at t=1s — retry from the start.
            (exit, _, err) = Run(ffmpeg, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", local, "-frames:v", "1", "-vf", "scale=192:-2", thumb,
            }, null, TimeSpan.FromSeconds(30), ct);
            if (exit == 0 && File.Exists(thumb) && new FileInfo(thumb).Length > 0) return thumb;

            _log.Warning("Thumbnail extraction failed for {File}: {Err}", deviceFileName, Trim(err));
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Thumbnail for {File} failed.", deviceFileName);
            return null;
        }
    }

    /// <summary>Keep the newest few cached videos; old panel videos are dead weight.</summary>
    private void PruneCache()
    {
        try
        {
            var files = new DirectoryInfo(CacheDir).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(CacheKeepCount);
            foreach (var f in files)
            {
                try { f.Delete(); } catch { /* in use by the preview — skip */ }
            }
        }
        catch { /* cache dir missing — nothing to prune */ }
    }

    public bool FfmpegAvailable => FindFfmpeg() is not null;
    public bool AdbAvailable => FindAdb() is not null;

    /// <summary>
    /// Transcode <paramref name="sourceVideoPath"/> and upload it to the panel, ready to be
    /// activated as part of the playlist. <paramref name="progress"/> receives short status
    /// lines. Runs on a background thread. On success, <c>DeviceFileName</c> is the on-device
    /// name to add to the playlist (persist it — the panel forgets its screen config on
    /// every reboot and the playlist is re-asserted from settings).
    /// </summary>
    public async Task<(bool Ok, string Message, string? DeviceFileName)> PrepareVideoAsync(
        string sourceVideoPath, VideoScaleMode scaleMode = VideoScaleMode.Fill,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() => PrepareVideo(sourceVideoPath, scaleMode, progress, ct), ct);
    }

    private (bool Ok, string Message, string? DeviceFileName) PrepareVideo(
        string sourceVideoPath, VideoScaleMode scaleMode, IProgress<string>? progress, CancellationToken ct)
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
                // Keep the transcode as the local cache copy so the Video tab can show what
                // the LCD is playing without pulling it back from the device.
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.Move(tempOut, Path.Combine(CacheDir, deviceName), overwrite: true);
                    PruneCache();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Caching the transcoded video failed (preview will pull it back over adb).");
                }

                progress?.Report("Uploaded.");
                return (true, $"Video uploaded to the panel ({deviceName}).", deviceName);
            }
            finally
            {
                try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { /* already moved to the cache */ }
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

    /// <summary>
    /// Recover source paths for library entries that predate provenance tracking (pre-1.9
    /// settings recorded only the device file names). The transcode step has always logged
    /// <c>Transcoding &lt;source&gt; -&gt; &lt;temp&gt;\ryuo_&lt;deviceName&gt;</c> at INFO,
    /// so the app's own rolling logs are an authoritative record: scan them newest-first and
    /// return the source seen for each requested device file (only when that source still
    /// exists on disk). Entries whose log lines have rotated away stay unresolved. Static and
    /// read-only — no device access; tolerates the live log file being open for writing.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RecoverSourcePathsFromLogs(
        string logDir, IReadOnlyCollection<string> deviceFileNames, ILogger log)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var unresolved = new HashSet<string>(deviceFileNames, StringComparer.OrdinalIgnoreCase);
            if (unresolved.Count == 0 || !Directory.Exists(logDir)) return result;

            // ryuo-YYYYMMDD.log sorts chronologically by name; scan newest first.
            var logFiles = Directory.GetFiles(logDir, "ryuo-*.log").OrderByDescending(f => f, StringComparer.Ordinal);
            foreach (var logFile in logFiles)
            {
                if (unresolved.Count == 0) break;
                try
                {
                    // Serilog holds the current day's file open for writing — share accordingly.
                    using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        int tcIdx = line.IndexOf(" Transcoding ", StringComparison.Ordinal);
                        if (tcIdx < 0) continue;
                        int arrowIdx = line.LastIndexOf(" -> ", StringComparison.Ordinal);
                        if (arrowIdx <= tcIdx) continue;

                        string source = line[(tcIdx + " Transcoding ".Length)..arrowIdx].Trim();
                        string dest = line[(arrowIdx + " -> ".Length)..].Trim();
                        string destName = Path.GetFileName(dest);
                        if (!destName.StartsWith("ryuo_", StringComparison.OrdinalIgnoreCase)) continue;
                        string deviceName = destName["ryuo_".Length..];

                        if (unresolved.Contains(deviceName) && !result.ContainsKey(deviceName) &&
                            File.Exists(source))
                        {
                            result[deviceName] = source;
                            unresolved.Remove(deviceName);
                            if (unresolved.Count == 0) break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "Provenance recovery could not read {LogFile}; continuing with the rest.", logFile);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Provenance recovery from the logs failed.");
        }
        return result;
    }

    /// <summary>
    /// Best-effort removal of a video this app previously pushed: the device-side copy in
    /// /sdcard/pcMedia plus the local cache copies (video + thumbnail). Used when a
    /// scale-mode re-encode replaces an upload under a new name, so dead files don't pile
    /// up on the panel. Never throws; failures are logged (a cache file locked by the
    /// preview player is cleaned up by the next cache prune).
    /// </summary>
    public async Task TryDeleteDeviceVideoAsync(string deviceFileName)
    {
        await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceFileName) ||
                    deviceFileName.Contains('/') || deviceFileName.Contains('\\'))
                {
                    _log.Warning("Refusing to delete a suspicious device file name: {File}", deviceFileName);
                    return;
                }
                foreach (var f in new[]
                         {
                             Path.Combine(CacheDir, deviceFileName),
                             Path.Combine(CacheDir, deviceFileName + ".png"),
                         })
                {
                    try { if (File.Exists(f)) File.Delete(f); }
                    catch (Exception ex) { _log.Debug(ex, "Cache delete of {File} failed (in use by the preview?).", f); }
                }
                var adb = FindAdb();
                if (adb is null)
                {
                    _log.Debug("No adb — leaving the replaced video {File} on the device.", deviceFileName);
                    return;
                }
                var (exit, _, err) = Run(adb, new[] { "shell", "rm", "-f", DeviceMediaDir + "/" + deviceFileName },
                    AdbWorkingDir(adb), TimeSpan.FromSeconds(15), default);
                if (exit == 0)
                    _log.Information("Deleted the replaced panel video {File}.", deviceFileName);
                else
                    _log.Warning("Deleting {File} from the panel failed: {Err}", deviceFileName, Trim(err));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Deleting the replaced panel video {File} failed.", deviceFileName);
            }
        });
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
