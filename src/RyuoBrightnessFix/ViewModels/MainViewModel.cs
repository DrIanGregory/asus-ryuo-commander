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
    public RelayCommand RemoveVideoCommand { get; }
    public RelayCommand MoveVideoUpCommand { get; }
    public RelayCommand MoveVideoDownCommand { get; }

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
        RemoveVideoCommand = new RelayCommand(RemovePlaylistItem, () => SelectedPlaylistItem is not null && !VideoBusy);
        MoveVideoUpCommand = new RelayCommand(() => MovePlaylistItem(-1), () => CanMovePlaylistItem(-1));
        MoveVideoDownCommand = new RelayCommand(() => MovePlaylistItem(+1), () => CanMovePlaylistItem(+1));
        foreach (var f in settings.PanelVideoFiles) Playlist.Add(f);

        uiSink.LineWritten += OnLogLine;
        _backlight.DeviceListChanged += OnDeviceListChanged;
        _backlight.SessionOpened += OnSessionOpened;

        _log.Debug("Settings on load: KeepBrightnessAlive={Keep}, AutoFixOnResume={Resume}, " +
                   "Target={Target}%, DebugLogging={Debug}.",
            _keepBrightnessAlive, _autoFixOnResume, _brightnessPercent, _debugLogging);

        InitializeMetrics();
        RefreshDevice();
        LoadActiveVideoPreview();
        RefreshStartupModeNote();
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
            RefreshStartupModeNote();
        }
    }

    private bool _startupElevatedTask;
    public string StartupModeNote
    {
        get
        {
            if (!_startWithWindows) return "";
            if (_startupElevatedTask)
                return "Autostart mode: as administrator (Task Scheduler, no UAC prompt) — " +
                       "full sensor access at every logon.";
            return "Autostart mode: standard (no admin). Run the app as administrator once " +
                   "with this ticked and it will auto-start as administrator from then on.";
        }
    }

    public bool ShowStartupModeNote => _startWithWindows;

    private void RefreshStartupModeNote()
    {
        // schtasks query takes ~100 ms — keep it off the UI thread.
        Task.Run(() =>
        {
            bool elevated = _startup.IsRegisteredElevated();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _startupElevatedTask = elevated;
                OnPropertyChanged(nameof(StartupModeNote));
                OnPropertyChanged(nameof(ShowStartupModeNote));
            });
        });
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
                OnPropertyChanged(nameof(HasSelectedVideo));
                SetVideoCommand.RaiseCanExecuteChanged();
                RaisePreviewChanged();
            }
        }
    }

    public bool HasSelectedVideo => !string.IsNullOrEmpty(_selectedVideoPath);

    // ------------------------------------------------ the LCD screen preview

    private string? _activeVideoLocalPath;
    /// <summary>Local playable copy of the video the panel is looping right now.</summary>
    public string? ActiveVideoLocalPath
    {
        get => _activeVideoLocalPath;
        private set { if (SetProperty(ref _activeVideoLocalPath, value)) RaisePreviewChanged(); }
    }

    /// <summary>What the LCD mock shows: the file being chosen, else the active panel video.</summary>
    public string? PreviewSource => SelectedVideoPath ?? ActiveVideoLocalPath;

    public bool HasPreview => PreviewSource is not null;

    public string PreviewTitle => SelectedVideoPath is not null
        ? $"LCD preview of the selected video — {SelectedVideoScaleMode} scale mode"
        : Playlist.Count > 1 && SelectedPlaylistItem is not null
            ? $"LCD preview — playlist entry: {SelectedPlaylistItem}"
            : "Now playing on the LCD";

    /// <summary>
    /// How the preview stretches into the LCD frame. A file being chosen simulates the scale
    /// mode that the transcode will bake in. The active video already has its mode baked into
    /// its 1920×960 pixels, and the panel stretches that frame to cover the screen (cropping
    /// ~3% of height) — UniformToFill reproduces exactly that.
    /// </summary>
    public System.Windows.Media.Stretch PreviewStretch => SelectedVideoPath is null
        ? System.Windows.Media.Stretch.UniformToFill
        : SelectedVideoScaleMode switch
        {
            VideoScaleMode.Fill => System.Windows.Media.Stretch.UniformToFill,
            VideoScaleMode.Stretch => System.Windows.Media.Stretch.Fill,
            _ => System.Windows.Media.Stretch.Uniform,
        };

    private void RaisePreviewChanged()
    {
        OnPropertyChanged(nameof(PreviewSource));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewStretch));
    }

    /// <summary>Fetch a local copy of a playlist video for the LCD mock (cache or adb pull):
    /// the highlighted playlist entry, else the first one.</summary>
    private void LoadActiveVideoPreview()
    {
        string? file = SelectedPlaylistItem ?? Playlist.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string? local = await _media.GetLocalCopyAsync(file);
                if (local is null) return;
                Application.Current?.Dispatcher.BeginInvoke(() => ActiveVideoLocalPath = local);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Loading the playlist-video preview failed.");
            }
        });
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

    // ------------------------------------------------ playlist

    public System.Collections.ObjectModel.ObservableCollection<string> Playlist { get; } = new();

    private string? _selectedPlaylistItem;
    public string? SelectedPlaylistItem
    {
        get => _selectedPlaylistItem;
        set
        {
            if (SetProperty(ref _selectedPlaylistItem, value))
            {
                RemoveVideoCommand.RaiseCanExecuteChanged();
                MoveVideoUpCommand.RaiseCanExecuteChanged();
                MoveVideoDownCommand.RaiseCanExecuteChanged();
                RaisePreviewChanged();
                LoadActiveVideoPreview();   // preview the highlighted playlist entry
            }
        }
    }

    public string ActiveVideoDisplay => Playlist.Count switch
    {
        0 => "Playlist is empty — the panel has nothing to show.",
        1 => $"Playing on the LCD: {Playlist[0]} (looped, re-asserted automatically)",
        _ => $"Playlist: {Playlist.Count} videos, rotated by the panel " +
             "(shuffled — the firmware has no in-order mode). Re-asserted automatically.",
    };

    /// <summary>"Single" loops one video; "Random" is the firmware's only multi-video rotation.</summary>
    private string CurrentPlayMode => Playlist.Count > 1 ? "Random" : "Single";

    private void SavePlaylist()
    {
        _settings.PanelVideoFiles = Playlist.ToList();
        _settings.PanelPlayMode = CurrentPlayMode;
        SaveSettings();
        OnPropertyChanged(nameof(ActiveVideoDisplay));
    }

    /// <summary>Send the whole playlist + widget slots to the panel in the background.</summary>
    private void ApplyPlaylist()
    {
        var files = Playlist.ToList();
        if (files.Count == 0) return;
        string mode = CurrentPlayMode;
        string[] slots = EffectiveMetricSlots();
        Task.Run(() =>
        {
            try
            {
                var (ok, msg) = _backlight.SetPanelPlaylist(files, mode, slots);
                if (!ok) _log.Warning("Applying the playlist failed: {Msg}", msg);
            }
            catch (Exception ex) { _log.Warning(ex, "Applying the playlist failed."); }
        });
    }

    private void RemovePlaylistItem()
    {
        if (SelectedPlaylistItem is null) return;
        int index = Playlist.IndexOf(SelectedPlaylistItem);
        if (index < 0) return;
        Playlist.RemoveAt(index);
        SelectedPlaylistItem = Playlist.Count > 0 ? Playlist[Math.Min(index, Playlist.Count - 1)] : null;
        SavePlaylist();
        ApplyPlaylist();
    }

    private bool CanMovePlaylistItem(int delta)
    {
        if (VideoBusy || SelectedPlaylistItem is null) return false;
        int index = Playlist.IndexOf(SelectedPlaylistItem);
        int target = index + delta;
        return index >= 0 && target >= 0 && target < Playlist.Count;
    }

    private void MovePlaylistItem(int delta)
    {
        if (SelectedPlaylistItem is null) return;
        int index = Playlist.IndexOf(SelectedPlaylistItem);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Playlist.Count) return;
        Playlist.Move(index, target);
        MoveVideoUpCommand.RaiseCanExecuteChanged();
        MoveVideoDownCommand.RaiseCanExecuteChanged();
        SavePlaylist();
        ApplyPlaylist();
    }

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
            var (ok, msg, deviceName) = await _media.PrepareVideoAsync(path, SelectedVideoScaleMode, progress);
            if (ok && deviceName is not null)
            {
                // Add to the playlist and activate it; persisted so it survives panel
                // reboots (OnSessionOpened re-asserts the whole playlist on reconnect).
                Playlist.Add(deviceName);
                SavePlaylist();
                ApplyPlaylist();
                VideoStatus = Playlist.Count == 1
                    ? "The panel is now playing your video."
                    : $"Added to the playlist ({Playlist.Count} videos — the panel rotates them).";
                _log.Information("Playlist updated: {Msg}", msg);

                // Flip the LCD mock to the new entry (the transcode is already in the cache).
                SelectedVideoPath = null;
                SelectedPlaylistItem = deviceName;
            }
            else
            {
                VideoStatus = msg;
                _log.Error("Adding the panel video failed: {Msg}", msg);
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

    // ---------------------------------------------------------------- metrics

    /// <summary>One of the six metric widget slots on the panel.</summary>
    public sealed class MetricSlot : ObservableObject
    {
        private readonly Action _changed;
        private string _selected;

        public MetricSlot(int number, string selected, Action changed)
        {
            Number = number;
            _selected = selected;
            _changed = changed;
        }

        public int Number { get; }
        public string Selected
        {
            get => _selected;
            set { if (SetProperty(ref _selected, value)) _changed(); }
        }
    }

    private const string MetricNone = "(None)";

    // The widget vocabulary the panel firmware understands (extracted from the HomeUI apk),
    // plus "Fan Speed <header>" entries discovered from the motherboard once sensors open.
    private static readonly string[] MetricVocabulary =
    {
        MetricNone,
        "CPU Temperature", "CPU Usage", "CPU Load", "CPU Speed Average", "CPU Voltage",
        "GPU Temperature", "GPU Usage", "GPU Load", "GPU Speed", "GPU Frequency",
        "GPU Power", "GPU Voltage",
        "Memory Frequency", "Motherboard Temperature", "Date&Time",
        "Fan Speed AIO Pump", "Fan Speed CPU Fan",
    };

    public System.Collections.ObjectModel.ObservableCollection<string> MetricOptions { get; } = new();
    public IReadOnlyList<MetricSlot> MetricSlots { get; private set; } = Array.Empty<MetricSlot>();

    private SystemMetricsService? _metrics;
    private System.Threading.Timer? _metricsTimer;
    private int _metricsBusy;
    private int _metricsSendFailures;
    private System.Windows.Threading.DispatcherTimer? _slotPushDebounce;

    public bool MetricsEnabled
    {
        get => _settings.MetricsEnabled;
        set
        {
            if (_settings.MetricsEnabled == value) return;
            _settings.MetricsEnabled = value;
            SaveSettings();
            OnPropertyChanged();
            UpdateMetricsStreaming();
            PushScreenConfig();   // show/hide the widgets immediately
        }
    }

    public string MetricsAccessNote =>
        SystemMetricsService.HasKernelSensorAccess
            ? ""
            : "Running without administrator rights: CPU temperature/voltage, motherboard " +
              "temperature and fan speeds read 0. Loads, GPU, memory, disk and network still work. " +
              "Run the app as administrator for the full set.";

    public bool ShowMetricsAccessNote => !SystemMetricsService.HasKernelSensorAccess;

    private void InitializeMetrics()
    {
        foreach (var option in MetricVocabulary) MetricOptions.Add(option);

        var slots = new List<MetricSlot>(6);
        for (int i = 0; i < 6; i++)
        {
            string saved = i < _settings.MetricSlots.Length ? _settings.MetricSlots[i] : "";
            string selected = string.IsNullOrWhiteSpace(saved) ? MetricNone : saved;
            if (!MetricOptions.Contains(selected)) MetricOptions.Add(selected);
            slots.Add(new MetricSlot(i + 1, selected, OnMetricSlotChanged));
        }
        MetricSlots = slots;

        UpdateMetricsStreaming();
    }

    private void OnMetricSlotChanged()
    {
        _settings.MetricSlots = MetricSlots
            .Select(s => s.Selected == MetricNone ? "" : s.Selected)
            .ToArray();
        SaveSettings();

        // Debounce: picking six metrics shouldn't fire six config pushes at the panel.
        if (_slotPushDebounce is null)
        {
            _slotPushDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500),
            };
            _slotPushDebounce.Tick += (_, _) => { _slotPushDebounce!.Stop(); PushScreenConfig(); };
        }
        _slotPushDebounce.Stop();
        _slotPushDebounce.Start();
    }

    /// <summary>The sysinfoDisplay slots to send: the saved picks, or all-hidden when metrics are off.</summary>
    private string[] EffectiveMetricSlots()
        => _settings.MetricsEnabled ? _settings.MetricSlots : new[] { "", "", "", "", "", "" };

    /// <summary>Re-send the screen config (playlist + widget slots) in the background.</summary>
    private void PushScreenConfig() => ApplyPlaylist();

    private void UpdateMetricsStreaming()
    {
        if (_settings.MetricsEnabled && _metricsTimer is null)
        {
            _metricsTimer = new System.Threading.Timer(MetricsTick, null,
                TimeSpan.Zero, TimeSpan.FromSeconds(3));
            _log.Information("Metrics streaming ON (STATE 'all' snapshot every 3s).");
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
            if (!MetricsEnabled || !CanControlDevice) return;

            if (_metrics is null)
            {
                _metrics = new SystemMetricsService(_log);
                if (_metrics.EnsureOpen())
                    AddDiscoveredFanOptions(_metrics.GetFanNames());
            }
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
                if (n == 1 || n % 100 == 0)
                    _log.Warning("Metrics send failing ({Count} consecutive): {Msg}", n, msg);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Metrics tick failed (will retry next interval).");
        }
        finally
        {
            Interlocked.Exchange(ref _metricsBusy, 0);
        }
    }

    private void AddDiscoveredFanOptions(IReadOnlyList<string> fanNames)
    {
        if (fanNames.Count == 0) return;
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            foreach (var name in fanNames)
            {
                string option = "Fan Speed " + name;
                if (!MetricOptions.Contains(option)) MetricOptions.Add(option);
            }
        });
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

                var files = _settings.PanelVideoFiles;
                if (files.Count > 0)
                {
                    _log.Information("Session opened — re-asserting the playlist ({Count} video(s)), " +
                                     "metric slots and {Percent}% brightness.", files.Count, BrightnessPercent);
                    _backlight.SetPanelPlaylist(files, _settings.PanelPlayMode, EffectiveMetricSlots());
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
        _metricsTimer?.Dispose();
        _metricsTimer = null;
        _metrics?.Dispose();
        _metrics = null;
        _slotPushDebounce?.Stop();
        _slotPushDebounce = null;
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
