using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using RyuoBrightnessFix.Models;
using RyuoBrightnessFix.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RyuoBrightnessFix.ViewModels;

/// <summary>
/// The single view model behind <c>MainWindow</c>. Sets the Ryuo IV LCD backlight over adb
/// (the actual fix), persists settings, and re-applies brightness on resume.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 500;

    private readonly ILogger _log;
    private readonly AppSettings _settings;
    private readonly StartupRegistrationService _startup;
    private readonly BacklightService _backlight;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly Queue<string> _logLines = new();

    private ResumeMonitor? _resumeMonitor;

    // Re-applies brightness on a short timer so the panel's firmware idle-dim (which fires
    // ~5 s after the last message from the PC) never kicks in. 3 s leaves a safe margin.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(3);
    private System.Threading.Timer? _keepAliveTimer;
    private int _keepAliveBusy;   // 0/1 guard so ticks never overlap on the device
    private int _keepAliveFailures;   // consecutive failed ticks, for throttled warnings

    // Wedge detection: a healthy panel streams input reports (~10/s). Writes that succeed
    // while the panel stays silent this long mean its firmware dropped its HID handle
    // (it does this whenever the host stops reading — app restart, PC sleep) and is
    // discarding everything; only a SerialService restart recovers it.
    private static readonly TimeSpan WedgeSilenceThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryRetryInterval = TimeSpan.FromMinutes(5);
    private readonly PanelRecoveryService _recovery;
    private readonly MediaService _media;
    private DateTime _lastRecoveryAttemptUtc = DateTime.MinValue;

    /// <summary>Raised when the tray icon visibility should change (App owns the tray).</summary>
    public event Action<bool>? TrayVisibilityRequested;

    public RelayCommand ApplyBrightnessCommand { get; }
    public RelayCommand Restore100Command { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand CopyVersionCommand { get; }
    public RelayCommand ChooseVideoCommand { get; }
    public RelayCommand SetVideoCommand { get; }

    public MainViewModel(ILogger log, UiLogSink uiSink, AppSettings settings, StartupRegistrationService startup,
        LoggingLevelSwitch levelSwitch)
    {
        _log = log.ForContext<MainViewModel>();
        _settings = settings;
        _startup = startup;
        _levelSwitch = levelSwitch;
        _backlight = new BacklightService(log);
        _recovery = new PanelRecoveryService(log);
        _media = new MediaService(log, _backlight);

        // Mirror persisted settings into bindable fields.
        _brightnessPercent = settings.TargetBrightnessPercent;
        _startWithWindows = startup.IsRegistered();   // reflect reality, not just the saved flag
        _startMinimized = settings.StartMinimized;
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _showTrayIcon = settings.ShowTrayIcon;
        _autoFixOnResume = settings.AutoFixOnResume;
        _keepBrightnessAlive = settings.KeepBrightnessAlive;
        _debugLogging = settings.DebugLogging;

        ApplyBrightnessCommand = new RelayCommand(() => ApplyBrightness(BrightnessPercent), () => CanControlDevice);
        Restore100Command = new RelayCommand(() => ApplyBrightness(100), () => CanControlDevice);
        ReloadCommand = new RelayCommand(RefreshDevice);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        CopyVersionCommand = new RelayCommand(CopyVersionToClipboard);
        ChooseVideoCommand = new RelayCommand(ChooseVideo, () => !VideoBusy);
        SetVideoCommand = new RelayCommand(SetVideo, () => CanSetVideo);

        uiSink.LineWritten += OnLogLine;
        _backlight.DeviceListChanged += OnDeviceListChanged;
        _backlight.SessionOpened += OnSessionOpened;

        _log.Debug("Settings on load: KeepBrightnessAlive={Keep}, AutoFixOnResume={Resume}, " +
                   "Target={Target}%, DebugLogging={Debug}.",
            _keepBrightnessAlive, _autoFixOnResume, _brightnessPercent, _debugLogging);

        RefreshDevice();
    }

    // ---------------------------------------------------------------- tabs & status

    private int _selectedTabIndex;
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }

    private DeviceStatusKind _status = DeviceStatusKind.NoDevice;
    public DeviceStatusKind Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string StatusText => Status.ToText();

    public System.Windows.Media.Brush StatusBrush
    {
        get
        {
            try
            {
                var color = (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(Status.ToColorHex());
                return new System.Windows.Media.SolidColorBrush(color);
            }
            catch { return System.Windows.Media.Brushes.Gray; }
        }
    }

    // ---------------------------------------------------------------- bindable state

    private int _brightnessPercent;
    public int BrightnessPercent
    {
        get => _brightnessPercent;
        set
        {
            if (SetProperty(ref _brightnessPercent, Math.Clamp(value, 0, 100)))
            {
                _settings.TargetBrightnessPercent = _brightnessPercent;
                SaveSettings();
            }
        }
    }

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value)) return;
            _startup.Set(value);
            _settings.StartWithWindows = value;
            SaveSettings();
        }
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set { if (SetProperty(ref _startMinimized, value)) { _settings.StartMinimized = value; SaveSettings(); } }
    }

    private bool _minimizeToTrayOnClose;
    public bool MinimizeToTrayOnClose
    {
        get => _minimizeToTrayOnClose;
        set { if (SetProperty(ref _minimizeToTrayOnClose, value)) { _settings.MinimizeToTrayOnClose = value; SaveSettings(); } }
    }

    private bool _showTrayIcon;
    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set
        {
            if (!SetProperty(ref _showTrayIcon, value)) return;
            _settings.ShowTrayIcon = value;
            SaveSettings();
            TrayVisibilityRequested?.Invoke(value);
        }
    }

    private bool _autoFixOnResume;
    public bool AutoFixOnResume
    {
        get => _autoFixOnResume;
        set
        {
            if (!SetProperty(ref _autoFixOnResume, value)) return;
            _settings.AutoFixOnResume = value;
            SaveSettings();
            RestartResumeMonitor();
        }
    }

    private bool _keepBrightnessAlive;
    public bool KeepBrightnessAlive
    {
        get => _keepBrightnessAlive;
        set
        {
            if (!SetProperty(ref _keepBrightnessAlive, value)) return;
            _settings.KeepBrightnessAlive = value;
            SaveSettings();
            UpdateKeepAlive();
        }
    }

    private bool _debugLogging;
    public bool DebugLogging
    {
        get => _debugLogging;
        set
        {
            if (!SetProperty(ref _debugLogging, value)) return;
            _settings.DebugLogging = value;
            SaveSettings();
            _levelSwitch.MinimumLevel = value ? LogEventLevel.Verbose : LogEventLevel.Information;
            _log.Information("Debug logging turned {State}. Minimum level is now {Level}.",
                value ? "ON" : "OFF", _levelSwitch.MinimumLevel);
        }
    }

    private string _deviceStatus = "Loading…";
    public string DeviceStatus { get => _deviceStatus; private set => SetProperty(ref _deviceStatus, value); }

    private bool _canControlDevice;
    public bool CanControlDevice
    {
        get => _canControlDevice;
        private set
        {
            if (SetProperty(ref _canControlDevice, value))
            {
                ApplyBrightnessCommand.RaiseCanExecuteChanged();
                Restore100Command.RaiseCanExecuteChanged();
                SetVideoCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanSetVideo));
            }
        }
    }

    private bool _sliderEnabled;
    public bool SliderEnabled { get => _sliderEnabled; private set => SetProperty(ref _sliderEnabled, value); }

    private string _configPathDisplay = "";
    public string ConfigPathDisplay { get => _configPathDisplay; private set => SetProperty(ref _configPathDisplay, value); }

    private string _logText = "";
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }

    // ---------------------------------------------------------------- version / status bar

    public string AppVersion => AppConstants.Version;

    private bool _versionFeedbackVisible;
    public bool VersionFeedbackVisible
    {
        get => _versionFeedbackVisible;
        private set => SetProperty(ref _versionFeedbackVisible, value);
    }

    private string _versionFeedbackText = "Copied";
    public string VersionFeedbackText
    {
        get => _versionFeedbackText;
        private set => SetProperty(ref _versionFeedbackText, value);
    }

    private System.Windows.Threading.DispatcherTimer? _versionFeedbackTimer;

    /// <summary>Copy the version to the clipboard and flash a short "Copied" confirmation.</summary>
    private void CopyVersionToClipboard()
    {
        try
        {
            // SetDataObject(copy: true) survives app exit and is less prone to the
            // transient CLIPBRD_E_CANT_OPEN failures of Clipboard.SetText.
            System.Windows.Clipboard.SetDataObject(AppVersion, true);
            VersionFeedbackText = "Copied";
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; tell the user instead of lying.
            _log.Warning(ex, "Could not copy the version to the clipboard.");
            VersionFeedbackText = "Copy failed";
        }
        ShowVersionFeedback();
    }

    private void ShowVersionFeedback()
    {
        if (_versionFeedbackTimer is null)
        {
            _versionFeedbackTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5),
            };
            _versionFeedbackTimer.Tick += (_, _) =>
            {
                _versionFeedbackTimer!.Stop();
                VersionFeedbackVisible = false;
            };
        }
        VersionFeedbackVisible = true;
        _versionFeedbackTimer.Stop();   // restart the window on rapid re-clicks
        _versionFeedbackTimer.Start();
    }

    // ---------------------------------------------------------------- operations

    /// <summary>Set the LCD backlight to a percent via adb, on a background thread.</summary>
    private void ApplyBrightness(int percent)
    {
        _log.Debug("ApplyBrightness({Percent}%) requested. CanControlDevice={Can}.", percent, CanControlDevice);
        if (!CanControlDevice)
        {
            _log.Warning("LCD not available; cannot set brightness (CanControlDevice is false).");
            return;
        }

        BrightnessPercent = percent;
        _log.Information("Setting LCD backlight to {Percent}%…", percent);
        Task.Run(() =>
        {
            try
            {
                var (ok, msg) = _backlight.SetPercent(percent);
                if (ok) _log.Information("Apply succeeded: {Msg}", msg);
                else _log.Error("Backlight set failed: {Msg}", msg);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Unexpected error applying brightness.");
            }
        });
    }

    /// <summary>Invoked by the tray "Restore brightness now" menu item.</summary>
    public void RestoreToTarget() => ApplyBrightness(BrightnessPercent);

    /// <summary>Open the folder holding the rolling log files in Explorer.</summary>
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppConstants.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppConstants.LogDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not open the logs folder ({Dir}).", AppConstants.LogDir);
        }
    }

    /// <summary>Refresh whether the Ryuo LCD is reachable over USB HID.</summary>
    private void RefreshDevice()
    {
        ConfigPathDisplay = "Direct USB HID control (ASUS VID 0B05 / PID 1C76).";

        try
        {
            if (_backlight.DeviceConnected())
            {
                CanControlDevice = true;
                SliderEnabled = true;
                DeviceStatus = "Ryuo IV LCD connected (USB HID).";
                _log.Information("LCD reachable over USB HID.");
                RestartResumeMonitor();
                UpdateKeepAlive();
            }
            else
            {
                CanControlDevice = false;
                SliderEnabled = false;
                DeviceStatus = "Ryuo IV LCD not found on USB (is it connected and powered?).";
                _log.Warning("Ryuo IV HID interface not found (VID 0B05 / PID 1C76 / MI_00).");
                StopResumeMonitor();
                UpdateKeepAlive();
            }
        }
        catch (Exception ex)
        {
            CanControlDevice = false;
            SliderEnabled = false;
            DeviceStatus = "HID error: " + ex.Message;
            _log.Error(ex, "Failed to query LCD over USB HID.");
        }
        finally
        {
            Status = CanControlDevice ? DeviceStatusKind.Connected : DeviceStatusKind.NoDevice;
        }
    }

    // ---------------------------------------------------------------- panel video

    private string? _selectedVideoPath;
    public string? SelectedVideoPath
    {
        get => _selectedVideoPath;
        private set
        {
            if (SetProperty(ref _selectedVideoPath, value))
            {
                OnPropertyChanged(nameof(SelectedVideoName));
                SetVideoCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedVideoName =>
        string.IsNullOrEmpty(_selectedVideoPath) ? "No video chosen" : Path.GetFileName(_selectedVideoPath);

    private bool _videoBusy;
    public bool VideoBusy
    {
        get => _videoBusy;
        private set
        {
            if (SetProperty(ref _videoBusy, value))
            {
                ChooseVideoCommand.RaiseCanExecuteChanged();
                SetVideoCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _videoStatus = "";
    public string VideoStatus { get => _videoStatus; private set => SetProperty(ref _videoStatus, value); }

    /// <summary>Choices for the scale-mode ComboBox.</summary>
    public IReadOnlyList<VideoScaleMode> VideoScaleModes { get; } =
        new[] { VideoScaleMode.Fill, VideoScaleMode.Fit, VideoScaleMode.Stretch };

    public VideoScaleMode SelectedVideoScaleMode
    {
        get => _settings.VideoScaleMode;
        set
        {
            if (_settings.VideoScaleMode == value) return;
            _settings.VideoScaleMode = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(VideoScaleModeDescription));
        }
    }

    /// <summary>One-line explanation of the selected scale mode, shown under the ComboBox.</summary>
    public string VideoScaleModeDescription => SelectedVideoScaleMode switch
    {
        VideoScaleMode.Fill => "Fill: scales the video up until it covers the whole screen, " +
                               "cropping whatever overflows. No bars, no distortion.",
        VideoScaleMode.Stretch => "Stretch: forces the video to the screen's shape. " +
                                  "No bars, but the image is distorted.",
        _ => "Fit: shows the whole video, with black bars where its shape differs from the screen.",
    };

    public string ActiveVideoDisplay =>
        string.IsNullOrEmpty(_settings.PanelVideoFile)
            ? "No panel video configured."
            : $"Active panel video: {_settings.PanelVideoFile} (re-asserted automatically)";

    public bool CanSetVideo =>
        CanControlDevice && !VideoBusy && !string.IsNullOrEmpty(SelectedVideoPath);

    private void ChooseVideo()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a video for the Ryuo IV panel",
            Filter = "Video files|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
        {
            SelectedVideoPath = dlg.FileName;
            VideoStatus = "";
        }
    }

    private async void SetVideo()
    {
        if (!CanSetVideo) return;
        var path = SelectedVideoPath!;

        if (!_media.FfmpegAvailable)
        {
            VideoStatus = "ffmpeg not found — see tools\\fetch-ffmpeg.ps1 (put ffmpeg.exe next to the app).";
            _log.Warning("Set video aborted: ffmpeg not found.");
            return;
        }
        if (!_media.AdbAvailable)
        {
            VideoStatus = "adb not found — install 'ASUS Info Hub - ROG RYUO IV'.";
            _log.Warning("Set video aborted: adb not found.");
            return;
        }

        VideoBusy = true;
        VideoStatus = "Starting…";
        _log.Information("Setting panel video from {Path}", path);
        try
        {
            var progress = new Progress<string>(s => VideoStatus = s);
            var (ok, msg, deviceName) = await _media.SetPanelVideoAsync(path, SelectedVideoScaleMode, progress);
            VideoStatus = msg;
            if (ok && deviceName is not null)
            {
                // Remember it so it survives panel reboots (the panel forgets its screen
                // config; OnSessionOpened re-asserts this file every time we reconnect).
                _settings.PanelVideoFile = deviceName;
                SaveSettings();
                OnPropertyChanged(nameof(ActiveVideoDisplay));
                _log.Information("Panel video set: {Msg}", msg);
            }
            else if (!ok)
            {
                _log.Error("Panel video failed: {Msg}", msg);
            }
        }
        catch (Exception ex)
        {
            VideoStatus = "Error: " + ex.Message;
            _log.Error(ex, "Unexpected error setting panel video.");
        }
        finally
        {
            VideoBusy = false;
        }
    }

    // ---------------------------------------------------------------- session re-assert

    private readonly object _reassertGate = new();
    private DateTime _lastReassertUtc = DateTime.MinValue;

    /// <summary>
    /// A new HID session opened (startup, self-heal, or the panel came back from a reboot or
    /// firmware recovery). The panel forgets its screen config across reboots — without this
    /// it sits on a black screen that looks "dead" at any brightness. Re-assert the saved
    /// video and the target brightness. May be raised under BacklightService's internal lock,
    /// so all work is queued; throttled because sessions can reopen in bursts.
    /// </summary>
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

                string? video = _settings.PanelVideoFile;
                if (!string.IsNullOrWhiteSpace(video))
                {
                    _log.Information("Session opened — re-asserting panel video {File} and {Percent}% brightness.",
                        video, BrightnessPercent);
                    _backlight.SetPanelVideo(video);
                }
                _backlight.SetPercent(BrightnessPercent, quiet: true);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Re-asserting panel state after session open failed.");
            }
        });
    }

    // ---------------------------------------------------------------- device hot-plug

    private System.Windows.Threading.DispatcherTimer? _deviceChangeDebounce;

    /// <summary>
    /// A HID device appeared or vanished somewhere on the machine. Devices flap while
    /// Windows re-enumerates a composite device, so debounce, then refresh only if the
    /// Ryuo's connected-state actually changed. Without this the app would sit dead
    /// after starting with the panel absent (or after the panel drops) until the user
    /// manually clicked "Re-check LCD".
    /// </summary>
    private void OnDeviceListChanged()
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            if (_deviceChangeDebounce is null)
            {
                _deviceChangeDebounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1000),
                };
                _deviceChangeDebounce.Tick += (_, _) =>
                {
                    _deviceChangeDebounce!.Stop();
                    try
                    {
                        bool present = _backlight.DeviceConnected();
                        if (present != CanControlDevice)
                        {
                            _log.Information("Ryuo IV {State} on USB; re-checking.",
                                present ? "appeared" : "disappeared");
                            RefreshDevice();
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Device hot-plug re-check failed.");
                    }
                };
            }
            _deviceChangeDebounce.Stop();
            _deviceChangeDebounce.Start();
        });
    }

    // ---------------------------------------------------------------- resume monitor

    private void RestartResumeMonitor()
    {
        StopResumeMonitor();
        if (!AutoFixOnResume) return;

        // On resume, restore the target brightness immediately (the keep-alive also
        // re-applies within a few seconds, but this gives a prompt kick on wake).
        // Read the slider value live so moving it takes effect without a restart.
        _resumeMonitor = new ResumeMonitor(10_000, _log,
            onResume: _ => _backlight.SetPercent(BrightnessPercent).Ok);
        _resumeMonitor.Start();
        _log.Information("Resume monitor ON (restore to {Target}% after wake).", BrightnessPercent);
    }

    private void StopResumeMonitor()
    {
        _resumeMonitor?.Dispose();
        _resumeMonitor = null;
    }

    // ---------------------------------------------------------------- keep-alive

    /// <summary>
    /// Start or stop the brightness keep-alive so it runs exactly when it should:
    /// the device is controllable AND the user has the option enabled. The hold opens a
    /// persistent HID session that continuously drains the device's input stream (which is
    /// what actually keeps the panel out of standby); the timer re-pushes the current
    /// brightness so slider changes take effect and the value stays applied.
    /// </summary>
    private void UpdateKeepAlive()
    {
        bool shouldRun = CanControlDevice && KeepBrightnessAlive;

        if (shouldRun && _keepAliveTimer is null)
        {
            _backlight.StartHold();   // open the read-draining session
            _keepAliveTimer = new System.Threading.Timer(KeepAliveTick, null, TimeSpan.Zero, KeepAliveInterval);
            _log.Information("Brightness hold ON (HID read-drain + re-apply every {Sec}s).",
                KeepAliveInterval.TotalSeconds);
        }
        else if (!shouldRun && _keepAliveTimer is not null)
        {
            _keepAliveTimer.Dispose();
            _keepAliveTimer = null;
            _backlight.StopHold();
            _log.Information("Brightness hold OFF.");
        }
    }

    private void KeepAliveTick(object? _)
    {
        // Skip if a previous tick (or an Apply) is still writing to the device.
        if (Interlocked.Exchange(ref _keepAliveBusy, 1) == 1) return;
        try
        {
            if (!CanControlDevice || !KeepBrightnessAlive) return;
            var (ok, msg) = _backlight.SetPercent(BrightnessPercent, quiet: true);
            if (ok)
            {
                NoteKeepAliveSuccess();
                CheckForWedgedPanel();
            }
            else
            {
                NoteKeepAliveFailure(msg);
            }
        }
        catch (Exception ex)
        {
            NoteKeepAliveFailure(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _keepAliveBusy, 0);
        }
    }

    // The tick fires every 3 s, so a dead panel would otherwise flood the log (or worse,
    // fail in total silence, as the pre-fix code did). Warn on the first failure and every
    // 100th after that (~5 min), and log recovery so the gap is visible in the log.
    private void NoteKeepAliveFailure(string message)
    {
        int n = ++_keepAliveFailures;
        if (n == 1 || n % 100 == 0)
            _log.Warning("Brightness keep-alive failing ({Count} consecutive tick(s)): {Msg}", n, message);
    }

    private void NoteKeepAliveSuccess()
    {
        int n = _keepAliveFailures;
        if (n > 0)
        {
            _keepAliveFailures = 0;
            _log.Information("Brightness keep-alive recovered after {Count} failed tick(s); " +
                             "holding at {Percent}% again.", n, BrightnessPercent);
        }
    }

    /// <summary>
    /// Writes succeeding while the panel sends nothing back = the firmware wedged its HID
    /// handle and is silently discarding our messages (it does this whenever the host stops
    /// reading its stream — app restarts, PC sleep). Restart its SerialService over ASUS's
    /// bundled adb; the gadget then re-enumerates and the session self-heal + hot-plug
    /// detection bring brightness back automatically. Runs on the keep-alive timer thread;
    /// the busy-guard keeps ticks from stacking behind the (seconds-long) adb call.
    /// </summary>
    private void CheckForWedgedPanel()
    {
        TimeSpan? silence = _backlight.TimeSinceLastInputReport;
        if (silence is null || silence < WedgeSilenceThreshold) return;
        if (DateTime.UtcNow - _lastRecoveryAttemptUtc < RecoveryRetryInterval) return;
        _lastRecoveryAttemptUtc = DateTime.UtcNow;

        _log.Warning("Panel looks wedged: brightness writes succeed but the panel has sent " +
                     "nothing for {Sec:F0}s. Restarting its SerialService over adb…",
            silence.Value.TotalSeconds);
        var (ok, msg) = _recovery.TryRestartSerialService();
        if (ok)
            _log.Information("Panel recovery started; brightness will re-apply automatically " +
                             "once the USB device re-enumerates.");
        else
            _log.Warning("Panel recovery failed: {Msg} Will retry in {Min} minutes.",
                msg, RecoveryRetryInterval.TotalMinutes);
    }

    // ---------------------------------------------------------------- logging plumbing

    private void OnLogLine(string message, LogEventLevel level)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            _logLines.Enqueue($"{DateTimeOffset.Now:HH:mm:ss} [{level.ToString()[..3].ToUpperInvariant()}] {message}");
            while (_logLines.Count > MaxLogLines) _logLines.Dequeue();

            var sb = new StringBuilder(_logLines.Count * 64);
            foreach (var line in _logLines) sb.AppendLine(line);
            LogText = sb.ToString();
        });
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { _log.Warning(ex, "Could not save settings."); }
    }

    // ---------------------------------------------------------------- window placement

    public sealed record WindowPlacement(double? Left, double? Top, double? Width, double? Height, bool Maximized);

    public WindowPlacement GetWindowPlacement()
        => new(_settings.WindowLeft, _settings.WindowTop, _settings.WindowWidth, _settings.WindowHeight, _settings.WindowMaximized);

    public void SaveWindowPlacement(WindowPlacement p)
    {
        _settings.WindowLeft = p.Left;
        _settings.WindowTop = p.Top;
        _settings.WindowWidth = p.Width;
        _settings.WindowHeight = p.Height;
        _settings.WindowMaximized = p.Maximized;
        SaveSettings();
    }

    public void Dispose()
    {
        _backlight.SessionOpened -= OnSessionOpened;
        _backlight.DeviceListChanged -= OnDeviceListChanged;
        _deviceChangeDebounce?.Stop();
        _deviceChangeDebounce = null;
        _versionFeedbackTimer?.Stop();
        _versionFeedbackTimer = null;
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
        StopResumeMonitor();
        _backlight.Dispose();
    }
}
