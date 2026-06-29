using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using RyuoBrightnessFix.Util;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Creates/removes a Windows Scheduled Task that runs this executable on resume.
///
/// The trigger is the canonical "machine woke from sleep" signal:
///   Log = System, Source = Microsoft-Windows-Power-Troubleshooter, EventID = 1.
/// We shell out to schtasks.exe with an XML definition (the only reliable way to
/// express an event trigger with an XPath query). Requires elevation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskInstaller
{
    public const string DefaultTaskName = "RyuoBrightnessFix-OnResume";

    private readonly ILogger _log;

    public ScheduledTaskInstaller(ILogger log) => _log = log.ForContext<ScheduledTaskInstaller>();

    /// <summary>
    /// Install the resume-triggered task. The action runs:
    ///   RyuoBrightnessFix.exe set-brightness-100 --config &lt;configPath&gt; --execute
    /// </summary>
    public bool Install(string exePath, string configPath, string taskName = DefaultTaskName, int resumeDelayMs = 10_000)
    {
        if (!AdminUtil.IsElevated())
        {
            _log.Error("install-task requires Administrator privileges. Re-run this command from an elevated terminal.");
            return false;
        }

        exePath = Path.GetFullPath(exePath);
        configPath = Path.GetFullPath(configPath);

        if (!File.Exists(exePath))
        {
            _log.Error("Executable not found: {Exe}", exePath);
            return false;
        }
        if (!File.Exists(configPath))
        {
            _log.Error("Config not found: {Config}", configPath);
            return false;
        }

        var xml = BuildTaskXml(exePath, configPath, resumeDelayMs);
        var xmlPath = Path.Combine(Path.GetTempPath(), $"{taskName}.xml");
        // Task Scheduler expects UTF-16 for /XML in practice; write with BOM.
        File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        try
        {
            _log.Information("Installing scheduled task '{Task}' (trigger: resume from sleep).", taskName);
            var result = RunSchtasks($"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F");
            if (result.ExitCode != 0)
            {
                _log.Error("schtasks failed (exit {Code}). {Output}", result.ExitCode, result.Output);
                return false;
            }

            _log.Information("Scheduled task installed. It will run on resume:");
            _log.Information("  {Exe} set-brightness-100 --config \"{Config}\" --execute", exePath, configPath);
            _log.Information("Verify with: schtasks /Query /TN \"{Task}\" /V /FO LIST", taskName);
            return true;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* best effort */ }
        }
    }

    public bool Uninstall(string taskName = DefaultTaskName)
    {
        if (!AdminUtil.IsElevated())
        {
            _log.Error("uninstall-task requires Administrator privileges. Re-run from an elevated terminal.");
            return false;
        }

        _log.Information("Removing scheduled task '{Task}'.", taskName);
        var result = RunSchtasks($"/Delete /TN \"{taskName}\" /F");
        if (result.ExitCode != 0)
        {
            _log.Error("schtasks delete failed (exit {Code}). {Output}", result.ExitCode, result.Output);
            return false;
        }
        _log.Information("Scheduled task '{Task}' removed.", taskName);
        return true;
    }

    /// <summary>
    /// Build a Task Scheduler v1.2 XML definition. The action arguments use the config's
    /// resume delay implicitly by passing --config; the task itself fires on the
    /// Power-Troubleshooter event. A small built-in delay is also expressed via the
    /// action arguments handled by set-brightness-100 (the app sleeps resumeDelayMs).
    /// </summary>
    private static string BuildTaskXml(string exePath, string configPath, int resumeDelayMs)
    {
        // schtasks XML must NOT contain a <Delay> on event triggers in some Windows builds;
        // we let the app perform the resume delay itself for portability. We pass the
        // config so the app reads resumeDelayMs from there.
        string args = $"set-brightness-100 --config \"{configPath}\" --execute";
        string workingDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;

        // XML-escape user-controlled path text.
        string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Restore ASUS ROG Ryuo IV AIO LCD brightness to 100% after the system resumes from sleep.</Description>
    <URI>\{Esc(DefaultTaskName)}</URI>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=1)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Esc(exePath)}</Command>
      <Arguments>{Esc(args)}</Arguments>
      <WorkingDirectory>{Esc(workingDir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    private (int ExitCode, string Output) RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start schtasks.exe");

        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        var output = (stdout + Environment.NewLine + stderr).Trim();
        return (proc.ExitCode, output);
    }
}
