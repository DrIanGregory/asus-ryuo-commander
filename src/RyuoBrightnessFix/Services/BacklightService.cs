using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// THE fix. The Ryuo IV LCD is an Android device ("cm16"); its brightness is the kernel
/// backlight node /sys/class/backlight/backlight/brightness (0–256), reachable only over adb.
/// We use the adb.exe ASUS already ships (run from its own folder so it finds AdbWinApi.dll),
/// and the device grants root over adb, so we can read/write the node directly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BacklightService
{
    public const int MaxBacklight = 256;
    private const string Node = "/sys/class/backlight/backlight/brightness";

    private readonly ILogger _log;
    private readonly string? _adb;
    private readonly string _workingDir;

    public BacklightService(ILogger log)
    {
        _log = log.ForContext<BacklightService>();
        _adb = FindAdb();
        _workingDir = _adb is not null
            ? Directory.GetParent(Path.GetDirectoryName(_adb)!)?.FullName ?? Path.GetDirectoryName(_adb)!
            : "";
    }

    public bool AdbAvailable => _adb is not null;

    private static string? FindAdb()
    {
        foreach (var p in new[]
                 {
                     @"C:\Program Files\ASUS Info Hub - ROG RYUO IV\bin\adb.exe",
                     @"C:\Program Files (x86)\ASUS Info Hub - ROG RYUO IV\bin\adb.exe",
                 })
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>True when adb sees the Ryuo LCD connected.</summary>
    public bool DeviceConnected()
    {
        if (_adb is null) return false;
        var (exit, outp, _) = Run(new[] { "get-state" }, TimeSpan.FromSeconds(8));
        return exit == 0 && outp.Trim().Equals("device", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Current backlight value (0–256), or null on failure.</summary>
    public int? GetBacklight()
    {
        if (_adb is null) return null;
        var (exit, outp, _) = Run(new[] { "shell", "cat " + Node }, TimeSpan.FromSeconds(8));
        if (exit != 0) return null;
        return int.TryParse(outp.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Set the raw backlight (0–256). Returns (ok, message).
    /// </summary>
    /// <param name="verify">
    /// When true (default) the value is read back to confirm the device accepted it.
    /// Pass false on the suspend path: Windows gives apps only a short window to react
    /// to <see cref="Microsoft.Win32.PowerModes.Suspend"/>, so we write fast and skip the
    /// extra round-trip rather than risk being cut off mid-readback.
    /// </param>
    public (bool Ok, string Message) SetBacklight(int value, bool verify = true)
    {
        if (_adb is null)
            return (false, "ASUS adb.exe not found (is 'ASUS Info Hub - ROG RYUO IV' installed?).");

        value = Math.Clamp(value, 0, MaxBacklight);
        var writeTimeout = verify ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(4);
        var (exit, _, err) = Run(new[] { "shell", $"echo {value} > {Node}" }, writeTimeout);
        if (exit != 0)
            return (false, "adb write failed: " + (err.Trim().Length > 0 ? err.Trim() : $"exit {exit}"));

        if (!verify)
        {
            _log.Information("Backlight write {Value}/{Max} issued (unverified).", value, MaxBacklight);
            return (true, $"Backlight write {value}/{MaxBacklight} issued.");
        }

        var now = GetBacklight();
        if (now is int n && Math.Abs(n - value) <= 1)
        {
            _log.Information("Backlight set to {Value}/{Max}.", n, MaxBacklight);
            return (true, $"Backlight set to {n}/{MaxBacklight}.");
        }
        return (false, $"Wrote {value} but read back {now?.ToString() ?? "?"} — device may have rejected it.");
    }

    /// <summary>Set brightness as a 0–100% value.</summary>
    /// <param name="verify">See <see cref="SetBacklight"/>; pass false on the fast suspend path.</param>
    public (bool Ok, string Message) SetPercent(int percent, bool verify = true)
    {
        percent = Math.Clamp(percent, 0, 100);
        int raw = (int)Math.Round(percent * MaxBacklight / 100.0);
        if (percent > 0 && raw < 1) raw = 1;
        return SetBacklight(raw, verify);
    }

    /// <summary>Current brightness as 0–100%, or null.</summary>
    public int? GetPercent()
    {
        var raw = GetBacklight();
        return raw is int r ? (int)Math.Round(r * 100.0 / MaxBacklight) : null;
    }

    private (int ExitCode, string StdOut, string StdErr) Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adb!,
            WorkingDirectory = _workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            string outp = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            return (proc.HasExited ? proc.ExitCode : -1, outp, err);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "adb invocation failed.");
            return (-1, "", ex.Message);
        }
    }
}
