using System.Runtime.Versioning;
using RyuoBrightnessFix.Models;
using RyuoBrightnessFix.Services;
using Serilog;

namespace RyuoPanelService;

/// <summary>
/// The headless always-on panel daemon — the same job <c>MainViewModel</c>'s background loops did
/// inside the tray app, now owned by the Windows Service so the SCM keeps it alive across crashes,
/// logons and Windows Update. It:
/// <list type="bullet">
/// <item>holds the Ryuo IV backlight at the target brightness (read-drain + periodic re-apply,
/// because the panel firmware idle-dims ~5 s after the last host message);</item>
/// <item>streams live system metrics (the STATE 'all' snapshot) to the panel's widgets;</item>
/// <item>re-asserts the saved video playlist + widgets whenever the HID session (re)opens — the
/// panel forgets its screen config on every reboot/firmware recovery;</item>
/// <item>recovers the post-sleep HID wedge on resume (adb SerialService restart + fresh handle),
/// driven by <see cref="RyuoPanelWindowsService"/>'s <c>OnPowerEvent</c>;</item>
/// <item>watches for the panel appearing/disappearing on USB by polling (a session-0 service does
/// not get HidSharp's window-message hot-plug events).</item>
/// </list>
/// Settings live in the shared <c>%ProgramData%</c> root; the daemon reloads them when the config
/// UI rewrites the file, so brightness/playlist/metric changes apply without a restart.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PanelDaemon : IDisposable
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromSeconds(5);

    // Wedge detection (mirrors the tray app): writes that "succeed" while the panel streams
    // nothing back for this long mean the firmware dropped its HID handle — only an adb
    // SerialService restart recovers it, retried no more often than this.
    private static readonly TimeSpan WedgeSilenceThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryRetryInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger _log;
    private readonly BacklightService _backlight;
    private readonly PanelRecoveryService _recovery;
    private readonly object _sync = new();

    private SystemMetricsService? _metrics;
    private AppSettings _settings = new();
    private DateTime _settingsStampUtc = DateTime.MinValue;

    private Timer? _keepAliveTimer;
    private Timer? _metricsTimer;
    private Timer? _devicePollTimer;
    private int _keepAliveBusy, _metricsBusy, _devicePollBusy;
    private int _keepAliveFailures, _metricsSendFailures;
    private bool _deviceConnected;
    private bool _holdRunning;
    private DateTime _lastRecoveryAttemptUtc = DateTime.MinValue;

    private readonly object _reassertGate = new();
    private DateTime _lastReassertUtc = DateTime.MinValue;

    public PanelDaemon(ILogger log)
    {
        _log = log.ForContext<PanelDaemon>();
        _backlight = new BacklightService(log);
        _recovery = new PanelRecoveryService(log);
        _backlight.SessionOpened += OnSessionOpened;
    }

    /// <summary>True once <see cref="Start"/> has confirmed the panel is reachable over USB HID.</summary>
    public bool PanelReachable { get; private set; }

    /// <summary>
    /// Bring the daemon up. Loads settings, verifies the panel is reachable over HID (the key
    /// session-0 reachability check — logged loudly either way), applies the saved state, and
    /// starts the hold / metrics / device-poll loops. Returns false if the panel is not currently
    /// reachable; the service still runs (device-poll will pick the panel up when it appears).
    /// </summary>
    public bool Start()
    {
        ReloadSettings(force: true);

        bool present;
        try
        {
            present = _backlight.DeviceConnected();
        }
        catch (Exception ex)
        {
            _log.Fatal(ex, "HID reachability check threw. If this is an access/permission error it means the " +
                           "LocalSystem service cannot open the Ryuo IV panel from session 0 — the whole service " +
                           "design depends on this working. Investigate before relying on the service.");
            present = false;
        }

        PanelReachable = present;
        if (present)
            _log.Information("Panel reachable over USB HID from the service account — session-0 HID access confirmed.");
        else
            _log.Warning("Panel NOT reachable at startup (VID 0B05 / PID 1C76 / MI_00). Either it is not connected/" +
                         "powered, or session-0 HID access is blocked. Device-poll will keep retrying every {Sec}s.",
                DevicePollInterval.TotalSeconds);

        _deviceConnected = present;
        if (present) ApplyFullState("startup");

        _devicePollTimer = new Timer(DevicePollTick, null, DevicePollInterval, DevicePollInterval);
        return present;
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Reload settings from the shared file when it has changed (or on force), and
    /// return true if they were (re)loaded.</summary>
    private bool ReloadSettings(bool force = false)
    {
        try
        {
            string path = AppSettings.DefaultPath;
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (!force && stamp == _settingsStampUtc) return false;
            _settings = AppSettings.Load(path);
            _settingsStampUtc = stamp;
            if (!force)
                _log.Information("Settings changed on disk — reapplying (brightness {B}%, {N} video(s), metrics {M}).",
                    _settings.TargetBrightnessPercent, _settings.PanelVideoFiles.Count,
                    _settings.MetricsEnabled ? "on" : "off");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Reloading settings failed; keeping the current values.");
            return false;
        }
    }

    private string?[] EffectiveMetricSlots()
        => _settings.MetricsEnabled
            ? _settings.MetricSlots.Cast<string?>().ToArray()
            : new string?[] { "", "", "", "", "", "" };

    // ---------------------------------------------------------------- apply state

    /// <summary>Apply everything the panel should reflect right now: playlist + widgets, target
    /// brightness, the hold, and metrics streaming — matched to the current settings.</summary>
    private void ApplyFullState(string reason)
    {
        lock (_sync)
        {
            _log.Information("Applying panel state ({Reason}): {N} video(s) [{Mode}], {B}% brightness, metrics {M}.",
                reason, _settings.PanelVideoFiles.Count, _settings.PanelPlayMode,
                _settings.TargetBrightnessPercent, _settings.MetricsEnabled ? "on" : "off");

            ApplyPlaylist();
            SafeSetPercent(_settings.TargetBrightnessPercent, quiet: false);
            UpdateHold();
            UpdateMetrics();
        }
    }

    private void ApplyPlaylist()
    {
        var files = _settings.PanelVideoFiles;
        if (files.Count == 0) return;
        try
        {
            var (ok, msg) = _backlight.SetPanelPlaylist(files, _settings.PanelPlayMode, EffectiveMetricSlots(),
                _settings.MetricTitleColor, _settings.MetricContentColor);
            if (!ok) _log.Warning("Applying the playlist failed: {Msg}", msg);
        }
        catch (Exception ex) { _log.Warning(ex, "Applying the playlist failed."); }
    }

    private void SafeSetPercent(int percent, bool quiet)
    {
        try
        {
            var (ok, msg) = _backlight.SetPercent(percent, quiet);
            if (!ok && !quiet) _log.Warning("Setting brightness to {Percent}% failed: {Msg}", percent, msg);
        }
        catch (Exception ex) { if (!quiet) _log.Warning(ex, "Setting brightness failed."); }
    }

    // ---------------------------------------------------------------- hold (keep-alive)

    private void UpdateHold()
    {
        bool shouldRun = _deviceConnected && _settings.KeepBrightnessAlive;
        if (shouldRun && !_holdRunning)
        {
            _backlight.StartHold();
            _keepAliveTimer ??= new Timer(KeepAliveTick, null, TimeSpan.Zero, KeepAliveInterval);
            _holdRunning = true;
            _log.Information("Brightness hold ON (HID read-drain + re-apply every {Sec}s).", KeepAliveInterval.TotalSeconds);
        }
        else if (!shouldRun && _holdRunning)
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;
            _backlight.StopHold();
            _holdRunning = false;
            _log.Information("Brightness hold OFF.");
        }
    }

    private void KeepAliveTick(object? _)
    {
        if (Interlocked.Exchange(ref _keepAliveBusy, 1) == 1) return;
        try
        {
            if (!_deviceConnected || !_settings.KeepBrightnessAlive) return;
            var (ok, msg) = _backlight.SetPercent(_settings.TargetBrightnessPercent, quiet: true);
            if (ok)
            {
                if (_keepAliveFailures > 0)
                {
                    _log.Information("Brightness keep-alive recovered after {Count} failed tick(s).", _keepAliveFailures);
                    _keepAliveFailures = 0;
                }
                CheckForWedgedPanel();
            }
            else
            {
                int n = ++_keepAliveFailures;
                if (n == 1 || n % 100 == 0)
                    _log.Warning("Brightness keep-alive failing ({Count} consecutive): {Msg}", n, msg);
            }
        }
        catch (Exception ex) { _log.Warning(ex, "Keep-alive tick failed (will retry)."); }
        finally { Interlocked.Exchange(ref _keepAliveBusy, 0); }
    }

    /// <summary>Writes succeed but the panel has gone silent — the firmware wedged its HID handle.
    /// Restart its SerialService over adb (throttled); the poll + session re-assert bring it back.</summary>
    private void CheckForWedgedPanel()
    {
        TimeSpan? silence = _backlight.TimeSinceLastInputReport;
        if (silence is null || silence < WedgeSilenceThreshold) return;
        if (DateTime.UtcNow - _lastRecoveryAttemptUtc < RecoveryRetryInterval) return;
        _lastRecoveryAttemptUtc = DateTime.UtcNow;

        _log.Warning("Panel looks wedged: writes succeed but nothing received for {Sec:F0}s. Restarting SerialService…",
            silence.Value.TotalSeconds);
        var (ok, msg) = _recovery.TryRestartSerialService();
        if (!ok) _log.Warning("Panel recovery failed: {Msg} Retrying in {Min} min.", msg, RecoveryRetryInterval.TotalMinutes);
    }

    // ---------------------------------------------------------------- metrics

    private void UpdateMetrics()
    {
        if (_settings.MetricsEnabled && _metricsTimer is null)
        {
            _metricsTimer = new Timer(MetricsTick, null, TimeSpan.Zero, MetricsInterval);
            _log.Information("Metrics streaming ON (STATE 'all' snapshot every {Sec}s).", MetricsInterval.TotalSeconds);
        }
        else if (!_settings.MetricsEnabled && _metricsTimer is not null)
        {
            _metricsTimer.Dispose();
            _metricsTimer = null;
            _log.Information("Metrics streaming OFF.");
        }
    }

    private void MetricsTick(object? _)
    {
        if (Interlocked.Exchange(ref _metricsBusy, 1) == 1) return;
        try
        {
            if (!_settings.MetricsEnabled || !_deviceConnected) return;
            _metrics ??= new SystemMetricsService(_log);
            if (!_metrics.EnsureOpen()) return;

            string? json = _metrics.BuildAllJson();
            if (json is null) return;

            var (ok, msg) = _backlight.SendSysinfo(json);
            if (ok)
            {
                if (_metricsSendFailures > 0)
                {
                    _log.Information("Metrics stream recovered after {Count} failed send(s).", _metricsSendFailures);
                    _metricsSendFailures = 0;
                }
            }
            else
            {
                int n = ++_metricsSendFailures;
                if (n == 1 || n % 100 == 0) _log.Warning("Metrics send failing ({Count} consecutive): {Msg}", n, msg);
            }
        }
        catch (Exception ex) { _log.Warning(ex, "Metrics tick failed (will retry)."); }
        finally { Interlocked.Exchange(ref _metricsBusy, 0); }
    }

    // ---------------------------------------------------------------- device presence + settings poll

    /// <summary>
    /// Polls for the panel appearing/disappearing (a service gets no window-message hot-plug
    /// events) and for a settings change from the config UI. On a connect transition, re-applies
    /// full state; on a settings change while connected, re-applies too.
    /// </summary>
    private void DevicePollTick(object? _)
    {
        if (Interlocked.Exchange(ref _devicePollBusy, 1) == 1) return;
        try
        {
            bool present;
            try { present = _backlight.DeviceConnected(); }
            catch (Exception ex) { _log.Warning(ex, "Device presence poll failed."); return; }

            if (present != _deviceConnected)
            {
                _deviceConnected = present;
                _log.Information("Ryuo IV {State} on USB.", present ? "appeared" : "disappeared");
                if (present) { PanelReachable = true; ApplyFullState("device connected"); }
                else lock (_sync) { UpdateHold(); }   // tears the hold down until it returns
                return;
            }

            if (present && ReloadSettings()) ApplyFullState("settings changed");
        }
        catch (Exception ex) { _log.Warning(ex, "Device-poll tick failed (will retry)."); }
        finally { Interlocked.Exchange(ref _devicePollBusy, 0); }
    }

    // ---------------------------------------------------------------- session re-assert

    /// <summary>A fresh HID session opened (startup, self-heal, panel reboot/recovery). The panel
    /// forgets its screen config across reboots, so re-assert the playlist + brightness. Raised
    /// under BacklightService's write lock, so queue the work; throttled against session bursts.</summary>
    private void OnSessionOpened()
    {
        Task.Run(() =>
        {
            try
            {
                lock (_reassertGate)
                {
                    if (DateTime.UtcNow - _lastReassertUtc < TimeSpan.FromSeconds(15)) return;
                    _lastReassertUtc = DateTime.UtcNow;
                }

                var files = _settings.PanelVideoFiles;
                if (files.Count > 0)
                {
                    _log.Information("Session opened — re-asserting {Count} video(s), widgets and {B}% brightness.",
                        files.Count, _settings.TargetBrightnessPercent);
                    _backlight.SetPanelPlaylist(files, _settings.PanelPlayMode, EffectiveMetricSlots(),
                        _settings.MetricTitleColor, _settings.MetricContentColor);
                }
                _backlight.SetPercent(_settings.TargetBrightnessPercent, quiet: true);
            }
            catch (Exception ex) { _log.Warning(ex, "Re-asserting panel state after session open failed."); }
        });
    }

    // ---------------------------------------------------------------- resume (from OnPowerEvent)

    /// <summary>
    /// Restore the panel on system resume. Sleep wedges the panel firmware's own HID handle: after
    /// wake our writes are silently discarded until its SerialService is restarted over adb, so do
    /// that deterministically on every resume, give the firmware a moment to re-open hidg0, recycle
    /// the host handle, re-apply, and reset the sensor stack (its native GPU/driver handles are
    /// stale too). Called on the SCM's power-event thread.
    /// </summary>
    public void OnResume()
    {
        try
        {
            _log.Information("System resume — recovering the panel.");
            ReloadSettings();

            var (recovered, msg) = _recovery.TryRestartSerialService();
            if (recovered)
            {
                _log.Information("Resume: restarted the panel SerialService over adb to clear the post-sleep HID wedge.");
                Thread.Sleep(2500);   // am startservice is async; let the firmware re-open hidg0
            }
            else
            {
                _log.Warning("Resume: adb panel recovery unavailable ({Msg}); falling back to a HID handle recycle.", msg);
            }

            if (_settings.KeepBrightnessAlive && _deviceConnected && _backlight.RecycleHold())
                _log.Information("Resume: recycled the HID session for a fresh handle before re-applying.");

            _metrics?.Reset("system resume");
            SafeSetPercent(_settings.TargetBrightnessPercent, quiet: false);
        }
        catch (Exception ex) { _log.Error(ex, "Panel resume recovery failed."); }
    }

    // ---------------------------------------------------------------- control channel (named pipe)

    /// <summary>Live daemon state for the config UI's status display (serialized to the pipe).</summary>
    public string GetStatusJson()
    {
        lock (_sync)
        {
            var status = new
            {
                version = AppConstants.Version,
                panelReachable = _deviceConnected,
                brightness = _settings.TargetBrightnessPercent,
                keepAlive = _settings.KeepBrightnessAlive,
                metricsEnabled = _settings.MetricsEnabled,
                playlistCount = _settings.PanelVideoFiles.Count,
                holdRunning = _holdRunning,
                kernelSensors = SystemMetricsService.HasKernelSensorAccess,
            };
            return System.Text.Json.JsonSerializer.Serialize(status);
        }
    }

    /// <summary>The config UI wrote new settings and asked the daemon to apply them now, rather
    /// than waiting for the periodic settings poll.</summary>
    public void ApplyExternalReload()
    {
        _log.Information("Control channel: reload requested.");
        ReloadSettings(force: true);
        if (_deviceConnected) ApplyFullState("control: reload");
    }

    /// <summary>Sensors hold native driver handles that a suspend invalidates — drop them so the
    /// next metrics poll reopens cleanly.</summary>
    public void OnSuspend()
    {
        try { _metrics?.Reset("system suspend"); }
        catch (Exception ex) { _log.Warning(ex, "Sensor reset on suspend failed."); }
    }

    public void Dispose()
    {
        _devicePollTimer?.Dispose();
        _keepAliveTimer?.Dispose();
        _metricsTimer?.Dispose();
        _backlight.SessionOpened -= OnSessionOpened;
        try { _backlight.StopHold(); } catch { }
        _metrics?.Dispose();
        _backlight.Dispose();
    }
}
