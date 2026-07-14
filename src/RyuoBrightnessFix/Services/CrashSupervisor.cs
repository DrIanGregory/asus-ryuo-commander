using System.Diagnostics;
using System.Runtime.Versioning;
using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Watchdog that keeps the tray app alive across crashes it cannot catch itself.
///
/// Why a separate supervisor process at all: the panel-killing crash is a native
/// <see cref="AccessViolationException"/> (0xC0000005) raised below managed code — e.g. NVIDIA's
/// NVML after a GPU driver reset. The CLR treats it as a corrupted-state exception and tears the
/// process down regardless of any try/catch or <c>AppDomain.UnhandledException</c> handler, so a
/// crashed instance can never restart itself. Restart-on-crash therefore needs a survivor process.
///
/// Windows Task Scheduler already restarts <i>this</i> supervisor if it dies (see
/// <see cref="StartupRegistrationService"/>), but its <c>RestartOnFailure</c> only supports a fixed
/// interval × count — it cannot express exponential backoff. This supervisor owns the GUI's
/// restart policy instead: relaunch with exponentially growing delays, and if the app is still
/// crash-looping after <see cref="GiveUpWindow"/>, stop hammering it and fail loudly.
///
/// Exit-code contract with the GUI: a clean user quit returns 0 (WPF <c>Application.Shutdown()</c>);
/// any crash exits non-zero (the CLR reports the native exception code). 0 ⇒ stop supervising;
/// non-zero ⇒ a crash to restart.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CrashSupervisor
{
    /// <summary>Argument that selects supervisor mode instead of the GUI. See <see cref="Program"/>.</summary>
    public const string Switch = "--supervise";

    /// <summary>Second-instance guard so two supervisors can't both babysit the same GUI.</summary>
    private const string SupervisorMutex = @"Global\RyuoBrightnessFix.Supervisor";

    /// <summary>Delay before the first restart; each subsequent restart doubles it.</summary>
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling on the backoff delay so a long crash-loop still retries a few times an hour.</summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// An instance that stayed up at least this long before crashing is treated as a fresh
    /// incident: the backoff and the give-up window reset. This is what keeps an occasional
    /// crash-after-hours (the NVML/TDR case) restarting promptly instead of ever escalating —
    /// only a genuine fast crash-loop grows the delay and can hit the give-up window.
    /// </summary>
    private static readonly TimeSpan HealthyRun = TimeSpan.FromSeconds(60);

    /// <summary>Give up (loudly) once the app has been crash-looping continuously for this long.</summary>
    private static readonly TimeSpan GiveUpWindow = TimeSpan.FromHours(2);

    public static int Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, SupervisorMutex, out bool createdNew);
        if (!createdNew)
            return 0;   // another supervisor already owns this GUI; nothing to do.

        var log = BuildLogger();
        string exe = Environment.ProcessPath
            ?? Path.Combine(AppConstants.ExeDir, AppConstants.AppName + ".exe");
        string workingDir = Path.GetDirectoryName(exe) ?? AppConstants.ExeDir;

        log.Information("===== Crash supervisor started for {Exe}. Backoff {Base}s→{Max}min, give up after {Hours}h of crash-looping. =====",
            exe, BaseDelay.TotalSeconds, MaxDelay.TotalMinutes, GiveUpWindow.TotalHours);

        int failures = 0;              // consecutive fast crashes in the current streak
        DateTime streakStart = default; // when the current crash-loop began

        while (true)
        {
            DateTime launchedAt = DateTime.UtcNow;
            int exitCode;
            try
            {
                exitCode = LaunchAndWait(exe, workingDir, log);
            }
            catch (Exception ex)
            {
                // Couldn't even start the child — the supervisor itself is broken. Exit non-zero
                // so Task Scheduler's RestartOnFailure gives the supervisor another go.
                log.Fatal(ex, "Crash supervisor could not launch the app; exiting so Task Scheduler retries the supervisor.");
                FailLoud(log, $"Could not launch {AppConstants.DisplayName}: {ex.Message}");
                return 1;
            }

            TimeSpan ranFor = DateTime.UtcNow - launchedAt;

            if (exitCode == 0)
            {
                // Clean quit (user chose Exit, or a second GUI instance deferred to the first).
                log.Information("App exited cleanly (code 0) after {Ran}. Supervisor stopping — no restart.",
                    Format(ranFor));
                return 0;
            }

            // --- crash path ---
            if (failures == 0 || ranFor >= HealthyRun)
            {
                // First crash, or it had been healthy for a while: start a fresh streak.
                failures = 0;
                streakStart = DateTime.UtcNow;
            }
            failures++;

            TimeSpan loopingFor = DateTime.UtcNow - streakStart;
            if (loopingFor >= GiveUpWindow)
            {
                log.Fatal("{App} has crashed {Count} times and been crash-looping for {Looping} " +
                          "(last exit code 0x{Code:X8}). Giving up — the supervisor will NOT restart it again. " +
                          "Fix the underlying fault and relaunch manually.",
                    AppConstants.DisplayName, failures, Format(loopingFor), (uint)exitCode);
                FailLoud(log,
                    $"{AppConstants.DisplayName} has been crash-looping for over " +
                    $"{GiveUpWindow.TotalHours:0} hours ({failures} crashes, last exit 0x{(uint)exitCode:X8}).\n\n" +
                    "Automatic restart has been given up. Please fix the fault and relaunch the app.");
                // Exit 0: this is a deliberate stop, not a supervisor failure — we do NOT want
                // Task Scheduler to restart the supervisor and resume hammering the app.
                return 0;
            }

            TimeSpan delay = NextDelay(failures);
            log.Warning("App crashed (exit 0x{Code:X8}) after {Ran}. Restart #{Count} in {Delay} " +
                        "(crash-looping for {Looping} of {Window} before giving up).",
                (uint)exitCode, Format(ranFor), failures, Format(delay), Format(loopingFor), Format(GiveUpWindow));

            Thread.Sleep(delay);
        }
    }

    /// <summary>Start the GUI child and block until it exits, returning its exit code.</summary>
    private static int LaunchAndWait(string exe, string workingDir, ILogger log)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDir,
            // No arguments → the child runs the GUI (only --supervise selects supervisor mode).
            // UseShellExecute=false so the child inherits this process's token: when Task Scheduler
            // launched the supervisor elevated, the GUI is elevated too (full sensor access) with
            // no UAC prompt.
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        log.Information("Launched {App} (pid {Pid}).", AppConstants.DisplayName, proc.Id);
        proc.WaitForExit();
        return proc.ExitCode;
    }

    /// <summary>Exponential backoff: BaseDelay · 2^(failures-1), capped at MaxDelay.</summary>
    private static TimeSpan NextDelay(int failures)
    {
        double seconds = BaseDelay.TotalSeconds * Math.Pow(2, failures - 1);
        if (double.IsInfinity(seconds) || seconds > MaxDelay.TotalSeconds)
            return MaxDelay;
        return TimeSpan.FromSeconds(seconds);
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.0}h"
        : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.0}m"
        : $"{t.TotalSeconds:0}s";

    /// <summary>
    /// Fail loudly per the "no silent skips" standard: the rolling log already has the record,
    /// so also drop a crash marker, write to the Windows event log, and show a message box. Every
    /// channel is best-effort and independently guarded — one failing must not swallow the alert.
    /// </summary>
    private static void FailLoud(ILogger log, string message)
    {
        try
        {
            var dir = AppConstants.LogDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.txt"),
                $"{DateTimeOffset.Now:O}  [SUPERVISOR GAVE UP]{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) { try { log.Warning(ex, "Writing crash.txt failed."); } catch { } }

        try
        {
            // Source "Application" always exists, so this never needs to create an event source
            // (which would require elevation the first time).
            EventLog.WriteEntry("Application",
                $"{AppConstants.DisplayName}: {message}", EventLogEntryType.Error);
        }
        catch (Exception ex) { try { log.Warning(ex, "Writing to the Windows event log failed."); } catch { } }

        try
        {
            System.Windows.Forms.MessageBox.Show(message, $"{AppConstants.DisplayName} — restart given up",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch (Exception ex) { try { log.Warning(ex, "Showing the give-up message box failed."); } catch { } }
    }

    /// <summary>
    /// A file-only logger writing to the same rolling ryuo-*.log the GUI uses, so the supervisor's
    /// restarts and give-up land in one unified timeline. (The GUI's logger adds the in-app pane
    /// sink and the runtime level switch; the supervisor has no UI and needs neither.)
    /// </summary>
    private static ILogger BuildLogger()
    {
        var logDir = AppConstants.LogDir;
        Directory.CreateDirectory(logDir);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDir, "ryuo-.log"), rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger()
            .ForContext("SourceContext", "CrashSupervisor");
    }
}
