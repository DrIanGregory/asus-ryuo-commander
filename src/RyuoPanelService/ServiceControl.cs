using System.Diagnostics;
using System.Runtime.Versioning;
using RyuoBrightnessFix.Models;

namespace RyuoPanelService;

/// <summary>
/// Installs / removes the panel service with the SCM (via <c>sc.exe</c>), including the recovery
/// policy that makes the Service Control Manager the supervisor: restart on failure with an
/// increasing delay — the OS-native version of the exponential backoff that a custom supervisor
/// was faking. Must be run from an elevated process.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceControl
{
    private const string DisplayName = "Ryuo Panel Service";

    public static int Install()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the service executable path.");

        // Carry the interactive user's pre-service settings/video cache into %ProgramData%
        // before the LocalSystem service (which can't see the user's %APPDATA%) first reads them.
        AppConstants.MigrateLegacyDataIfNeeded(Console.WriteLine);
        EnsureDataDirWritable();

        Console.WriteLine($"Installing '{AppConstants.ServiceName}' -> {exe}");
        var create = RunSc("create", AppConstants.ServiceName,
            "binPath=", exe,
            "start=", "delayed-auto",
            "obj=", "LocalSystem",
            "DisplayName=", DisplayName);
        if (create.Exit != 0)
        {
            // 1073 = already exists: reconfigure the binPath instead of failing.
            if (create.Exit == 1073)
            {
                Console.WriteLine("Service already exists — updating its configuration.");
                RunSc("config", AppConstants.ServiceName, "binPath=", exe, "start=", "delayed-auto", "obj=", "LocalSystem");
            }
            else
            {
                Console.Error.WriteLine($"sc create failed (exit {create.Exit}): {create.Output}");
                return create.Exit;
            }
        }

        RunSc("description", AppConstants.ServiceName,
            "Holds the ASUS Ryuo IV LCD backlight, video and metrics, and recovers it after sleep. Runs headless and is restarted by Windows if it stops.");

        // SCM recovery = restart on failure with a growing delay (5s, 30s, then 60s), counter
        // reset after a day of health. This replaces the custom crash supervisor.
        RunSc("failure", AppConstants.ServiceName,
            "reset=", "86400",
            "actions=", "restart/5000/restart/30000/restart/60000");
        // Also run recovery when the service stops with a non-zero exit even without a crash.
        RunSc("failureflag", AppConstants.ServiceName, "1");

        DisableLegacyTrayAutostart();

        Console.WriteLine("Starting the service…");
        var start = RunSc("start", AppConstants.ServiceName);
        if (start.Exit != 0)
            Console.Error.WriteLine($"sc start reported exit {start.Exit}: {start.Output.Trim()} " +
                                    "(check the log at " + AppConstants.LogDir + ").");
        else
            Console.WriteLine("Service started. It will now start automatically at every boot.");

        Console.WriteLine("Done. Check " + Path.Combine(AppConstants.LogDir, "ryuo-*.log") +
                          " for 'session-0 HID access confirmed'.");
        return 0;
    }

    public static int Uninstall()
    {
        Console.WriteLine($"Stopping and removing '{AppConstants.ServiceName}'…");
        RunSc("stop", AppConstants.ServiceName);
        var del = RunSc("delete", AppConstants.ServiceName);
        if (del.Exit != 0 && del.Exit != 1060 /* not installed */)
        {
            Console.Error.WriteLine($"sc delete failed (exit {del.Exit}): {del.Output}");
            return del.Exit;
        }
        Console.WriteLine("Service removed.");
        return 0;
    }

    /// <summary>Give the shared data root an ACL that lets the service (LocalSystem, already full)
    /// and the interactive users read/write — ProgramData subfolders otherwise inherit
    /// admin-only write. Best-effort.</summary>
    private static void EnsureDataDirWritable()
    {
        try
        {
            Directory.CreateDirectory(AppConstants.AppDataDir);
            // Grant Users modify on the tree so the config UI (unelevated) can write settings.
            RunProcess("icacls.exe", AppConstants.AppDataDir, "/grant", "*S-1-5-32-545:(OI)(CI)M", "/T", "/C", "/Q");
        }
        catch (Exception ex) { Console.WriteLine("Could not adjust data-folder permissions: " + ex.Message); }
    }

    /// <summary>Remove the old tray-app autostart so it doesn't also grab the HID and fight the
    /// service. The elevated Task Scheduler task and the HKCU Run value are both cleared.</summary>
    private static void DisableLegacyTrayAutostart()
    {
        try
        {
            RunProcess("schtasks.exe", "/Delete", "/TN", "RyuoBrightnessFix", "/F");
            RunProcess("reg.exe", "delete",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", "RyuoBrightnessFix", "/f");
            Console.WriteLine("Disabled the old tray-app autostart (the service owns the panel now).");
        }
        catch (Exception ex) { Console.WriteLine("Could not remove the old autostart: " + ex.Message); }
    }

    private static (int Exit, string Output) RunSc(params string[] args) => RunProcess("sc.exe", args);

    private static (int Exit, string Output) RunProcess(string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, $"{file} failed to start");
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, $"{file} timed out");
            }
            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
