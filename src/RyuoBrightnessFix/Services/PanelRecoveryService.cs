using System.Diagnostics;
using System.Runtime.Versioning;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Recovers the Ryuo IV's firmware when it wedges its own HID handle.
///
/// The panel firmware tries to send data to the PC every ~100 ms. Whenever the host stops
/// reading its HID input stream (the app exits or restarts, the PC sleeps), that send path
/// errors out, the firmware nulls its HID handle, and from then on it silently discards
/// every host message — brightness writes "succeed" on the PC but the panel stays in its
/// dim standby. The firmware never recovers on its own; its <c>SerialService</c> must be
/// restarted. Verified live: <c>am force-stop</c> + <c>am startservice</c> over the panel's
/// ADB interface (MI_01) un-wedges it, the gadget re-enumerates, and brightness applies again.
///
/// The Ryuo IV grants adb access as shipped, and ASUS Info Hub installs the <c>adb.exe</c>
/// this service uses. adb only drives the recovery; normal brightness control stays pure
/// USB-HID.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PanelRecoveryService
{
    private const string SerialServicePackage = "com.baiyi.service.serialservice.serialdataservice";
    private const string SerialServiceComponent = SerialServicePackage + "/.SerialService";

    private static readonly string[] AdbCandidates =
    {
        @"C:\Program Files\ASUS Info Hub - ROG RYUO IV\bin\adb.exe",
        @"C:\Program Files (x86)\ASUS Info Hub - ROG RYUO IV\bin\adb.exe",
    };

    private readonly ILogger _log;
    private bool _adbMissingLogged;

    public PanelRecoveryService(ILogger log) => _log = log.ForContext<PanelRecoveryService>();

    /// <summary>Full path to ASUS's bundled adb.exe, or null when Info Hub isn't installed.</summary>
    public string? FindAdb()
    {
        foreach (var candidate in AdbCandidates)
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* inaccessible path — try the next */ }
        }
        return null;
    }

    /// <summary>
    /// Restart the panel's SerialService over adb to un-wedge its HID handle. Blocking
    /// (several seconds); call from a background thread. Returns success plus a
    /// human-readable message. Never throws.
    /// </summary>
    public (bool Ok, string Message) TryRestartSerialService()
    {
        string? adb = FindAdb();
        if (adb is null)
        {
            if (!_adbMissingLogged)
            {
                _adbMissingLogged = true;
                _log.Warning("ASUS adb.exe not found (is 'ASUS Info Hub - ROG RYUO IV' installed?). " +
                             "Panel firmware recovery is unavailable; power-cycle the PC to recover the panel.");
            }
            return (false, "ASUS adb.exe not found; cannot restart the panel's SerialService.");
        }

        // One shell invocation so stop + start behave atomically from adb's point of view.
        var (ok, output) = RunAdb(adb,
            $"shell \"am force-stop {SerialServicePackage} && am startservice -n {SerialServiceComponent}\"",
            timeoutMs: 20_000);

        if (!ok)
            return (false, $"adb recovery failed: {output}");

        _log.Information("Panel SerialService restarted over adb; the USB gadget will re-enumerate shortly.");
        return (true, "Panel SerialService restarted.");
    }

    private (bool Ok, string Output) RunAdb(string adbPath, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                // adb.exe loads AdbWinApi.dll relative to its own folder; run from there.
                WorkingDirectory = Path.GetDirectoryName(adbPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "adb.exe failed to start.");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, $"adb timed out after {timeoutMs / 1000}s.");
            }

            string combined = (stdout + " " + stderr).Trim();
            if (proc.ExitCode != 0)
                return (false, $"adb exited {proc.ExitCode}: {combined}");
            return (true, combined);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Running adb for panel recovery failed.");
            return (false, ex.Message);
        }
    }
}
