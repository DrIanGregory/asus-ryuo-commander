using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using RyuoBrightnessFix.Models;
using RyuoBrightnessFix.Services;
using RyuoBrightnessFix.Util;
using Serilog;

namespace RyuoBrightnessFix.ViewModels;

/// <summary>
/// Drives the Setup/Calibration tab: pick the device, capture two reports, auto-detect
/// the brightness byte, test live, then confirm + save. Raises <see cref="CalibrationSaved"/>
/// so the main view model can reload the now-working config.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CalibrationViewModel : ObservableObject
{
    private readonly DeviceDiscoveryService _discovery;
    private readonly BrightnessFixer _fixer;
    private readonly CalibrationService _calibration = new();
    private readonly CaptureService _capture;
    private readonly AppSettings _settings;
    private readonly Func<int> _resumeDelayProvider;
    private readonly ILogger _log;

    private RyuoConfig? _working;
    private bool _suppressRedetect;
    private IReadOnlyList<byte[]> _highReports = Array.Empty<byte[]>();
    private IReadOnlyList<byte[]> _lowReports = Array.Empty<byte[]>();

    public event Action? CalibrationSaved;

    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public ReportType[] ReportTypes { get; } = { ReportType.Output, ReportType.Feature };
    public ObservableCollection<int> CandidateOffsets { get; } = new();

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand DetectCommand { get; }
    public RelayCommand TestLowCommand { get; }
    public RelayCommand TestHighCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand RecaptureCommand { get; }
    public RelayCommand GetUsbPcapCommand { get; }
    public RelayCommand GetWiresharkCommand { get; }
    public RelayCommand RecheckSoftwareCommand { get; }
    public RelayCommand RelaunchAsAdminCommand { get; }
    public RelayCommand CaptureHighCommand { get; }
    public RelayCommand CaptureLowCommand { get; }
    public RelayCommand StopRecordingCommand { get; }
    public RelayCommand SweepOffsetsCommand { get; }

    public CalibrationViewModel(ILogger log, DeviceDiscoveryService discovery, BrightnessFixer fixer,
                                AppSettings settings, Func<int> resumeDelayProvider)
    {
        _log = log.ForContext<CalibrationViewModel>();
        _discovery = discovery;
        _fixer = fixer;
        _settings = settings;
        _resumeDelayProvider = resumeDelayProvider;
        _capture = new CaptureService(log);
        _safetyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _safetyTimer.Tick += (_, _) => OnSafetyTimeout();

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        DetectCommand = new RelayCommand(() => Detect(null), () => SelectedDevice is not null);
        TestLowCommand = new RelayCommand(() => Test(LowPercent), () => HasCandidate);
        TestHighCommand = new RelayCommand(() => Test(HighPercent), () => HasCandidate);
        ConfirmCommand = new RelayCommand(ConfirmAndSave, () => HasCandidate);
        RecaptureCommand = new RelayCommand(Recapture);
        GetUsbPcapCommand = new RelayCommand(() => OpenUrl(CaptureToolDetector.UsbPcapDownloadUrl));
        GetWiresharkCommand = new RelayCommand(() => OpenUrl(CaptureToolDetector.WiresharkDownloadUrl));
        RecheckSoftwareCommand = new RelayCommand(RecheckSoftware);
        RelaunchAsAdminCommand = new RelayCommand(RelaunchAsAdmin, () => !IsElevated);
        CaptureHighCommand = new RelayCommand(async () => await StartRecord(isHigh: true), () => HighCanRecord);
        CaptureLowCommand = new RelayCommand(async () => await StartRecord(isHigh: false), () => LowCanRecord);
        StopRecordingCommand = new RelayCommand(async () => await StopRecord(), () => IsRecording);
        SweepOffsetsCommand = new RelayCommand(async () => await ToggleSweep(),
            () => IsSweeping || (HasCandidate && CandidateOffsets.Count > 1));

        // Restore any in-progress calibration draft (bytes/percents/report type) from a prior run.
        _loadingDraft = true;
        _highPercent = Math.Clamp(settings.CaptureHighPercent, 0, 100);
        _lowPercent = Math.Clamp(settings.CaptureLowPercent, 0, 100);
        _selectedReportType = settings.CaptureReportType;
        _highHex = settings.CaptureHighHex ?? "";
        _lowHex = settings.CaptureLowHex ?? "";
        _loadingDraft = false;

        RecheckSoftware();
        RefreshDevices();

        // If we have saved bytes, re-derive the candidate so Test/Save are ready immediately.
        if (!string.IsNullOrWhiteSpace(_highHex) && !string.IsNullOrWhiteSpace(_lowHex) && SelectedDevice is not null)
            Detect(settings.CaptureOffset);
    }

    private bool _loadingDraft;

    private void SaveDraft()
    {
        if (_loadingDraft) return;
        _settings.CaptureHighHex = HighHex;
        _settings.CaptureLowHex = LowHex;
        _settings.CaptureHighPercent = HighPercent;
        _settings.CaptureLowPercent = LowPercent;
        _settings.CaptureReportType = SelectedReportType;
        _settings.CaptureOffset = SelectedOffset;
        try { _settings.Save(); }
        catch (Exception ex) { _log.Warning(ex, "Could not save calibration draft."); }
    }

    // ---------------------------------------------------------------- step 1: device

    private DeviceInfo? _selectedDevice;
    public DeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(DeviceStepDone));
                DetectCommand.RaiseCanExecuteChanged();
                RaiseRecordState();
            }
        }
    }

    public bool DeviceStepDone => SelectedDevice is not null;

    private bool _showAllDevices;
    /// <summary>
    /// When false (default), show only ASUS (ROG) devices — the Ryuo IV is VID 0x0B05.
    /// When true, show every HID device (mice, keyboards, radios) as an escape hatch.
    /// </summary>
    public bool ShowAllDevices
    {
        get => _showAllDevices;
        set { if (SetProperty(ref _showAllDevices, value)) RefreshDevices(); }
    }

    private void RefreshDevices()
    {
        var previousPath = SelectedDevice?.DevicePath;
        Devices.Clear();
        try
        {
            var all = _discovery.Enumerate()
                .GroupBy(x => x.Info.DevicePath)
                .Select(g => g.First().Info)
                .ToList();

            // Default to ASUS only — the LCD is an ASUS ROG device (VID 0x0B05). Everything
            // else (mice, keyboards, Bluetooth radios) is noise and is hidden unless asked for.
            var visible = (ShowAllDevices ? all : all.Where(d => d.LikelyAsus))
                .OrderByDescending(i => i.LikelyAsus)
                .ThenByDescending(i => i.MaxOutputReportLength)
                .ToList();

            foreach (var d in visible) Devices.Add(d);

            // Keep the previous pick if it's still shown; else best-guess the LCD.
            SelectedDevice = Devices.FirstOrDefault(d => d.DevicePath == previousPath)
                             ?? Devices.FirstOrDefault(d => d.LikelyAsus)
                             ?? Devices.FirstOrDefault();

            _log.Information("Setup: showing {Shown} of {Total} HID device(s){Mode}; selected '{Sel}'.",
                Devices.Count, all.Count, ShowAllDevices ? " (all)" : " (ASUS only)", SelectedDevice?.ProductName);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to enumerate devices for setup.");
        }
    }

    // ---------------------------------------------------------------- step 2: required software

    private bool _usbPcapInstalled;
    public bool UsbPcapInstalled
    {
        get => _usbPcapInstalled;
        private set { if (SetProperty(ref _usbPcapInstalled, value)) RaiseSoftwareChanged(); }
    }

    private bool _wiresharkInstalled;
    public bool WiresharkInstalled
    {
        get => _wiresharkInstalled;
        private set { if (SetProperty(ref _wiresharkInstalled, value)) RaiseSoftwareChanged(); }
    }

    private bool _isElevated;
    public bool IsElevated
    {
        get => _isElevated;
        private set
        {
            if (SetProperty(ref _isElevated, value))
            {
                OnPropertyChanged(nameof(AdminStatusText));
                OnPropertyChanged(nameof(NeedsElevation));
                RelaunchAsAdminCommand.RaiseCanExecuteChanged();
                RaiseRecordState();
            }
        }
    }

    public string UsbPcapStatusText => UsbPcapInstalled ? "USBPcap — installed" : "USBPcap — not installed";
    public string WiresharkStatusText => WiresharkInstalled ? "Wireshark (tshark) — installed" : "Wireshark — not installed";
    public string AdminStatusText => IsElevated ? "Running as administrator" : "Not elevated — USB capture needs administrator";

    private int _captureDeviceCount;
    public string CaptureDevicesText => _captureDeviceCount > 0
        ? $"USBPcap capture devices — {_captureDeviceCount} ready"
        : "USBPcap capture devices — none (reboot once after installing USBPcap)";
    public bool CaptureDevicesReady => _captureDeviceCount > 0;

    /// <summary>Both tools present.</summary>
    public bool SoftwareReady => UsbPcapInstalled && WiresharkInstalled;
    public bool SoftwareStepDone => SoftwareReady && IsElevated;
    public bool NeedsElevation => !IsElevated;

    private void RaiseSoftwareChanged()
    {
        OnPropertyChanged(nameof(UsbPcapStatusText));
        OnPropertyChanged(nameof(WiresharkStatusText));
        OnPropertyChanged(nameof(SoftwareReady));
        OnPropertyChanged(nameof(SoftwareStepDone));
        RaiseRecordState();
    }

    private void RecheckSoftware()
    {
        UsbPcapInstalled = _capture.UsbPcapInstalled;
        WiresharkInstalled = _capture.WiresharkInstalled;
        IsElevated = AdminUtil.IsElevated();
        _captureDeviceCount = _capture.CountControlDevices();
        OnPropertyChanged(nameof(CaptureDevicesText));
        OnPropertyChanged(nameof(CaptureDevicesReady));
        _log.Information("Setup: software check — USBPcap={U}, Wireshark={W}, elevated={E}, captureDevices={D}.",
            UsbPcapInstalled, WiresharkInstalled, IsElevated, _captureDeviceCount);
    }

    private void RelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            // User cancelled the UAC prompt, or it failed — stay running unelevated.
            _log.Warning(ex, "Relaunch as administrator was cancelled or failed.");
        }
    }

    // ---------------------------------------------------------------- step 3: capture & detect

    private enum Rec { None, High, Low }
    private Rec _recording = Rec.None;
    private CaptureService.CaptureSession? _session;
    private readonly System.Windows.Threading.DispatcherTimer _safetyTimer;

    // Record buttons only START, and only when idle. The dedicated red STOP button stops.
    public bool HighCanRecord => SoftwareReady && IsElevated && SelectedDevice is not null && _recording == Rec.None;
    public bool LowCanRecord => SoftwareReady && IsElevated && SelectedDevice is not null && _recording == Rec.None;

    public bool IsRecording => _recording != Rec.None;

    private string _captureStatus = "";
    public string CaptureStatus { get => _captureStatus; private set => SetProperty(ref _captureStatus, value); }

    private bool _highCaptured;
    public bool HighCaptured { get => _highCaptured; private set => SetProperty(ref _highCaptured, value); }

    private bool _lowCaptured;
    public bool LowCaptured { get => _lowCaptured; private set => SetProperty(ref _lowCaptured, value); }

    public string CaptureHighLabel => $"① Record HIGH ({HighPercent}%)";
    public string CaptureLowLabel => $"② Record LOW ({LowPercent}%)";
    public string StopRecordLabel => _recording == Rec.High
        ? "■ STOP RECORDING (HIGH)"
        : "■ STOP RECORDING (LOW)";

    private void RaiseRecordState()
    {
        OnPropertyChanged(nameof(HighCanRecord));
        OnPropertyChanged(nameof(LowCanRecord));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(StopRecordLabel));
        CaptureHighCommand.RaiseCanExecuteChanged();
        CaptureLowCommand.RaiseCanExecuteChanged();
        StopRecordingCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Start recording. No fixed duration — the red STOP button ends it.</summary>
    private async Task StartRecord(bool isHigh)
    {
        if (_recording != Rec.None) return;

        CaptureStatus = "Starting capture …";
        var (session, msg) = await Task.Run(() => _capture.StartCapture());
        if (session is null) { CaptureStatus = "✗ " + msg; return; }

        _session = session;
        _recording = isHigh ? Rec.High : Rec.Low;
        _safetyTimer.Start();
        RaiseRecordState();
        int pct = isHigh ? HighPercent : LowPercent;
        CaptureStatus = $"● Recording… now set the LCD brightness to {pct}% in Armoury Crate (it only takes a moment), " +
                        $"then click the red ■ STOP RECORDING button. [{msg}]";
    }

    /// <summary>Stop the active recording and parse it.</summary>
    private async Task StopRecord()
    {
        if (_recording == Rec.None || _session is null) return;

        bool isHigh = _recording == Rec.High;
        var session = _session;
        _recording = Rec.None;
        _session = null;
        _safetyTimer.Stop();
        RaiseRecordState();
        CaptureStatus = "Processing capture …";

        var outcome = await Task.Run(() => _capture.StopAndParse(session));
        if (!outcome.Ok) { CaptureStatus = "✗ " + outcome.Message; return; }

        if (isHigh) { _highReports = outcome.Reports; HighCaptured = outcome.Reports.Count > 0; }
        else { _lowReports = outcome.Reports; LowCaptured = outcome.Reports.Count > 0; }

        CaptureStatus = $"{(isHigh ? "HIGH" : "LOW")}: {outcome.Message}";
        if (HighCaptured && LowCaptured) AutoDetectFromCaptures();
    }

    private void OnSafetyTimeout()
    {
        if (_recording == Rec.None) return;
        _log.Warning("Setup: capture auto-stopped after 90s (no Stop pressed).");
        _ = StopRecord();
    }

    private void AutoDetectFromCaptures()
    {
        // Prefer the ASUS Ryuo screen command (JSON opacity) if it's in either recording —
        // it's a whole-command replay, not a byte diff.
        var screenHigh = _calibration.FindScreenTemplate(_highReports);
        var screenLow = _calibration.FindScreenTemplate(_lowReports);
        var screen = screenHigh ?? screenLow;
        if (screen is not null)
        {
            HighHex = Util.HexUtil.ToHex(screen);
            Detect(null);

            // Validate the hypothesis: did "opacity" actually change between the two brightness levels?
            string extra = "";
            int? hi = screenHigh is not null ? Services.RyuoScreenProtocol.ReadOpacity(screenHigh) : null;
            int? lo = screenLow is not null ? Services.RyuoScreenProtocol.ReadOpacity(screenLow) : null;
            if (hi is int h && lo is int l)
            {
                extra = h == l
                    ? $"  ⚠ opacity was {h} in BOTH recordings — on your unit opacity may NOT be brightness, so resending may not change anything. Try recording with a bigger brightness difference."
                    : $"  (HIGH opacity={h}, LOW opacity={l} — confirms opacity = brightness.)";
            }
            _log.Information("Opacity check — HIGH={Hi}, LOW={Lo} (same={Same}).",
                hi?.ToString() ?? "?", lo?.ToString() ?? "?", hi is not null && hi == lo);

            if (HasCandidate)
                CaptureStatus = "Detected the ASUS Ryuo screen command — now Test it (Step 4)." + extra;
            return;
        }

        var pair = _calibration.FindBrightnessPair(_highReports, _lowReports, HighPercent, LowPercent);
        if (pair is null)
        {
            CaptureStatus = "Couldn't spot a brightness report in the captures. Try again, moving only the LCD " +
                            "brightness slider, or paste the bytes manually below.";
            return;
        }

        // Feed the discovered bytes into the normal detect path so test/save just work.
        HighHex = pair.Value.HighHex;
        LowHex = pair.Value.LowHex;
        Detect(null);
        if (HasCandidate)
            CaptureStatus = "Found the brightness command automatically — now Test it (Step 4).";
    }

    // ---------------------------------------------------------------- step 3 (manual): detect

    private int _highPercent = 100;
    public int HighPercent
    {
        get => _highPercent;
        set
        {
            if (SetProperty(ref _highPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(TestHighLabel));
                OnPropertyChanged(nameof(CaptureHighLabel));
                SaveDraft();
                RedetectIfNeeded();
            }
        }
    }

    private int _lowPercent = 50;
    public int LowPercent
    {
        get => _lowPercent;
        set
        {
            if (SetProperty(ref _lowPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(TestLowLabel));
                OnPropertyChanged(nameof(CaptureLowLabel));
                SaveDraft();
                RedetectIfNeeded();
            }
        }
    }

    private string _highHex = "";
    public string HighHex { get => _highHex; set { if (SetProperty(ref _highHex, value)) SaveDraft(); } }

    private string _lowHex = "";
    public string LowHex { get => _lowHex; set { if (SetProperty(ref _lowHex, value)) SaveDraft(); } }

    private ReportType _selectedReportType = ReportType.Output;
    public ReportType SelectedReportType
    {
        get => _selectedReportType;
        set { if (SetProperty(ref _selectedReportType, value)) { SaveDraft(); RedetectIfNeeded(); } }
    }

    private int? _selectedOffset;
    public int? SelectedOffset
    {
        get => _selectedOffset;
        set { if (SetProperty(ref _selectedOffset, value)) { SaveDraft(); if (!_suppressRedetect) Detect(value); } }
    }

    private bool _hasCandidate;
    public bool HasCandidate
    {
        get => _hasCandidate;
        private set
        {
            if (SetProperty(ref _hasCandidate, value))
            {
                OnPropertyChanged(nameof(DetectStepDone));
                OnPropertyChanged(nameof(IsScreenMode));
                OnPropertyChanged(nameof(IsByteMode));
                OnPropertyChanged(nameof(UseFreshSequence));
                TestLowCommand.RaiseCanExecuteChanged();
                TestHighCommand.RaiseCanExecuteChanged();
                ConfirmCommand.RaiseCanExecuteChanged();
                SweepOffsetsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool DetectStepDone => HasCandidate;

    /// <summary>The detected command is the ASUS Ryuo screen protocol (no byte offsets apply).</summary>
    public bool IsScreenMode => HasCandidate && _working?.BrightnessControl?.Mode == BrightnessMode.RyuoScreen;

    /// <summary>The detected command is a single-byte template (byte offsets / sweep apply).</summary>
    public bool IsByteMode => HasCandidate && _working?.BrightnessControl?.Mode == BrightnessMode.ByteValue;

    /// <summary>(Screen mode) resend with a fresh SeqNumber so the device doesn't ignore a duplicate.</summary>
    public bool UseFreshSequence
    {
        get => _working?.BrightnessControl?.FreshSequence ?? true;
        set
        {
            if (_working?.BrightnessControl is { } bc && bc.FreshSequence != value)
            {
                bc.FreshSequence = value;
                OnPropertyChanged();
            }
        }
    }

    private string _detectionMessage = "";
    public string DetectionMessage { get => _detectionMessage; private set => SetProperty(ref _detectionMessage, value); }

    public string TestLowLabel => $"Set test {LowPercent}%";
    public string TestHighLabel => $"Set test {HighPercent}%";

    private void RedetectIfNeeded()
    {
        if (HasCandidate) Detect(SelectedOffset);
    }

    private void Detect(int? forcedOffset)
    {
        if (SelectedDevice is null)
        {
            DetectionMessage = "Select your device first (step 1).";
            return;
        }

        var result = _calibration.Detect(HighHex, LowHex, HighPercent, LowPercent, SelectedReportType, forcedOffset);
        DetectionMessage = result.Message;

        if (!result.Success || result.Control is null)
        {
            HasCandidate = false;
            _working = null;
            CandidateOffsets.Clear();
            return;
        }

        _suppressRedetect = true;
        CandidateOffsets.Clear();
        foreach (var o in result.DifferingOffsets) CandidateOffsets.Add(o);
        _selectedOffset = result.ChosenOffset;
        OnPropertyChanged(nameof(SelectedOffset));
        _suppressRedetect = false;

        _working = new RyuoConfig
        {
            Device = new DeviceFilter
            {
                VendorId = SelectedDevice.VendorIdHex,
                ProductId = SelectedDevice.ProductIdHex,
                // Pin the EXACT interface the user picked (the LCD exposes several with the
                // same VID/PID); the full device path uniquely identifies this one.
                PathContains = SelectedDevice.DevicePath,
            },
            BrightnessControl = result.Control,
            Brightness100Sequence = CalibrationService.Build100Sequence(result.Control),
            ResumeDelayMs = _resumeDelayProvider(),
            DryRun = false,
            Verified = false,
        };

        HasCandidate = true;
        _log.Information("Setup: candidate brightness command built — {Msg}", result.Message);
    }

    // ---------------------------------------------------------------- step 4: test

    private string _testStatus = "";
    public string TestStatus { get => _testStatus; private set => SetProperty(ref _testStatus, value); }

    private void Test(int percent)
    {
        if (_working is null) return;
        var cfg = _working;
        TestStatus = $"Sending {percent}% …";
        _log.Information("Setup: sending TEST brightness {Percent}% — watch the LCD.", percent);

        Task.Run(() =>
        {
            var (ok, message) = _fixer.SetBrightnessReport(cfg, percent, dryRun: false);
            var app = Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                TestStatus = ok
                    ? $"✓ {message}\nIf the LCD didn't change: the captured report may be wrong — try a different byte offset above, toggle the report type in Advanced, or Re-capture."
                    : $"✗ Failed: {message}";
            });
        });
    }

    private CancellationTokenSource? _sweepCts;
    public bool IsSweeping => _sweepCts is not null;
    public string SweepLabel => IsSweeping ? "■ Stop auto-test" : "Auto-test offsets";

    /// <summary>
    /// Toggle: start an automatic sweep of every candidate byte offset (sending a LOW→HIGH change
    /// for each so the user can watch which offset moves the LCD), or stop one in progress.
    /// </summary>
    private async Task ToggleSweep()
    {
        // Already running → request cancel and return.
        if (_sweepCts is not null)
        {
            _sweepCts.Cancel();
            return;
        }

        if (!HasCandidate || _working is null) return;
        var offsets = CandidateOffsets.ToList();
        if (offsets.Count == 0) return;

        _sweepCts = new CancellationTokenSource();
        var ct = _sweepCts.Token;
        RaiseSweepState();

        try
        {
            for (int i = 0; i < offsets.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                int off = offsets[i];

                _suppressRedetect = true;
                SelectedOffset = off;
                _suppressRedetect = false;
                Detect(off);                 // rebuild the command using this offset
                var cfg = _working;
                if (cfg is null) continue;

                TestStatus = $"Auto-test {i + 1}/{offsets.Count}: trying byte offset {off} — watch the LCD now…";
                await Task.Run(() => _fixer.SetBrightnessReport(cfg, LowPercent, dryRun: false));
                try { await Task.Delay(1300, ct); } catch (OperationCanceledException) { break; }
                await Task.Run(() => _fixer.SetBrightnessReport(cfg, HighPercent, dryRun: false));
                try { await Task.Delay(1600, ct); } catch (OperationCanceledException) { break; }
            }

            TestStatus = ct.IsCancellationRequested
                ? "Auto-test stopped."
                : "Auto-test finished. Set 'Brightness byte offset' to whichever number made the LCD change, then Save (Step 5).";
        }
        finally
        {
            _sweepCts?.Dispose();
            _sweepCts = null;
            RaiseSweepState();
        }
    }

    private void RaiseSweepState()
    {
        OnPropertyChanged(nameof(IsSweeping));
        OnPropertyChanged(nameof(SweepLabel));
        SweepOffsetsCommand.RaiseCanExecuteChanged();
    }

    // ---------------------------------------------------------------- step 5: confirm + save

    private bool _verifiedStepDone;
    public bool VerifiedStepDone { get => _verifiedStepDone; private set => SetProperty(ref _verifiedStepDone, value); }

    private void ConfirmAndSave()
    {
        if (_working is null) return;
        try
        {
            _working.Verified = true;
            var path = _settings.ResolveDeviceConfigPath();
            _working.Save(path);
            VerifiedStepDone = true;
            _log.Information("Setup: calibration saved and verified -> {Path}", path);
            CalibrationSaved?.Invoke();
        }
        catch (Exception ex)
        {
            DetectionMessage = "Could not save config: " + ex.Message;
            _log.Error(ex, "Failed to save calibrated config.");
        }
    }

    private void Recapture()
    {
        HasCandidate = false;
        VerifiedStepDone = false;
        _working = null;
        CandidateOffsets.Clear();
        DetectionMessage = "";
        HighHex = "";
        LowHex = "";
        _highReports = Array.Empty<byte[]>();
        _lowReports = Array.Empty<byte[]>();
        HighCaptured = false;
        LowCaptured = false;
        CaptureStatus = "";
        TestStatus = "";
        _log.Information("Setup: cleared candidate; ready to re-capture.");
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _log.Warning(ex, "Could not open {Url}", url); }
    }
}
