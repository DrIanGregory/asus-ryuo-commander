using System.IO.Pipes;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// The config UI's side of the control channel to the panel service. Detects whether the service
/// owns the panel and, if so, sends it commands over the named pipe instead of driving the HID
/// directly — so the UI and the LocalSystem daemon never fight over the device.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceClient
{
    private readonly ILogger _log;

    public ServiceClient(ILogger log) => _log = log.ForContext<ServiceClient>();

    /// <summary>True when the panel service is registered with the SCM. When installed it owns the
    /// panel (auto-start), so the UI runs as a client regardless of the service's momentary state.</summary>
    public static bool IsServiceInstalled()
    {
        try
        {
            using var sc = new ServiceController(AppConstants.ServiceName);
            _ = sc.Status;   // throws InvalidOperationException if the service does not exist
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when the service is installed and currently running.</summary>
    public static bool IsServiceRunning()
    {
        try
        {
            using var sc = new ServiceController(AppConstants.ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    /// <summary>Ask the daemon to re-read settings.json and re-apply the panel now. Returns false
    /// if the service isn't reachable (the caller's settings write still stands; the daemon's
    /// periodic poll will pick it up).</summary>
    public bool Reload() => Send(PanelControlProtocol.CmdReload) == PanelControlProtocol.Ok;

    /// <summary>Fetch the daemon's live status, or null if the service isn't reachable.</summary>
    public ServiceStatusDto? GetStatus()
    {
        string? resp = Send(PanelControlProtocol.CmdStatus);
        if (string.IsNullOrWhiteSpace(resp) || resp.StartsWith("ERR", StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<ServiceStatusDto>(resp); }
        catch (Exception ex) { _log.Debug(ex, "Parsing service status failed."); return null; }
    }

    /// <summary>Fetch the current widget values (token → display string) the panel is showing, so
    /// the UI preview mirrors them, or null if the service isn't reachable.</summary>
    public IReadOnlyDictionary<string, string>? GetWidgetValues()
    {
        string? resp = Send(PanelControlProtocol.CmdWidgets);
        if (string.IsNullOrWhiteSpace(resp) || resp.StartsWith("ERR", StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(resp); }
        catch (Exception ex) { _log.Debug(ex, "Parsing widget values failed."); return null; }
    }

    private string? Send(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AppConstants.ControlPipeName, PipeDirection.InOut);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            writer.WriteLine(command);
            return reader.ReadLine();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Control command '{Command}' to the service failed.", command);
            return null;
        }
    }
}
