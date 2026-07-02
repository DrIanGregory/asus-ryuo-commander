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

    /// <summary>%APPDATA%\RyuoBrightnessFix</summary>
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>%APPDATA%\RyuoBrightnessFix\logs</summary>
    public static string LogDir => Path.Combine(AppDataDir, "logs");

    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>
    /// The app version from the assembly's informational version (the csproj
    /// &lt;Version&gt;), without any "+commit" SourceLink suffix. Never throws.
    /// </summary>
    public static string Version
    {
        get
        {
            try
            {
                var asm = typeof(AppConstants).Assembly;
                var info = asm
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info.IndexOf('+');
                    return plus > 0 ? info[..plus] : info;
                }
                return asm.GetName().Version?.ToString(3) ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
