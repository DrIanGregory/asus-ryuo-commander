using System.Runtime.Versioning;
using System.ServiceProcess;
using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoPanelService;

/// <summary>
/// Entry point for the panel daemon. Modes by argument:
/// <list type="bullet">
/// <item><c>install</c> / <c>uninstall</c>: register/remove the Windows Service (run elevated).</item>
/// <item><c>console</c> / <c>--console</c>: run the daemon in the foreground for debugging (Ctrl+C to stop).</item>
/// <item>no arguments: the SCM launches it this way — run as a Windows Service.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].TrimStart('-', '/').ToLowerInvariant() : "";
        switch (mode)
        {
            case "install":
                return ServiceControl.Install();
            case "uninstall":
            case "remove":
                return ServiceControl.Uninstall();
            case "console":
            case "debug":
                return RunConsole();
            default:
                ServiceBase.Run(new RyuoPanelWindowsService());
                return 0;
        }
    }

    /// <summary>Foreground run for local debugging without installing — same daemon, Ctrl+C to stop.</summary>
    private static int RunConsole()
    {
        var log = ServiceLogging.CreateLogger().ForContext(typeof(Program));
        AppConstants.MigrateLegacyDataIfNeeded(m => log.Information("{Msg}", m));
        log.Information("Running the panel daemon in console mode (Ctrl+C to stop).");

        using var daemon = new PanelDaemon(log);
        using var pipe = new PipeControlServer(log, daemon);
        pipe.Start();
        daemon.Start();

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();

        log.Information("Console daemon stopping.");
        Log.CloseAndFlush();
        return 0;
    }
}
