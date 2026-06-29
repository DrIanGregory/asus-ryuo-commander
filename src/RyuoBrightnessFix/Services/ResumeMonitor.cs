using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Subscribes to <see cref="SystemEvents.PowerModeChanged"/> and, on
/// <see cref="PowerModes.Resume"/>, waits a configurable delay then invokes a
/// caller-supplied action (e.g. "restore brightness to target").
///
/// SystemEvents runs its own hidden message-window thread, so this works from a
/// console host AND from a WPF app without extra plumbing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ResumeMonitor : IDisposable
{
    private readonly int _resumeDelayMs;
    private readonly Func<CancellationToken, bool> _onResume;
    private readonly ILogger _log;

    // Serialize resume handling so overlapping events don't race the device.
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _subscribed;

    /// <param name="resumeDelayMs">Delay after resume before running the action.</param>
    /// <param name="onResume">Action to run on resume; returns true on success.</param>
    public ResumeMonitor(int resumeDelayMs, Func<CancellationToken, bool> onResume, ILogger log)
    {
        _resumeDelayMs = resumeDelayMs;
        _onResume = onResume;
        _log = log.ForContext<ResumeMonitor>();
    }

    public void Start()
    {
        if (_subscribed) return;
        _cts = new CancellationTokenSource();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _subscribed = true;
        _log.Information("Resume monitor active. Resume delay = {Delay} ms.", _resumeDelayMs);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        _log.Debug("PowerModeChanged: {Mode}", e.Mode);
        if (e.Mode != PowerModes.Resume)
            return;

        _log.Information("System resume detected. Applying fix in {Delay} ms.", _resumeDelayMs);

        var token = _cts?.Token ?? CancellationToken.None;
        Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    Task.Delay(_resumeDelayMs, token).Wait(token);
                    bool ok = _onResume(token);
                    if (ok) _log.Information("Post-resume fix applied successfully.");
                    else _log.Error("Post-resume fix FAILED. See log above.");
                }
                catch (OperationCanceledException)
                {
                    _log.Information("Post-resume handling cancelled (shutting down).");
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unexpected error during post-resume fix.");
                }
            }
        }, token);
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _subscribed = false;
        }
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
