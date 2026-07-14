using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Manages "Start with Windows" via two mechanisms:
/// <list type="bullet">
/// <item><b>HKCU Run key</b> — standard, no elevation needed, starts the app unelevated.</item>
/// <item><b>Task Scheduler logon task with highest privileges</b> — created automatically the
/// first time the app runs elevated with autostart enabled. From then on the app starts
/// <i>as administrator on every logon with no UAC prompt</i>, which is what full sensor
/// access (CPU temperature, fan RPM for the Metrics feature) needs. The Run key is removed
/// when the task exists so the app doesn't start twice.</item>
/// </list>
/// The task launches the exe in <c>--supervise</c> mode (see <see cref="CrashSupervisor"/>),
/// which owns the GUI's restart-with-exponential-backoff policy. The task's own
/// <c>RestartOnFailure</c> is the backstop that restarts the <i>supervisor</i> if it dies.
///
/// The task is registered from an XML definition rather than schtasks flags, because only
/// XML can express the settings that matter here: restart the supervisor if it dies (up to
/// 3 times, 1 minute apart), no execution time limit (the schtasks default kills the action after 72 h!),
/// normal process priority (the schtasks default runs actions BelowNormal, which stutters
/// the panel's video stream), and no battery conditions.
/// So: enable "Start with Windows", run the app as administrator once, and every restart
/// after that launches it elevated automatically.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationService
{
    private const string TaskName = "RyuoBrightnessFix";

    private readonly ILogger _log;

    public StartupRegistrationService(ILogger log) => _log = log.ForContext<StartupRegistrationService>();

    private static string ExePath => Environment.ProcessPath
        ?? Path.Combine(AppConstants.ExeDir, AppConstants.AppName + ".exe");

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public bool IsRegistered() => ElevatedTaskExists() || RunKeyExists();

    /// <summary>True when autostart goes through the elevated Task Scheduler task.</summary>
    public bool IsRegisteredElevated() => ElevatedTaskExists();

    /// <summary>Idempotently set or clear the startup registration to match <paramref name="enabled"/>.</summary>
    public bool Set(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (IsElevated)
                {
                    // Upgrade to the elevated logon task (creating/refreshing it needs admin,
                    // which we have right now) and drop the Run key so we don't start twice.
                    if (CreateOrUpdateElevatedTask())
                    {
                        DeleteRunKey(quiet: true);
                        return true;
                    }
                    return SetRunKey();   // task creation failed — fall back to the Run key
                }

                if (ElevatedTaskExists())
                {
                    // Already registered via the elevated task; leave it in charge.
                    return true;
                }
                return SetRunKey();
            }

            bool ok = DeleteRunKey(quiet: false);
            if (ElevatedTaskExists())
            {
                if (!DeleteElevatedTask())
                {
                    _log.Warning("The elevated autostart task could not be removed (needs admin). " +
                                 "Run the app as administrator and untick 'Start with Windows' to remove it.");
                    ok = false;
                }
            }
            return ok;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to {Action} startup registration.", enabled ? "enable" : "disable");
            return false;
        }
    }

    // ---------------------------------------------------------------- HKCU Run key

    private bool RunKeyExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppConstants.RunRegistryPath, writable: false);
            return key?.GetValue(AppConstants.RunValueName) is not null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read startup registration.");
            return false;
        }
    }

    private bool SetRunKey()
    {
        using var key = Registry.CurrentUser.CreateSubKey(AppConstants.RunRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open HKCU Run key.");

        // Quote the path so spaces in the install dir are handled. Always written with THIS
        // exe's path so a stale registration (e.g. an old build location) self-heals on every
        // enable/startup rather than launching the old binary. Launch via the crash supervisor
        // so an uncatchable native crash is restarted with backoff (see CrashSupervisor).
        string value = $"\"{ExePath}\" {CrashSupervisor.Switch}";
        string? existing = key.GetValue(AppConstants.RunValueName) as string;
        if (!string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(AppConstants.RunValueName, value, RegistryValueKind.String);
            _log.Information("Start with Windows -> {Exe}{Was}", ExePath,
                existing is null ? "" : $" (was {existing})");
        }
        return true;
    }

    private bool DeleteRunKey(bool quiet)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppConstants.RunRegistryPath, writable: true);
        if (key?.GetValue(AppConstants.RunValueName) is not null)
        {
            key.DeleteValue(AppConstants.RunValueName, throwOnMissingValue: false);
            if (!quiet) _log.Information("Disabled start with Windows (Run key removed).");
        }
        return true;
    }

    // ---------------------------------------------------------------- elevated logon task

    private bool ElevatedTaskExists()
    {
        var (exit, _, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return exit == 0;
    }

    private bool CreateOrUpdateElevatedTask()
    {
        // Register from XML (/F refreshes an existing task, so stale exe paths and old
        // task definitions self-heal on every elevated start). See the class comment for
        // why XML instead of /SC ONLOGON flags.
        string xmlPath = Path.Combine(Path.GetTempPath(), TaskName + "-task.xml");
        try
        {
            // Task Scheduler XML is conventionally UTF-16; the declaration must match the bytes.
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
            var (exit, _, err) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (exit == 0)
            {
                _log.Information("Start with Windows (as administrator, restart-on-crash) -> {Exe} " +
                                 "via Task Scheduler.", ExePath);
                return true;
            }
            _log.Error("Creating the elevated autostart task failed (schtasks exit {Exit}): {Err}",
                exit, err.Trim());
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Creating the elevated autostart task failed.");
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temp file left behind is harmless */ }
        }
    }

    private static string BuildTaskXml()
    {
        // Scope both the trigger and the principal to the current user: the task fires only
        // for this user's logon and runs in their interactive session with their highest
        // (admin) token — elevated, no UAC prompt.
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(ExePath);
        string exeDir = SecurityElement.Escape(Path.GetDirectoryName(ExePath) ?? AppConstants.ExeDir);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts {AppConstants.DisplayName} elevated at logon and restarts it if it crashes.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{CrashSupervisor.Switch}</Arguments>
                  <WorkingDirectory>{exeDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private bool DeleteElevatedTask()
    {
        var (exit, _, err) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (exit == 0)
        {
            _log.Information("Removed the elevated autostart task.");
            return true;
        }
        _log.Debug("Deleting the elevated autostart task failed (exit {Exit}): {Err}", exit, err.Trim());
        return false;
    }

    private (int Exit, string StdOut, string StdErr) RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "", "schtasks failed to start");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, stdout, "schtasks timed out");
            }
            return (proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Running schtasks failed.");
            return (-1, "", ex.Message);
        }
    }
}
