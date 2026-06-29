using Serilog.Core;
using Serilog.Events;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// A Serilog sink that raises an event for each log line, so the GUI can show a live
/// activity log without coupling the services to WPF. The view model marshals to the
/// UI thread.
/// </summary>
public sealed class UiLogSink : ILogEventSink
{
    public event Action<string, LogEventLevel>? LineWritten;

    public void Emit(LogEvent logEvent)
    {
        var message = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
            message += " — " + logEvent.Exception.Message;
        LineWritten?.Invoke(message, logEvent.Level);
    }
}

public static class UiLogSinkExtensions
{
    public static Serilog.LoggerConfiguration UiSink(
        this Serilog.Configuration.LoggerSinkConfiguration cfg, UiLogSink sink)
        => cfg.Sink(sink);
}
