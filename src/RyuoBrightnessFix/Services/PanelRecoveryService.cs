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
    private bool _adbDllMissingLogged;

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
    /// adb.exe's directories to add to the child process's DLL search path: its own folder plus
    /// the install root one level up. ASUS puts adb.exe in bin\ but AdbWinApi.dll in the parent,
    /// so both must be searchable for adb to load.
    /// </summary>
    private static IEnumerable<string> AdbDllSearchDirs(string adbPath)
    {
        string? dir = Path.GetDirectoryName(adbPath);
        if (dir is null) yield break;
        yield return dir;
        string? parent = Path.GetDirectoryName(dir);
        if (parent is not null) yield return parent;
    }

    /// <summary>Locate adb.exe's AdbWinApi.dll dependency in its folder or the install root, or null.</summary>
    private static string? FindAdbWinApi(string adbPath)
    {
        foreach (var dir in AdbDllSearchDirs(adbPath))
        {
            try
            {
                string candidate = Path.Combine(dir, "AdbWinApi.dll");
                if (File.Exists(candidate)) return candidate;
            }
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

        // ASUS's installer ships adb.exe in bin\ but its native dependency AdbWinApi.dll (and
        // AdbWinUsbApi.dll) in the install ROOT one level up. The Windows loader searches adb.exe's
        // own directory, never the parent, so adb dies with 0xC0000135 (DLL not found) and recovery
        // silently never works. Warn loudly if the dependency is genuinely absent — RunAdb still
        // puts the parent dir on PATH so the split layout resolves.
        if (FindAdbWinApi(adb) is null && !_adbDllMissingLogged)
        {
            _adbDllMissingLogged = true;
            _log.Warning("AdbWinApi.dll not found next to adb.exe ({AdbDir}) or in its parent folder. " +
                         "adb will fail to load; reinstall 'ASUS Info Hub - ROG RYUO IV' to restore it.",
                Path.GetDirectoryName(adb));
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

            // ASUS's split layout (adb.exe in bin\, AdbWinApi.dll in the parent) means the loader
            // can't find the DLL from adb.exe's own directory. Prepend both directories to the
            // child's PATH so the native dependency resolves either way; without this adb exits
            // 0xC0000135 and every recovery attempt fails silently.
            string dllDirs = string.Join(Path.PathSeparator.ToString(), AdbDllSearchDirs(adbPath));
            string existingPath = psi.Environment.TryGetValue("PATH", out var p) && !string.IsNullOrEmpty(p)
                ? p
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = existingPath.Length == 0
                ? dllDirs
                : dllDirs + Path.PathSeparator + existingPath;

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
