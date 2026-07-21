using RyuoBrightnessFix.Models;
using Serilog;

namespace RyuoPanelService;

/// <summary>Builds the daemon's rolling-file logger, writing to the same shared
/// <c>%ProgramData%\RyuoBrightnessFix\logs\ryuo-*.log</c> the config UI reads.</summary>
internal static class ServiceLogging
{
    public static ILogger CreateLogger()
    {
        var logDir = AppConstants.LogDir;
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDir, "ryuo-.log"), rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return Log.Logger;
    }
}
