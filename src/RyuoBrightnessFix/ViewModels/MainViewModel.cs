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

    /// <summary>Raised when the tray icon visibility should change (App owns the tray).</summary>
    public event Action<bool>? TrayVisibilityRequested;

    public RelayCommand ApplyBrightnessCommand { get; }
    public RelayCommand Restore100Command { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }

    public MainViewModel(ILogger log, UiLogSink uiSink, AppSettings settings, StartupRegistrationService startup,
        LoggingLevelSwitch levelSwitch)
    {
        _log = log.ForContext<MainViewModel>();
        _settings = settings;
        _startup = startup;
        _levelSwitch = levelSwitch;
        _backlight = new BacklightService(log);

        // Mirror persisted settings into bindable fields.
        _brightnessPercent = settings.TargetBrightnessPercent;
        _suspendBrightnessPercent = settings.SuspendBrightnessPercent;
        _startWithWindows = startup.IsRegistered();   // reflect reality, not just the saved flag
        _startMinimized = settings.StartMinimized;
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _showTrayIcon = settings.ShowTrayIcon;
        _autoFixOnResume = settings.AutoFixOnResume;
        _setBrightnessOnSuspend = settings.SetBrightnessOnSuspend;
        _debugLogging = settings.DebugLogging;

        ApplyBrightnessCommand = new RelayCommand(() => ApplyBrightness(BrightnessPercent), () => CanControlDevice);
        Restore100Command = new RelayCommand(() => ApplyBrightness(100), () => CanControlDevice);
        ReloadCommand = new RelayCommand(RefreshDevice);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);

        uiSink.LineWritten += OnLogLine;

        _log.Debug("Settings on load: AutoFixOnResume={Resume}, SetBrightnessOnSuspend={Suspend}, " +
                   "Target={Target}%, SuspendBrightness={SuspendPct}%, DebugLogging={Debug}.",
            _autoFixOnResume, _setBrightnessOnSuspend, _brightnessPercent, _suspendBrightnessPercent, _debugLogging);

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

    private int _suspendBrightnessPercent;
    public int SuspendBrightnessPercent
    {
        get => _suspendBrightnessPercent;
        set
        {
            if (SetProperty(ref _suspendBrightnessPercent, Math.Clamp(value, 0, 100)))
            {
                _settings.SuspendBrightnessPercent = _suspendBrightnessPercent;
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

    private bool _setBrightnessOnSuspend;
    public bool SetBrightnessOnSuspend
    {
        get => _setBrightnessOnSuspend;
        set
        {
            if (!SetProperty(ref _setBrightnessOnSuspend, value)) return;
            _settings.SetBrightnessOnSuspend = value;
            SaveSettings();
            RestartResumeMonitor();
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
            }
            else
            {
                CanControlDevice = false;
                SliderEnabled = false;
                DeviceStatus = "Ryuo IV LCD not found on USB (is it connected and powered?).";
                _log.Warning("Ryuo IV HID interface not found (VID 0B05 / PID 1C76 / MI_00).");
                StopResumeMonitor();
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

    // ---------------------------------------------------------------- resume monitor

    private void RestartResumeMonitor()
    {
        StopResumeMonitor();
        if (!AutoFixOnResume && !SetBrightnessOnSuspend) return;

        // Read the slider values live (not captured) so moving a slider takes effect
        // without restarting the monitor. Each action is wired only if its toggle is on.

        // On resume, restore the normal target brightness.
        Func<CancellationToken, bool>? onResume = AutoFixOnResume
            ? _ => _backlight.SetPercent(BrightnessPercent).Ok
            : null;

        // Just before sleep, push the dedicated "while asleep" brightness so the panel
        // stays bright instead of dropping to its minimum. Fast/unverified write — the
        // suspend window is short.
        Func<bool>? onSuspend = SetBrightnessOnSuspend
            ? () => _backlight.SetPercent(SuspendBrightnessPercent, verify: false).Ok
            : null;

        _resumeMonitor = new ResumeMonitor(10_000, _log, onResume, onSuspend);
        _resumeMonitor.Start();
        _log.Information(
            "Power monitor ON (restore-on-wake={Resume} to {Target}%, set-on-sleep={Suspend} to {SuspendPct}%).",
            AutoFixOnResume, BrightnessPercent, SetBrightnessOnSuspend, SuspendBrightnessPercent);
    }

    private void StopResumeMonitor()
    {
        _resumeMonitor?.Dispose();
        _resumeMonitor = null;
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

    public void Dispose() => StopResumeMonitor();
}
