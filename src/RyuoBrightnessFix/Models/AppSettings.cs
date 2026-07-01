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
                if (loaded is not null) return loaded;
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
