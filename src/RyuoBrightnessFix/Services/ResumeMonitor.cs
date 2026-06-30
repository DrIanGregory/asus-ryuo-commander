using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Subscribes to <see cref="SystemEvents.PowerModeChanged"/> and bridges the two
/// power transitions we care about to caller-supplied actions:
///
/// <list type="bullet">
/// <item><see cref="PowerModes.Suspend"/> — fires just before the PC sleeps. The
/// LCD is still reachable over adb here, so we set the target brightness
/// <em>synchronously</em> (no delay) so the panel stays bright while asleep instead
/// of dropping to its minimum.</item>
/// <item><see cref="PowerModes.Resume"/> — fires after wake. We wait a configurable
/// delay (the device needs a moment to re-enumerate) then re-apply the target.</item>
/// </list>
///
/// SystemEvents runs its own hidden message-window thread, so this works from a
/// console host AND from a WPF app without extra plumbing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ResumeMonitor : IDisposable
{
    private readonly int _resumeDelayMs;
    private readonly Func<CancellationToken, bool>? _onResume;
    private readonly Func<bool>? _onSuspend;
    private readonly ILogger _log;

    // Serialize resume handling so overlapping events don't race the device.
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _subscribed;

    /// <param name="resumeDelayMs">Delay after resume before running the resume action.</param>
    /// <param name="onResume">
    /// Optional action to run on resume (after the delay); returns true on success.
    /// </param>
    /// <param name="onSuspend">
    /// Optional action to run synchronously just before sleep; returns true on success.
    /// Must be fast — Windows gives apps only a short window to react to suspend.
    /// </param>
    public ResumeMonitor(int resumeDelayMs, ILogger log,
        Func<CancellationToken, bool>? onResume = null, Func<bool>? onSuspend = null)
    {
        _resumeDelayMs = resumeDelayMs;
        _onResume = onResume;
        _onSuspend = onSuspend;
        _log = log.ForContext<ResumeMonitor>();
    }

    public void Start()
    {
        if (_subscribed) return;
        _cts = new CancellationTokenSource();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _subscribed = true;
        _log.Information(
            "Power monitor active. Restore-on-wake = {OnResume}, set-on-sleep = {OnSuspend}. Resume delay = {Delay} ms.",
            _onResume is not null, _onSuspend is not null, _resumeDelayMs);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        _log.Debug("PowerModeChanged: {Mode}", e.Mode);

        if (e.Mode == PowerModes.Suspend)
        {
            HandleSuspend();
            return;
        }

        if (e.Mode != PowerModes.Resume || _onResume is null)
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
                    bool ok = _onResume!(token);
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

    /// <summary>
    /// Runs the suspend action inline on the SystemEvents thread so the brightness
    /// write completes before the machine actually sleeps. Bounded and best-effort:
    /// if the device has already gone away we log and let the suspend proceed.
    /// </summary>
    private void HandleSuspend()
    {
        if (_onSuspend is null)
            return;

        _log.Information("System suspend detected. Setting brightness now so it stays bright while asleep.");
        try
        {
            bool ok = _onSuspend();
            if (ok) _log.Information("Pre-suspend brightness applied.");
            else _log.Warning("Pre-suspend brightness write did not confirm (device may be sleeping already).");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unexpected error during pre-suspend brightness write.");
        }
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
