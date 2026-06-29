using System.Diagnostics;
using System.Runtime.Versioning;
using RyuoBrightnessFix.Util;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Performs a "software replug" of the Ryuo IV: disable then re-enable its USB composite
/// device, forcing Windows (and ASUS Info Hub) to re-enumerate and re-initialize the LCD.
///
/// This mirrors the physical unplug/replug that is known to clear the after-sleep dimming —
/// without needing to know any brightness command. The AIO pump is driven by hardware PWM,
/// not this USB interface, so cycling the screen's USB does not affect cooling.
///
/// Requires Administrator (PnP enable/disable is privileged).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceCycler
{
    public const string RyuoVidPid = "VID_0B05&PID_1C76";
    private readonly ILogger _log;

    public DeviceCycler(ILogger log) => _log = log.ForContext<DeviceCycler>();

    public (bool Ok, string Message) RestartRyuo()
    {
        if (!AdminUtil.IsElevated())
            return (false, "Restarting the device needs administrator. Use 'Relaunch as admin' first.");

        // Target the composite parent (no &MI_) so all interfaces re-enumerate together.
        const string script =
            "$ErrorActionPreference='Stop';" +
            "$d=Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\\" + RyuoVidPid + "\\*' -and $_.InstanceId -notlike '*&MI_*' };" +
            "if(-not $d){ Write-Output 'NODEV'; exit 3 };" +
            "$d | Disable-PnpDevice -Confirm:$false;" +
            "Start-Sleep -Milliseconds 1500;" +
            "$d | Enable-PnpDevice -Confirm:$false;" +
            "Write-Output 'OK'";

        try
        {
            _log.Information("Software-replug: cycling the Ryuo USB device…");
            var (exit, stdout, stderr) = Run("powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-Command", script }, TimeSpan.FromSeconds(30));

            if (stdout.Contains("NODEV", StringComparison.Ordinal))
                return (false, "Ryuo device (VID_0B05/PID_1C76) not found.");
            if (exit == 0 && stdout.Contains("OK", StringComparison.Ordinal))
            {
                _log.Information("Software-replug complete — the LCD should re-initialize.");
                return (true, "Restarted the Ryuo device — the LCD re-initializes (give it a few seconds).");
            }

            var msg = (stderr + " " + stdout).Trim();
            _log.Error("Software-replug failed (exit {Exit}): {Msg}", exit, msg);
            return (false, "Restart failed: " + (msg.Length > 0 ? msg : $"exit {exit}"));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Software-replug threw.");
            return (false, "Restart error: " + ex.Message);
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        string outp = proc.StandardOutput.ReadToEnd();
        string err = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
        return (proc.HasExited ? proc.ExitCode : -1, outp, err);
    }
}
