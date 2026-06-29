using System.Runtime.Versioning;
using Microsoft.Win32;
using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Manages the "Start with Windows" behaviour via the per-user Run registry key
/// (HKCU\...\CurrentVersion\Run). Per-user means no elevation is required — ideal
/// for a tray app. The registered command launches the GUI (no args), which then
/// honours the StartMinimized / ShowTrayIcon settings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationService
{
    private readonly ILogger _log;

    public StartupRegistrationService(ILogger log) => _log = log.ForContext<StartupRegistrationService>();

    private static string ExePath => Environment.ProcessPath
        ?? Path.Combine(AppConstants.ExeDir, AppConstants.AppName + ".exe");

    public bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppConstants.RunRegistryPath, writable: false);
            return key?.GetValue(AppConstants.RunValueName) is not null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read startup registration.");
            return false;
        }
    }

    /// <summary>Idempotently set or clear the startup entry to match <paramref name="enabled"/>.</summary>
    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppConstants.RunRegistryPath, writable: true)
                ?? throw new InvalidOperationException("Could not open HKCU Run key.");

            if (enabled)
            {
                // Quote the path so spaces in the install dir are handled.
                key.SetValue(AppConstants.RunValueName, $"\"{ExePath}\"", RegistryValueKind.String);
                _log.Information("Enabled start with Windows -> {Exe}", ExePath);
            }
            else if (key.GetValue(AppConstants.RunValueName) is not null)
            {
                key.DeleteValue(AppConstants.RunValueName, throwOnMissingValue: false);
                _log.Information("Disabled start with Windows.");
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to {Action} startup registration.", enabled ? "enable" : "disable");
            return false;
        }
    }
}
