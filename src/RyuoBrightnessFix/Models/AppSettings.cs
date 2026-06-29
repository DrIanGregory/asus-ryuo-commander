using System.Text.Json;

namespace RyuoBrightnessFix.Models;

/// <summary>
/// GUI / startup / tray preferences. Persisted to
/// %APPDATA%\RyuoBrightnessFix\settings.json. Separate from <see cref="RyuoConfig"/>,
/// which holds the captured device command.
/// </summary>
public sealed class AppSettings
{
    // --- Startup & System Tray (matches the UI group) ---
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;

    // --- Brightness behaviour ---
    /// <summary>Reapply the target brightness automatically after the system resumes.</summary>
    public bool AutoFixOnResume { get; set; } = true;

    /// <summary>
    /// Fix the after-sleep dim by software-replugging the Ryuo device (disable/enable) instead of
    /// resending a command. This is the reliable fix for the Ryuo IV (an Android LCD) whose
    /// brightness isn't a replayable command. Requires admin.
    /// </summary>
    public bool RestartDeviceOnResume { get; set; } = true;

    /// <summary>The slider value; also the brightness restored after resume.</summary>
    public int TargetBrightnessPercent { get; set; } = 100;

    /// <summary>Path to the device command config (ryuo.json). Null = look next to the exe.</summary>
    public string? DeviceConfigPath { get; set; }

    // --- In-progress calibration draft (so captured/pasted bytes survive a restart) ---
    public string? CaptureHighHex { get; set; }
    public string? CaptureLowHex { get; set; }
    public int CaptureHighPercent { get; set; } = 100;
    public int CaptureLowPercent { get; set; } = 50;
    public ReportType CaptureReportType { get; set; } = ReportType.Output;
    public int? CaptureOffset { get; set; }

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

    /// <summary>Resolve the device-config path: explicit setting, else ryuo.json next to the exe.</summary>
    public string ResolveDeviceConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(DeviceConfigPath))
            return DeviceConfigPath!;
        return Path.Combine(AppConstants.ExeDir, AppConstants.DeviceConfigFileName);
    }
}
