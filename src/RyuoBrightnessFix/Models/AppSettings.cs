using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyuoBrightnessFix.Models;

/// <summary>
/// GUI / startup / tray / brightness preferences, persisted to
/// %APPDATA%\RyuoBrightnessFix\settings.json.
/// </summary>
public sealed class AppSettings
{
    // --- Startup & System Tray ---
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;

    // --- Brightness ---
    /// <summary>Re-apply the target brightness automatically after the system resumes.</summary>
    public bool AutoFixOnResume { get; set; } = true;

    /// <summary>The slider value; also the brightness restored after resume.</summary>
    public int TargetBrightnessPercent { get; set; } = 100;

    /// <summary>
    /// Continuously re-apply the target brightness on a short timer. The panel's own
    /// firmware dims itself ~5 s after the last message from the PC (that's how it idles
    /// when ASUS Info Hub isn't streaming); the keep-alive resets that timer so the
    /// brightness actually holds. Without this, a single Apply reverts within seconds.
    /// </summary>
    public bool KeepBrightnessAlive { get; set; } = true;

    // --- Panel video ---
    /// <summary>Legacy single-video field (pre-playlist). Migrated into
    /// <see cref="PanelVideoFiles"/> on load; kept so old settings files still parse.</summary>
    public string? PanelVideoFile { get; set; }

    /// <summary>
    /// The playlist of device-side video file names the panel loops (in /sdcard/pcMedia, or
    /// stock preset names from /sdcard/pcMediaPreset). Re-asserted whenever the HID session
    /// reopens, because the panel forgets its screen config when it reboots and would
    /// otherwise sit on a black screen. Defaults to ASUS's stock hardware-info video.
    /// Since v1.9 this is a mirror of <see cref="PanelVideos"/> (kept in sync on every save
    /// so a rollback to an older build still finds its playlist); readers should prefer
    /// <see cref="PanelVideos"/>.
    /// </summary>
    public List<string> PanelVideoFiles { get; set; } = new() { "RYUO_IV_HW_Info_01.mp4" };

    /// <summary>
    /// The media library with provenance: each entry pairs the on-device file name with the
    /// source path and scale mode it was transcoded with, so a scale-mode change can
    /// re-encode the library from the originals. Null only while parsing a pre-1.9 settings
    /// file; <see cref="Load"/> migrates it from <see cref="PanelVideoFiles"/> (with unknown
    /// provenance) and never returns it null.
    /// </summary>
    public List<PanelVideoEntry>? PanelVideos { get; set; }

    /// <summary>Playlist mode (firmware enum, from Info Hub's source): "Single" loops the
    /// first entry, "Cycle" plays the list in order, "Random" shuffles.</summary>
    public string PanelPlayMode { get; set; } = "Cycle";

    /// <summary>Metric widget title color (hex), part of the panel screen config.</summary>
    public string MetricTitleColor { get; set; } = "#25cfe5";

    /// <summary>Metric widget value color (hex), part of the panel screen config.</summary>
    public string MetricContentColor { get; set; } = "#25cfe5";

    /// <summary>How a source video is fitted into the panel frame: Fill = crop to cover the
    /// screen (default), Fit = letterbox, Stretch = distort. Applies to the whole library —
    /// changing it re-encodes every entry whose source file is recorded in
    /// <see cref="PanelVideos"/> (the mode is baked into the transcoded pixels).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoScaleMode VideoScaleMode { get; set; } = VideoScaleMode.Fill;

    // --- Metrics ---
    /// <summary>Stream live system metrics to the panel (the STATE 'all' telemetry Info Hub sends).</summary>
    public bool MetricsEnabled { get; set; }

    /// <summary>
    /// The six metric widget slots shown over the panel content (sysinfoDisplay tokens, e.g.
    /// "CPU Temperature", "Fan Speed AIO Pump", "Date&amp;Time"; empty = slot hidden).
    /// </summary>
    public string[] MetricSlots { get; set; } =
    {
        "CPU Temperature", "GPU Temperature", "CPU Usage",
        "Fan Speed AIO Pump", "Motherboard Temperature", "Date&Time",
    };

    /// <summary>
    /// Friendly labels for motherboard fan headers, keyed by the raw sensor name
    /// LibreHardwareMonitor reports (e.g. <c>{"Fan #7":"AIO Pump"}</c>). Boards like the ROG APEX
    /// expose fans generically as "Fan #1..N" with no header names, so this maps the ones you use
    /// to the names the panel widgets reference ("Fan Speed AIO Pump" etc.). Empty by default.
    /// </summary>
    public Dictionary<string, string> FanLabels { get; set; } = new();

    // --- Diagnostics ---
    /// <summary>
    /// When true, the logger captures full verbose/debug detail (adb command lines,
    /// exit codes, stdout/stderr, power-event timing). Off by default — turn on to
    /// diagnose, then off to keep the log readable.
    /// </summary>
    public bool DebugLogging { get; set; }

    // --- Window placement (restored on next launch) ---
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // Enums as their names, including the nullable PanelVideoEntry.ScaleMode
        // (a per-property [JsonConverter] attribute can't wrap a nullable enum).
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(AppConstants.AppDataDir, AppConstants.SettingsFileName);

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    // Migrate the pre-playlist single-video field.
                    if (!string.IsNullOrWhiteSpace(loaded.PanelVideoFile) &&
                        (loaded.PanelVideoFiles is null || loaded.PanelVideoFiles.Count == 0 ||
                         (loaded.PanelVideoFiles.Count == 1 &&
                          loaded.PanelVideoFiles[0] == "RYUO_IV_HW_Info_01.mp4" &&
                          loaded.PanelVideoFile != "RYUO_IV_HW_Info_01.mp4")))
                    {
                        loaded.PanelVideoFiles = new List<string> { loaded.PanelVideoFile };
                    }
                    loaded.PanelVideoFiles ??= new List<string>();
                    loaded.PanelVideoFile = null;
                    return Normalize(loaded);
                }
            }
        }
        catch
        {
            // Corrupt/unreadable settings should never block startup — fall back to defaults.
        }
        return Normalize(new AppSettings());
    }

    /// <summary>Establish the v1.9 invariant: <see cref="PanelVideos"/> is never null and
    /// <see cref="PanelVideoFiles"/> mirrors it. Pre-1.9 settings files (and fresh defaults)
    /// only carry the plain name list — migrate it into entries with unknown provenance.</summary>
    private static AppSettings Normalize(AppSettings s)
    {
        s.PanelVideoFiles ??= new List<string>();
        s.PanelVideos ??= s.PanelVideoFiles
            .Select(f => new PanelVideoEntry { File = f })
            .ToList();
        s.PanelVideos.RemoveAll(e => e is null || string.IsNullOrWhiteSpace(e.File));
        s.PanelVideoFiles = s.PanelVideos.Select(e => e.File).ToList();
        return s;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
