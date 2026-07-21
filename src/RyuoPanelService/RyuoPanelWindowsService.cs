using System.Runtime.Versioning;
using System.ServiceProcess;
using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoPanelService;

/// <summary>
/// The SCM-facing Windows Service. Classic <see cref="ServiceBase"/> (not the generic host) is
/// deliberate: this app lives or dies on resume handling, and <see cref="OnPowerEvent"/> is the
/// reliable way a session-0 service learns the machine woke — <c>Microsoft.Win32.SystemEvents</c>,
/// which the tray app used, does not fire dependably without an interactive message pump.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RyuoPanelWindowsService : ServiceBase
{
    private ILogger _log = Serilog.Core.Logger.None;
    private PanelDaemon? _daemon;
    private PipeControlServer? _pipe;

    public RyuoPanelWindowsService()
    {
        ServiceName = AppConstants.ServiceName;
        CanHandlePowerEvent = true;
        CanShutdown = true;
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        _log = ServiceLogging.CreateLogger().ForContext<RyuoPanelWindowsService>();
        _log.Information("===== {App} service {Version} starting. Log folder: {Dir} =====",
            AppConstants.DisplayName, AppConstants.Version, AppConstants.LogDir);

        // Build + start the daemon off the SCM thread so OnStart returns well within the SCM's
        // 30 s timeout even if the first panel assert is slow. Any failure is logged, not thrown —
        // the service stays up so device-poll can recover when the panel/driver settles.
        _daemon = new PanelDaemon(_log);
        _pipe = new PipeControlServer(_log, _daemon);
        _pipe.Start();   // listening is cheap; safe to start before the daemon finishes opening the panel
        Task.Run(() =>
        {
            try { _daemon.Start(); }
            catch (Exception ex) { _log.Fatal(ex, "Panel daemon failed to start."); }
        });
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeCritical:
                _log.Information("Power event: {Status} — restoring the panel.", powerStatus);
                Task.Run(() => _daemon?.OnResume());
                break;
            case PowerBroadcastStatus.Suspend:
                _log.Information("Power event: Suspend.");
                Task.Run(() => _daemon?.OnSuspend());
                break;
        }
        return true;
    }

    protected override void OnStop() => Shutdown("OnStop");

    protected override void OnShutdown() => Shutdown("OnShutdown");

    private void Shutdown(string reason)
    {
        try
        {
            _log.Information("Service stopping ({Reason}).", reason);
            _pipe?.Dispose();
            _pipe = null;
            _daemon?.Dispose();
            _daemon = null;
        }
        catch (Exception ex) { _log.Warning(ex, "Error during service shutdown."); }
        finally { Log.CloseAndFlush(); }
    }
}
