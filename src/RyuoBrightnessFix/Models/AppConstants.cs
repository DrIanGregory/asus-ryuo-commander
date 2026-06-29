namespace RyuoBrightnessFix.Models;

/// <summary>Central home for app-wide string/path constants (no magic strings scattered around).</summary>
public static class AppConstants
{
    public const string AppName = "RyuoBrightnessFix";
    public const string DisplayName = "Ryuo Brightness Fix";

    /// <summary>HKCU run key used for "Start with Windows".</summary>
    public const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = AppName;

    public const string SingleInstanceMutex = @"Global\RyuoBrightnessFix.SingleInstance";

    public const string SettingsFileName = "settings.json";
    public const string DeviceConfigFileName = "ryuo.json";
    public const string ExampleConfigFileName = "ryuo.example.json";

    /// <summary>%APPDATA%\RyuoBrightnessFix</summary>
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string ExeDir => AppContext.BaseDirectory;
}
