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

    /// <summary>Named pipe the panel service exposes for the config UI (status + commands).</summary>
    public const string ControlPipeName = "RyuoBrightnessFix.Control";

    /// <summary>Windows Service name registered with the SCM.</summary>
    public const string ServiceName = "RyuoPanelService";

    /// <summary>
    /// Machine-wide data root: <c>%ProgramData%\RyuoBrightnessFix</c>. Deliberately NOT the
    /// per-user %APPDATA% — the panel daemon runs as the LocalSystem Windows Service and the
    /// config UI runs as the interactive user, and both must read/write the SAME settings,
    /// video cache and logs. CommonApplicationData resolves to C:\ProgramData for every
    /// account, so it is the one location both contexts share.
    /// </summary>
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

    /// <summary>The pre-service per-user location (<c>%APPDATA%\RyuoBrightnessFix</c>), migrated
    /// once into <see cref="AppDataDir"/> by <see cref="MigrateLegacyDataIfNeeded"/>.</summary>
    public static string LegacyAppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>%ProgramData%\RyuoBrightnessFix\logs</summary>
    public static string LogDir => Path.Combine(AppDataDir, "logs");

    /// <summary>
    /// One-time move of a pre-service install's per-user data (settings.json + the video cache)
    /// into the shared %ProgramData% root, so the user's existing playlist/brightness carry over
    /// when the service takes over. Must run in the interactive USER's context (only they can see
    /// their own %APPDATA%) — called from the app on startup and from the elevated installer.
    /// No-op once the shared settings.json exists. Never throws.
    /// </summary>
    public static void MigrateLegacyDataIfNeeded(Action<string>? log = null)
    {
        try
        {
            string shared = AppDataDir;
            string legacy = LegacyAppDataDir;
            string sharedSettings = Path.Combine(shared, SettingsFileName);
            string legacySettings = Path.Combine(legacy, SettingsFileName);

            // Already migrated (or a fresh install with no legacy data): nothing to do.
            if (File.Exists(sharedSettings) || !File.Exists(legacySettings)) return;
            if (string.Equals(Path.GetFullPath(shared), Path.GetFullPath(legacy),
                    StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(shared);
            File.Copy(legacySettings, sharedSettings, overwrite: false);

            string legacyCache = Path.Combine(legacy, "videocache");
            string sharedCache = Path.Combine(shared, "videocache");
            if (Directory.Exists(legacyCache) && !Directory.Exists(sharedCache))
            {
                Directory.CreateDirectory(sharedCache);
                foreach (var file in Directory.GetFiles(legacyCache))
                    File.Copy(file, Path.Combine(sharedCache, Path.GetFileName(file)), overwrite: false);
            }
            log?.Invoke($"Migrated settings + video cache from {legacy} to {shared}.");
        }
        catch (Exception ex)
        {
            log?.Invoke("Legacy data migration failed (continuing with defaults): " + ex.Message);
        }
    }

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
