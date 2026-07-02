using System.Text.Json;

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
    /// </summary>
    public List<string> PanelVideoFiles { get; set; } = new() { "RYUO_IV_HW_Info_01.mp4" };

    /// <summary>Playlist mode (firmware enum, from Info Hub's source): "Single" loops the
    /// first entry, "Cycle" plays the list in order, "Random" shuffles.</summary>
    public string PanelPlayMode { get; set; } = "Cycle";

    /// <summary>Metric widget title color (hex), part of the panel screen config.</summary>
    public string MetricTitleColor { get; set; } = "#25cfe5";

    /// <summary>Metric widget value color (hex), part of the panel screen config.</summary>
    public string MetricContentColor { get; set; } = "#25cfe5";

    /// <summary>How to fit a source video into the panel frame when setting a new video.
    /// Fill = crop to cover the screen (default), Fit = letterbox, Stretch = distort.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
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
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable settings should never block startup — fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
