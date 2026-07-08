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
        ChooseVideoCommand = new RelayCommand(AddVideo, () => !VideoBusy);
        RemoveVideoCommand = new RelayCommand(RemovePlaylistItem, () => SelectedPlaylistItem is not null && !VideoBusy);
        MoveVideoUpCommand = new RelayCommand(() => MovePlaylistItem(-1), () => CanMovePlaylistItem(-1));
        MoveVideoDownCommand = new RelayCommand(() => MovePlaylistItem(+1), () => CanMovePlaylistItem(+1));
        RecoverLegacyProvenance(settings);   // pre-1.9 entries: pull source paths from our own logs
        foreach (var e in settings.PanelVideos ?? new List<PanelVideoEntry>())
        {
            var item = new PlaylistItem(e.File, e.SourcePath, e.ScaleMode);
            Playlist.Add(item);
            LoadThumbnail(item);
        }

        uiSink.LineWritten += OnLogLine;
        _backlight.DeviceListChanged += OnDeviceListChanged;
        _backlight.SessionOpened += OnSessionOpened;

        _log.Debug("Settings on load: KeepBrightnessAlive={Keep}, AutoFixOnResume={Resume}, " +
                   "Target={Target}%, DebugLogging={Debug}.",
            _keepBrightnessAlive, _autoFixOnResume, _brightnessPercent, _debugLogging);

        InitializeMetrics();
        RefreshDevice();
        LoadPreviewVideo();
        RefreshStartupModeNote();

        // Converge the library with the persisted scale mode. Entries can lag behind the
        // setting when a mode change couldn't re-encode at the time (pre-1.9 data whose
        // provenance was only just recovered, a missing device, an app exit mid-change).
        // Only fires when something is actually re-encodable, so a fresh install with the
        // stock preset stays silent.
        if (Playlist.Any(p => p.BakedScaleMode != _settings.VideoScaleMode &&
                              p.SourcePath is not null && System.IO.File.Exists(p.SourcePath)))
            _ = ReencodeLibraryForScaleModeAsync();
    }

    /// <summary>
    /// Fill in missing source paths for library entries recorded before v1.9 tracked
    /// provenance, by scanning the app's own logs (the transcode step has always logged
    /// source → device-file). Recovered entries keep a null baked mode — "unknown" — so
    /// the convergence pass re-encodes them to the current scale mode. Persisted
    /// immediately so later launches skip the scan.
    /// </summary>
    private void RecoverLegacyProvenance(AppSettings settings)
    {
        try
        {
            var entries = settings.PanelVideos;
            if (entries is null) return;
            // Only our own timestamped uploads can appear in the transcode logs — stock
            // preset names (e.g. RYUO_IV_HW_Info_01.mp4) never will, so don't scan for them.
            var missing = entries
                .Where(e => e.SourcePath is null &&
                            System.Text.RegularExpressions.Regex.IsMatch(
                                e.File, @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3}\.mp4$"))
                .Select(e => e.File)
                .ToList();
            if (missing.Count == 0) return;

            var recovered = MediaService.RecoverSourcePathsFromLogs(AppConstants.LogDir, missing, _log);
            if (recovered.Count == 0) return;

            foreach (var e in entries)
            {
                if (e.SourcePath is null && recovered.TryGetValue(e.File, out var src))
                    e.SourcePath = src;
            }
            SaveSettings();
            _log.Information("Recovered source paths for {Count} pre-1.9 library entrie(s) from the logs.",
                recovered.Count);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Legacy provenance recovery failed; those entries need re-adding to re-scale.");
        }
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

    // ---------------------------------------------------------------- panel video / media library

    /// <summary>One entry in the media library strip: an on-device video + its thumbnail.
    /// Immutable apart from the thumbnail — a scale-mode re-encode replaces the whole item.</summary>
    public sealed class PlaylistItem : ObservableObject
    {
        public PlaylistItem(string file, string? sourcePath = null, VideoScaleMode? bakedScaleMode = null)
        {
            File = file;
            SourcePath = sourcePath;
            BakedScaleMode = bakedScaleMode;
        }

        /// <summary>Device-side file name (pcMedia, or a stock preset name).</summary>
        public string File { get; }

        /// <summary>The local file this entry was transcoded from; null when unknown (a stock
        /// preset, or added before v1.9 tracked provenance). Entries without a usable source
        /// cannot be re-encoded when the scale mode changes — only re-added.</summary>
        public string? SourcePath { get; }

        /// <summary>The scale mode baked into the device file at transcode time; null when unknown.</summary>
        public VideoScaleMode? BakedScaleMode { get; }

        private string? _thumbnail;
        /// <summary>Local PNG path for the strip; null while extraction is pending.</summary>
        public string? Thumbnail
        {
            get => _thumbnail;
            set => SetProperty(ref _thumbnail, value);
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<PlaylistItem> Playlist { get; } = new();

    private PlaylistItem? _selectedPlaylistItem;
    public PlaylistItem? SelectedPlaylistItem
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
                LoadPreviewVideo();
                if (IsRepeatOne) ApplyPlaylist();   // repeat-one follows the selection
            }
        }
    }

    // ------------------------------------------------ the LCD screen preview

    private string? _previewLocalPath;
    /// <summary>Local playable copy of the previewed playlist entry.</summary>
    public string? PreviewSource
    {
        get => _previewLocalPath;
        private set { if (SetProperty(ref _previewLocalPath, value)) RaisePreviewChanged(); }
    }

    public bool HasPreview => PreviewSource is not null;

    public string PreviewTitle => SelectedPlaylistItem is not null
        ? $"LCD preview — {SelectedPlaylistItem.File}"
        : "Now playing on the LCD";

    private void RaisePreviewChanged()
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(PreviewTitle));
    }

    /// <summary>Fetch a local copy of a playlist video for the LCD mock (cache or adb pull):
    /// the highlighted entry, else the first one.</summary>
    private void LoadPreviewVideo()
    {
        string? file = SelectedPlaylistItem?.File ?? Playlist.FirstOrDefault()?.File;
        if (string.IsNullOrWhiteSpace(file)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string? local = await _media.GetLocalCopyAsync(file);
                if (local is null) return;
                Application.Current?.Dispatcher.BeginInvoke(() => PreviewSource = local);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Loading the playlist-video preview failed.");
            }
        });
    }

    private void LoadThumbnail(PlaylistItem item)
    {
        if (item.Thumbnail is not null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                string? thumb = await _media.GetThumbnailAsync(item.File);
                if (thumb is null) return;
                Application.Current?.Dispatcher.BeginInvoke(() => item.Thumbnail = thumb);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Thumbnail load for {File} failed.", item.File);
            }
        });
    }

    private bool _videoBusy;
    public bool VideoBusy
    {
        get => _videoBusy;
        private set
        {
            if (SetProperty(ref _videoBusy, value))
            {
                ChooseVideoCommand.RaiseCanExecuteChanged();
                RemoveVideoCommand.RaiseCanExecuteChanged();
                MoveVideoUpCommand.RaiseCanExecuteChanged();
                MoveVideoDownCommand.RaiseCanExecuteChanged();
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
            // The mode is baked into the transcoded pixels, so a change must re-encode
            // the library — otherwise the ComboBox silently does nothing (the v1.8 bug).
            _ = ReencodeLibraryForScaleModeAsync();
        }
    }

    // ------------------------------------------------ scale-mode re-encode

    private int _scaleModeReencodeActive;   // 0/1: one runner at a time (it converges on its own)

    /// <summary>
    /// Re-encode the media library after a scale-mode change. Every entry whose recorded
    /// source file is still on disk is re-transcoded with the new mode, uploaded under a
    /// fresh name, and swapped into the playlist in place (the replaced upload and its
    /// cache copies are deleted). Entries without a usable source — stock presets, pre-1.9
    /// entries, moved files — are reported; they can only be re-added. Safe against rapid
    /// ComboBox flipping: a single runner re-reads the current setting before each video,
    /// so it always converges on the latest choice.
    /// </summary>
    private async Task ReencodeLibraryForScaleModeAsync()
    {
        if (Interlocked.Exchange(ref _scaleModeReencodeActive, 1) == 1) return;
        try
        {
            // Let an in-flight Add finish rather than running two ffmpeg/adb pipelines at
            // once (the ComboBox stays enabled while the library is busy).
            while (VideoBusy) await Task.Delay(250);

            if (Playlist.Count > 0 && Playlist.All(p => p.BakedScaleMode == _settings.VideoScaleMode))
            {
                VideoStatus = $"Scale mode {_settings.VideoScaleMode} — the library already matches; " +
                              "new videos will use it too.";
                return;
            }
            bool anyReencodable = Playlist.Any(p =>
                p.BakedScaleMode != _settings.VideoScaleMode &&
                p.SourcePath is not null && System.IO.File.Exists(p.SourcePath));
            if (anyReencodable && !_media.FfmpegAvailable)
            {
                VideoStatus = "ffmpeg not found — see tools\\fetch-ffmpeg.ps1 (put ffmpeg.exe next to the app).";
                _log.Warning("Scale-mode re-encode aborted: ffmpeg not found.");
                return;
            }
            if (anyReencodable && !_media.AdbAvailable)
            {
                VideoStatus = "adb not found — install 'ASUS Info Hub - ROG RYUO IV'.";
                _log.Warning("Scale-mode re-encode aborted: adb not found.");
                return;
            }

            VideoBusy = true;
            int reencoded = 0;
            var failed = new List<string>();
            var replacedUploads = new List<string>();
            while (true)
            {
                var mode = _settings.VideoScaleMode;   // re-read: the user may have flipped again
                var item = Playlist.FirstOrDefault(p =>
                    p.BakedScaleMode != mode && !failed.Contains(p.File) &&
                    p.SourcePath is not null && System.IO.File.Exists(p.SourcePath));
                if (item is null) break;

                _log.Information("Re-encoding {File} from {Source} as {Mode}.", item.File, item.SourcePath, mode);
                var progress = new Progress<string>(s => VideoStatus = $"{item.File}: {s}");
                var (ok, msg, deviceName) = await _media.PrepareVideoAsync(item.SourcePath!, mode, progress);

                int index = Playlist.IndexOf(item);
                if (!ok || deviceName is null || index < 0)
                {
                    failed.Add(item.File);
                    _log.Error("Re-encoding {File} as {Mode} failed: {Msg}", item.File, mode, msg);
                    continue;
                }

                var replacement = new PlaylistItem(deviceName, item.SourcePath, mode);
                bool wasSelected = ReferenceEquals(SelectedPlaylistItem, item);
                Playlist[index] = replacement;                       // drops the ListBox selection…
                if (wasSelected) SelectedPlaylistItem = replacement; // …reselect (also refreshes the preview)
                LoadThumbnail(replacement);
                SavePlaylist();
                replacedUploads.Add(item.File);
                reencoded++;
            }

            if (reencoded > 0)
            {
                ApplyPlaylist();
                LoadPreviewVideo();
            }
            // Clean up the replaced uploads only after the panel has been pointed at the
            // new files — deleting earlier could yank a video out from under a running
            // Cycle/Shuffle rotation while later entries are still transcoding.
            foreach (var old in replacedUploads)
                _ = _media.TryDeleteDeviceVideoAsync(old);

            var finalMode = _settings.VideoScaleMode;
            int notReencodable = Playlist.Count(p => p.BakedScaleMode != finalMode &&
                (p.SourcePath is null || !System.IO.File.Exists(p.SourcePath)));
            var parts = new List<string>();
            if (reencoded > 0) parts.Add($"Re-encoded {reencoded} video(s) as {finalMode}.");
            if (failed.Count > 0) parts.Add($"{failed.Count} failed — see the log.");
            if (notReencodable > 0)
                parts.Add($"{notReencodable} video(s) can't be re-encoded (a stock video, or its original " +
                          $"file is unknown or moved) — remove and re-add them to apply {finalMode}.");
            if (parts.Count == 0)
                parts.Add($"Scale mode {finalMode} saved — new videos will use it.");
            VideoStatus = string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            VideoStatus = "Error re-encoding the library: " + ex.Message;
            _log.Error(ex, "Scale-mode re-encode failed.");
        }
        finally
        {
            VideoBusy = false;
            Interlocked.Exchange(ref _scaleModeReencodeActive, 0);
        }
    }

    // ------------------------------------------------ play mode (firmware enum, all verified live)

    public bool IsRepeatOne
    {
        get => _settings.PanelPlayMode == "Single";
        set { if (value) SetPlayMode("Single"); }
    }

    public bool IsRepeatAll
    {
        get => _settings.PanelPlayMode == "Cycle";
        set { if (value) SetPlayMode("Cycle"); }
    }

    public bool IsShuffle
    {
        get => _settings.PanelPlayMode == "Random";
        set { if (value) SetPlayMode("Random"); }
    }

    private void SetPlayMode(string mode)
    {
        if (_settings.PanelPlayMode == mode) return;
        _settings.PanelPlayMode = mode;
        SaveSettings();
        OnPropertyChanged(nameof(IsRepeatOne));
        OnPropertyChanged(nameof(IsRepeatAll));
        OnPropertyChanged(nameof(IsShuffle));
        OnPropertyChanged(nameof(ActiveVideoDisplay));
        ApplyPlaylist();
    }

    public string ActiveVideoDisplay => Playlist.Count switch
    {
        0 => "Media library is empty — the panel has nothing to show.",
        1 => $"Playing on the LCD: {Playlist[0].File} (looped, re-asserted automatically)",
        _ => _settings.PanelPlayMode switch
        {
            "Single" => $"{Playlist.Count} videos — repeating the selected one.",
            "Random" => $"{Playlist.Count} videos — shuffled by the panel.",
            _ => $"{Playlist.Count} videos — played in order by the panel.",
        },
    };

    // ------------------------------------------------ playlist operations

    private void SavePlaylist()
    {
        _settings.PanelVideos = Playlist
            .Select(p => new PanelVideoEntry { File = p.File, SourcePath = p.SourcePath, ScaleMode = p.BakedScaleMode })
            .ToList();
        // Mirror of the plain name list: session re-assert reads it, and a rollback to a
        // pre-1.9 build still finds its playlist.
        _settings.PanelVideoFiles = Playlist.Select(p => p.File).ToList();
        SaveSettings();
        OnPropertyChanged(nameof(ActiveVideoDisplay));
    }

    /// <summary>Send the whole playlist + widget slots + colors to the panel in the background.
    /// In repeat-one mode the selected video is put first (the firmware loops the first entry).</summary>
    private void ApplyPlaylist()
    {
        var files = Playlist.Select(p => p.File).ToList();
        if (files.Count == 0) return;
        string mode = _settings.PanelPlayMode;
        if (mode == "Single" && SelectedPlaylistItem is not null)
        {
            files.Remove(SelectedPlaylistItem.File);
            files.Insert(0, SelectedPlaylistItem.File);
        }
        string[] slots = EffectiveMetricSlots();
        string title = _settings.MetricTitleColor;
        string content = _settings.MetricContentColor;
        Task.Run(() =>
        {
            try
            {
                var (ok, msg) = _backlight.SetPanelPlaylist(files, mode, slots, title, content);
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

    /// <summary>Info Hub-style Add: pick a file, transcode, upload, and it joins the library.</summary>
    private async void AddVideo()
    {
        if (VideoBusy) return;

        if (!_media.FfmpegAvailable)
        {
            VideoStatus = "ffmpeg not found — see tools\\fetch-ffmpeg.ps1 (put ffmpeg.exe next to the app).";
            _log.Warning("Add video aborted: ffmpeg not found.");
            return;
        }
        if (!_media.AdbAvailable)
        {
            VideoStatus = "adb not found — install 'ASUS Info Hub - ROG RYUO IV'.";
            _log.Warning("Add video aborted: adb not found.");
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add a video to the panel's media library",
            Filter = "Video files|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        string path = dlg.FileName;

        // A scale-mode re-encode may have started while the file dialog was open —
        // wait for it rather than running two ffmpeg/adb pipelines at once.
        while (VideoBusy) await Task.Delay(250);

        VideoBusy = true;
        VideoStatus = "Starting…";
        _log.Information("Adding panel video from {Path}", path);
        try
        {
            var mode = SelectedVideoScaleMode;
            var progress = new Progress<string>(s => VideoStatus = s);
            var (ok, msg, deviceName) = await _media.PrepareVideoAsync(path, mode, progress);
            if (ok && deviceName is not null)
            {
                // Join the library and activate; persisted so it survives panel reboots
                // (OnSessionOpened re-asserts the whole playlist on reconnect). Source path
                // and mode are recorded so a later scale-mode change can re-encode it.
                var item = new PlaylistItem(deviceName, path, mode);
                Playlist.Add(item);
                LoadThumbnail(item);
                SavePlaylist();
                SelectedPlaylistItem = item;
                ApplyPlaylist();
                VideoStatus = Playlist.Count == 1
                    ? "The panel is now playing your video."
                    : $"Added — {Playlist.Count} videos in the library.";
                _log.Information("Media library updated: {Msg}", msg);
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
            _log.Error(ex, "Unexpected error adding the panel video.");
        }
        finally
        {
            VideoBusy = false;
        }
    }

    // ---------------------------------------------------------------- metrics (chips)

    /// <summary>A toggleable metric widget (Info Hub-style chip), grouped by category.</summary>
    public sealed class MetricChip : ObservableObject
    {
        private readonly Func<MetricChip, bool, bool> _toggle;
        private bool _isSelected;

        public MetricChip(string token, string label, bool selected, Func<MetricChip, bool, bool> toggle)
        {
            Token = token;
            Label = label;
            _isSelected = selected;
            _toggle = toggle;
        }

        /// <summary>The sysinfoDisplay token the firmware understands.</summary>
        public string Token { get; }
        /// <summary>Short label inside its category group (e.g. "CPU" under "Temperature").</summary>
        public string Label { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                if (!_toggle(this, value)) { OnPropertyChanged(); return; }   // refused (6 max)
                SetProperty(ref _isSelected, value);
            }
        }
    }

    public sealed record MetricChipGroup(string Name, IReadOnlyList<MetricChip> Chips);

    public System.Collections.ObjectModel.ObservableCollection<MetricChipGroup> MetricGroups { get; } = new();

    /// <summary>
    /// One live widget mirrored onto the in-app LCD preview, at the same region the panel
    /// draws it. Positions are the panel's six fixed widget regions (measured from photos of
    /// the real panel), in the preview's 411×196 coordinate space.
    /// </summary>
    public sealed class MetricOverlay : ObservableObject
    {
        public MetricOverlay(string title, double x, double y)
        {
            Title = title;
            X = x;
            Y = y;
        }

        public string Title { get; }
        public double X { get; }
        public double Y { get; }

        private string _value = "—";
        public string Value { get => _value; set => SetProperty(ref _value, value); }
    }

    public System.Collections.ObjectModel.ObservableCollection<MetricOverlay> MetricOverlays { get; } = new();

    // The panel's six widget anchor points, scaled from 2240×1080 photos to the 411×196 mock.
    private static readonly (double X, double Y)[] OverlayAnchors =
    {
        (14, 26),    // slot 1: upper left
        (14, 106),   // slot 2: lower left
        (222, 108),  // slot 3: center
        (296, 30),   // slot 4: upper right
        (296, 74),   // slot 5: mid right
        (296, 128),  // slot 6: lower right
    };

    /// <summary>Rebuild the preview overlays to mirror the active widget slots.</summary>
    private void RebuildMetricOverlays()
    {
        var app = Application.Current;
        void Rebuild()
        {
            MetricOverlays.Clear();
            for (int i = 0; i < _activeMetricTokens.Count && i < OverlayAnchors.Length; i++)
            {
                var (x, y) = OverlayAnchors[i];
                MetricOverlays.Add(new MetricOverlay(_activeMetricTokens[i], x, y));
            }
            RefreshOverlayValues();
        }
        if (app is null || app.Dispatcher.CheckAccess()) Rebuild();
        else app.Dispatcher.BeginInvoke(Rebuild);
    }

    /// <summary>Push the latest sensor readings into the preview overlays.</summary>
    private void RefreshOverlayValues()
    {
        var metrics = _metrics;
        if (metrics is null || MetricOverlays.Count == 0) return;
        var values = metrics.GetWidgetValues(MetricOverlays.Select(o => o.Title));
        foreach (var overlay in MetricOverlays)
            if (values.TryGetValue(overlay.Title, out var v)) overlay.Value = v;
    }

    public System.Windows.Media.Brush MetricTitleBrush => MakeBrush(_settings.MetricTitleColor);
    public System.Windows.Media.Brush MetricContentBrush => MakeBrush(_settings.MetricContentColor);

    private static System.Windows.Media.Brush MakeBrush(string hex)
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return new System.Windows.Media.SolidColorBrush(color);
        }
        catch { return System.Windows.Media.Brushes.Cyan; }
    }

    // The active widgets in slot order (max 6 — the panel has six fixed regions).
    private readonly List<string> _activeMetricTokens = new();

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

    private string _metricsStatus = "";
    public string MetricsStatus { get => _metricsStatus; private set => SetProperty(ref _metricsStatus, value); }

    public string MetricsAccessNote =>
        SystemMetricsService.HasKernelSensorAccess
            ? ""
            : "Running without administrator rights: CPU temperature/voltage, motherboard " +
              "temperature and fan speeds read 0. Loads, GPU, memory, disk and network still work. " +
              "Run the app as administrator for the full set.";

    public bool ShowMetricsAccessNote => !SystemMetricsService.HasKernelSensorAccess;

    public string MetricTitleColor
    {
        get => _settings.MetricTitleColor;
        set
        {
            if (_settings.MetricTitleColor == value) return;
            _settings.MetricTitleColor = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MetricTitleBrush));
            DebouncedPushScreenConfig();
        }
    }

    public string MetricContentColor
    {
        get => _settings.MetricContentColor;
        set
        {
            if (_settings.MetricContentColor == value) return;
            _settings.MetricContentColor = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MetricContentBrush));
            DebouncedPushScreenConfig();
        }
    }

    private void InitializeMetrics()
    {
        _activeMetricTokens.AddRange(_settings.MetricSlots.Where(s => !string.IsNullOrWhiteSpace(s)));

        // Categories mirror Info Hub's editor; tokens are the firmware vocabulary from the
        // HomeUI apk. Fan chips for headers this board actually has join once sensors open.
        AddMetricGroup("Temperature",
            ("CPU Temperature", "CPU"), ("Motherboard Temperature", "Motherboard"), ("GPU Temperature", "GPU"));
        AddMetricGroup("Fan Speed",
            ("Fan Speed CPU Fan", "CPU"), ("Fan Speed AIO Pump", "AIO Pump"));
        AddMetricGroup("Usage / Load",
            ("CPU Usage", "CPU Usage"), ("CPU Load", "CPU Load"),
            ("GPU Usage", "GPU Usage"), ("GPU Load", "GPU Load"));
        AddMetricGroup("Frequency",
            ("CPU Speed Average", "CPU"), ("GPU Frequency", "GPU"),
            ("GPU Speed", "GPU Speed"), ("Memory Frequency", "Memory"));
        AddMetricGroup("Voltage",
            ("CPU Voltage", "CPU"), ("GPU Voltage", "GPU"));
        AddMetricGroup("Other",
            ("GPU Power", "GPU Power"), ("Date&Time", "Date & Time"));

        RebuildMetricOverlays();
        UpdateMetricsStreaming();
    }

    private void AddMetricGroup(string name, params (string Token, string Label)[] chips)
    {
        MetricGroups.Add(new MetricChipGroup(name, chips
            .Select(c => new MetricChip(c.Token, c.Label, _activeMetricTokens.Contains(c.Token), OnChipToggled))
            .ToList()));
    }

    /// <summary>Chip toggle gate: max six active (the panel has six widget regions).</summary>
    private bool OnChipToggled(MetricChip chip, bool turningOn)
    {
        if (turningOn)
        {
            if (_activeMetricTokens.Count >= 6)
            {
                MetricsStatus = "The panel has six widget slots — untick one first.";
                return false;
            }
            if (!_activeMetricTokens.Contains(chip.Token)) _activeMetricTokens.Add(chip.Token);
        }
        else
        {
            _activeMetricTokens.Remove(chip.Token);
        }
        MetricsStatus = $"{_activeMetricTokens.Count} of 6 widget slots used.";
        _settings.MetricSlots = _activeMetricTokens
            .Concat(Enumerable.Repeat("", 6)).Take(6).ToArray();
        SaveSettings();
        RebuildMetricOverlays();
        DebouncedPushScreenConfig();
        return true;
    }

    /// <summary>Debounce config pushes so toggling several chips fires one panel update.</summary>
    private void DebouncedPushScreenConfig()
    {
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

            // Mirror the fresh readings onto the in-app LCD preview overlays.
            Application.Current?.Dispatcher.BeginInvoke(RefreshOverlayValues);

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

    /// <summary>Add chips for this board's real fan headers into the "Fan Speed" group.</summary>
    private void AddDiscoveredFanOptions(IReadOnlyList<string> fanNames)
    {
        if (fanNames.Count == 0) return;
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            int groupIndex = -1;
            for (int i = 0; i < MetricGroups.Count; i++)
                if (MetricGroups[i].Name == "Fan Speed") { groupIndex = i; break; }
            if (groupIndex < 0) return;

            var group = MetricGroups[groupIndex];
            var chips = group.Chips.ToList();
            bool changed = false;
            foreach (var name in fanNames)
            {
                string token = "Fan Speed " + name;
                if (chips.Any(c => c.Token == token)) continue;
                chips.Add(new MetricChip(token, name, _activeMetricTokens.Contains(token), OnChipToggled));
                changed = true;
            }
            if (changed)
                MetricGroups[groupIndex] = group with { Chips = chips };
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

        // On resume, restore the panel immediately. Read the slider value live so moving
        // it takes effect without a restart.
        _resumeMonitor = new ResumeMonitor(10_000, _log, onResume: RestorePanelAfterResume);
        _resumeMonitor.Start();
        _log.Information("Resume monitor ON (restore to {Target}% after wake).", BrightnessPercent);
    }

    private void StopResumeMonitor()
    {
        _resumeMonitor?.Dispose();
        _resumeMonitor = null;
    }

    /// <summary>
    /// Restore the panel on wake. While the PC sleeps the host stops reading the panel's HID
    /// stream, so its firmware nulls the host→device handle: after wake our writes "succeed"
    /// but are silently discarded and the panel sits dim — the state that used to force a manual
    /// app restart, because only a brand-new HID handle re-establishes delivery. So when the
    /// keep-alive hold is running, recycle its session first (fresh handle — the in-process
    /// equivalent of restarting the app; this also re-asserts the playlist via SessionOpened),
    /// then apply the target brightness over that fresh handle so it lands regardless of the
    /// SessionOpened re-assert throttle. Without a hold, a one-shot SetPercent already opens its
    /// own fresh handle, so a plain write suffices.
    /// </summary>
    private bool RestorePanelAfterResume(CancellationToken token)
    {
        // Sleep reliably wedges the panel firmware's OWN HID handle: after wake it silently
        // discards brightness writes while still streaming input reports back, so neither a write
        // "success" nor the input-silence wedge detector (CheckForWedgedPanel) can see it, and a
        // fresh host-side handle (RecycleHold) alone does not always clear it. The one verified
        // un-wedge is restarting the panel's SerialService over adb — that re-establishes the
        // device-side hidg0 handle so our writes land again. Do it on every resume (deterministic:
        // no fragile detection that this failure mode defeats), then take a fresh host handle and
        // re-apply. When ASUS's adb isn't installed the restart is unavailable and we degrade to
        // the recycle-and-reapply that earlier builds shipped.
        var (recovered, msg) = _recovery.TryRestartSerialService();
        if (recovered)
        {
            _log.Information("Resume: restarted the panel SerialService over adb to clear the post-sleep HID wedge.");
            // am startservice is async; give the firmware a moment to re-open hidg0 before we write.
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2.5));
        }
        else
        {
            _log.Warning("Resume: adb panel recovery unavailable ({Msg}); falling back to a HID handle recycle.", msg);
        }

        if (KeepBrightnessAlive && CanControlDevice && _backlight.RecycleHold())
            _log.Information("Resume: recycled the HID session for a fresh handle before re-applying.");
        return _backlight.SetPercent(BrightnessPercent).Ok;
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
