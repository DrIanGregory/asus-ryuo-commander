using System.Text.Json.Serialization;

namespace RyuoBrightnessFix.Models;

/// <summary>
/// The tiny line-based protocol the config UI uses to talk to the panel service over the
/// <see cref="AppConstants.ControlPipeName"/> named pipe. The client sends one command line and
/// reads one response line. Deliberately minimal: settings.json is the single source of truth
/// (only the UI writes it), so the only commands needed are "read live status" and "apply the
/// settings I just wrote now (don't wait for the poll)".
/// </summary>
public static class PanelControlProtocol
{
    /// <summary>Return a JSON <see cref="ServiceStatusDto"/> snapshot of the daemon's live state.</summary>
    public const string CmdStatus = "STATUS";

    /// <summary>Re-read settings.json and re-apply the panel state immediately. Response: "OK".</summary>
    public const string CmdReload = "RELOAD";

    /// <summary>Return the current formatted widget values (JSON token→display string) for the
    /// enabled metric slots, so the config UI's preview mirrors what the panel is showing.</summary>
    public const string CmdWidgets = "WIDGETS";

    /// <summary>Diagnostic: dump the full hardware/sensor tree the service currently sees (multi-line).</summary>
    public const string CmdSensors = "SENSORS";

    public const string Ok = "OK";
}

/// <summary>The daemon's live state, as sent over the pipe in response to
/// <see cref="PanelControlProtocol.CmdStatus"/>.</summary>
public sealed class ServiceStatusDto
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("panelReachable")] public bool PanelReachable { get; set; }
    [JsonPropertyName("brightness")] public int Brightness { get; set; }
    [JsonPropertyName("keepAlive")] public bool KeepAlive { get; set; }
    [JsonPropertyName("metricsEnabled")] public bool MetricsEnabled { get; set; }
    [JsonPropertyName("playlistCount")] public int PlaylistCount { get; set; }
    [JsonPropertyName("holdRunning")] public bool HoldRunning { get; set; }
    [JsonPropertyName("kernelSensors")] public bool KernelSensors { get; set; }
}
