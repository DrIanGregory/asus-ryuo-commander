using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Collects live system metrics (via LibreHardwareMonitor) and renders them as the
/// <c>STATE all 1</c> JSON snapshot the Ryuo IV panel expects — the same shape ASUS Info Hub
/// streams (captured live):
/// <code>
/// {"network":{...},"memory":{...},"cpu":{...},"gpu":{...},"disk":{...},
///  "fans":[{"onBoard":true,"name":"AIO Pump","value":3146}],
///  "motherboard":{...},"timestamp":...}
/// </code>
/// The panel matches its widget slots against this data: "CPU Temperature" reads
/// cpu.temperature, "Fan Speed AIO Pump" reads fans[name=="AIO Pump"], and so on.
///
/// Elevation note: CPU temperature/voltage and motherboard fan RPMs come from ring-0 sensor
/// access, which Windows only grants to elevated processes. Without admin those values read 0
/// and <see cref="HasKernelSensorAccess"/> is false. GPU metrics are intentionally disabled:
/// the NVIDIA/native path can terminate the process after sleep or driver resets.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemMetricsService : IDisposable
{
    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly NvidiaGpuReader _gpuReader;
    private IReadOnlyDictionary<string, string> _fanLabels = new Dictionary<string, string>();
    private Computer? _computer;
    private bool _openFailed;
    private bool _gpuDisabledLogged;

    public SystemMetricsService(ILogger log)
    {
        _log = log.ForContext<SystemMetricsService>();
        _gpuReader = new NvidiaGpuReader(_log);

        // A GPU driver reset (TDR) or a suspend/resume cycle can invalidate the native GPU
        // handles LibreHardwareMonitor uses. The next GPU poll can then die with an
        // AccessViolationException below managed code; .NET cannot catch it and the tray app
        // is killed outright. We still reset the sensor stack on these events, but the stable
        // fix is to avoid LHM's GPU path entirely in this always-on tray process.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
            ResetAsync($"power event: {e.Mode}");
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => ResetAsync("display settings changed (possible GPU driver reset)");

    // Reset off the SystemEvents broadcast thread: _sync can be held for seconds while a
    // poll re-opens the sensor stack, and stalling that shared thread would freeze every
    // other SystemEvents subscriber in the process.
    private void ResetAsync(string reason) => Task.Run(() =>
    {
        try { Reset(reason); }
        catch (Exception ex) { _log.Warning(ex, "Sensor reset ({Reason}) failed.", reason); }
    });

    /// <summary>
    /// Close the sensor stack so the next poll reopens it with fresh driver handles. Also
    /// clears the open-failed latch, giving sensors that failed to open a second chance
    /// once the machine's state has changed.
    /// </summary>
    public void Reset(string reason)
    {
        lock (_sync)
        {
            _openFailed = false;
            if (_computer is null) return;
            try { _computer.Close(); }
            catch (Exception ex)
            {
                _log.Warning(ex, "Closing hardware sensors during reset failed; dropping the instance anyway.");
            }
            _computer = null;
            _log.Information("Hardware sensors closed ({Reason}); reopening on the next metrics poll.", reason);
        }
    }

    /// <summary>True when running elevated, i.e. kernel sensor access (CPU temp, fan RPM) works.</summary>
    public static bool HasKernelSensorAccess
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Open the sensor stack (seconds on first call). Returns false if unavailable.</summary>
    public bool EnsureOpen()
    {
        lock (_sync)
        {
            if (_computer is not null) return true;
            if (_openFailed) return false;
            try
            {
                var computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = false,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                    IsNetworkEnabled = true,
                };
                computer.Open();
                _computer = computer;
                if (!_gpuDisabledLogged)
                {
                    _gpuDisabledLogged = true;
                    _log.Information("LibreHardwareMonitor's in-process NVIDIA path is disabled (it crashes the process after driver resets); GPU metrics come from nvidia-smi out-of-process instead.");
                }
                _log.Information("Hardware sensors opened (kernel sensor access: {Admin}).",
                    HasKernelSensorAccess);
                return true;
            }
            catch (Exception ex)
            {
                _openFailed = true;
                _log.Error(ex, "Opening hardware sensors failed; metrics streaming unavailable.");
                return false;
            }
        }
    }

    /// <summary>Names of motherboard fan headers with a non-zero reading (e.g. "CPU Fan", "AIO Pump").</summary>
    public IReadOnlyList<string> GetFanNames()
    {
        lock (_sync)
        {
            if (_computer is null) return Array.Empty<string>();
            var names = new List<string>();
            foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard))
            {
                hw.Update();
                foreach (var sub in hw.SubHardware)
                {
                    sub.Update();
                    names.AddRange(sub.Sensors
                        .Where(s => s.SensorType == SensorType.Fan && (s.Value ?? 0) > 0)
                        .Select(s => _fanLabels.TryGetValue(s.Name, out var l) && !string.IsNullOrWhiteSpace(l)
                            ? l : s.Name));
                }
            }
            return names;
        }
    }

    /// <summary>Take a snapshot of every sensor and render the "all" JSON body.</summary>
    public string? BuildAllJson()
    {
        lock (_sync)
        {
            if (_computer is null) return null;
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware) sub.Update();
                }
                var snap = AugmentWithGpu(RelabelFans(Collect(_computer)));
                _lastSnapshot = snap;
                return RenderJson(snap);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Collecting metrics failed; skipping this snapshot.");
                return null;
            }
        }
    }

    /// <summary>Everything one poll saw, for both the JSON stream and the UI overlays.</summary>
    public sealed record Snapshot(
        double CpuLoad, double CpuTemp, double CpuPkgTemp, double CpuClock, double CpuPower, double CpuVolt,
        bool HasDedicatedGpu, double GpuLoad, double GpuTemp, double GpuFan, double GpuClock,
        double GpuPower, double GpuVolt,
        long MemTotalMb, long MemUsedMb, double MemLoad,
        long DiskTotalGb, long DiskUsedGb, double DiskLoad, double DiskActivity, double DiskTemp,
        double DiskRead, double DiskWrite,
        double NetUp, double NetDown,
        double MbTemp, double ChipsetTemp,
        IReadOnlyList<(string Name, double Rpm)> Fans);

    private Snapshot? _lastSnapshot;

    /// <summary>
    /// Formatted display values (e.g. "71 °C", "3139 RPM") for the given widget tokens, from
    /// the most recent poll — what the panel's widgets show, mirrored for the in-app preview.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetWidgetValues(IEnumerable<string> tokens)
    {
        var snap = _lastSnapshot;
        var result = new Dictionary<string, string>();
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            result[token] = FormatWidgetValue(token, snap);
        }
        return result;
    }

    private static string FormatWidgetValue(string token, Snapshot? s)
    {
        if (token == "Date&Time")
            return DateTime.Now.ToString("ddd d  HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();
        if (s is null) return "—";
        if (token.StartsWith("Fan Speed ", StringComparison.OrdinalIgnoreCase))
        {
            string fan = token["Fan Speed ".Length..];
            var match = s.Fans.FirstOrDefault(f =>
                f.Name.Equals(fan, StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains(fan, StringComparison.OrdinalIgnoreCase) ||
                fan.Contains(f.Name, StringComparison.OrdinalIgnoreCase));
            return match.Name is null ? "0 RPM" : $"{match.Rpm:0} RPM";
        }
        return token switch
        {
            "CPU Temperature" => $"{s.CpuTemp:0} °C",
            "Motherboard Temperature" => $"{s.MbTemp:0} °C",
            "GPU Temperature" => $"{s.GpuTemp:0} °C",
            "CPU Usage" or "CPU Load" => $"{s.CpuLoad:0} %",
            "GPU Usage" or "GPU Load" => $"{s.GpuLoad:0} %",
            "CPU Speed Average" => $"{s.CpuClock:0} MHz",
            "GPU Frequency" or "GPU Speed" => $"{s.GpuClock:0} MHz",
            "Memory Frequency" => "0 MHz",
            "CPU Voltage" => $"{s.CpuVolt:0.###} V",
            "GPU Voltage" => $"{s.GpuVolt:0.###} V",
            "GPU Power" => $"{s.GpuPower:0} W",
            _ => "—",
        };
    }

    // ---------------------------------------------------------------- collection

    private static Snapshot Collect(Computer computer)
    {
        var all = computer.Hardware.ToList();

        // --- CPU ---
        var cpu = all.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        double cpuLoad = Sensor(cpu, SensorType.Load, "CPU Total") ?? 0;
        double cpuTemp = Sensor(cpu, SensorType.Temperature, "Core (Tctl/Tdie)")
                         ?? Sensor(cpu, SensorType.Temperature, "CPU Package")
                         ?? MaxSensor(cpu, SensorType.Temperature) ?? 0;
        double cpuPkgTemp = Sensor(cpu, SensorType.Temperature, "CPU Package") ?? cpuTemp;
        double cpuClock = AvgSensor(cpu, SensorType.Clock, exclude: "Bus") ?? 0;
        double cpuPower = Sensor(cpu, SensorType.Power, "Package")
                          ?? Sensor(cpu, SensorType.Power, "CPU Package")
                          ?? MaxSensor(cpu, SensorType.Power) ?? 0;
        double cpuVolt = Sensor(cpu, SensorType.Voltage, "Core (SVI2 TFN)")
                         ?? Sensor(cpu, SensorType.Voltage, "CPU Core")
                         ?? MaxSensor(cpu, SensorType.Voltage) ?? 0;

        // --- GPU (prefer the dedicated one) ---
        var gpu = all.FirstOrDefault(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                  ?? all.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
        bool hasDedicated = gpu is not null && gpu.HardwareType != HardwareType.GpuIntel;
        double gpuLoad = Sensor(gpu, SensorType.Load, "GPU Core") ?? MaxSensor(gpu, SensorType.Load) ?? 0;
        double gpuTemp = Sensor(gpu, SensorType.Temperature, "GPU Core")
                         ?? MaxSensor(gpu, SensorType.Temperature) ?? 0;
        double gpuFan = MaxSensor(gpu, SensorType.Fan) ?? 0;
        double gpuClock = Sensor(gpu, SensorType.Clock, "GPU Core") ?? MaxSensor(gpu, SensorType.Clock) ?? 0;
        double gpuPower = MaxSensor(gpu, SensorType.Power) ?? 0;
        double gpuVolt = MaxSensor(gpu, SensorType.Voltage) ?? 0;

        // --- Memory (LHM reports GB; the panel expects MB) ---
        var mem = all.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        double memUsedGb = Sensor(mem, SensorType.Data, "Memory Used") ?? 0;
        double memFreeGb = Sensor(mem, SensorType.Data, "Memory Available") ?? 0;
        double memLoad = Sensor(mem, SensorType.Load, "Memory") ?? 0;
        long memTotalMb = (long)Math.Round((memUsedGb + memFreeGb) * 1024);
        long memUsedMb = (long)Math.Round(memUsedGb * 1024);

        // --- Disk (system drive for capacity; first storage device for activity/temp) ---
        long diskTotalGb = 0, diskUsedGb = 0;
        double diskLoad = 0;
        try
        {
            var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            diskTotalGb = sys.TotalSize / (1024L * 1024 * 1024);
            diskUsedGb = (sys.TotalSize - sys.TotalFreeSpace) / (1024L * 1024 * 1024);
            diskLoad = diskTotalGb > 0 ? Math.Round(100.0 * diskUsedGb / diskTotalGb) : 0;
        }
        catch { /* removable/odd system drives — leave zeros */ }
        var disk = all.FirstOrDefault(h => h.HardwareType == HardwareType.Storage);
        double diskTemp = MaxSensor(disk, SensorType.Temperature) ?? 0;
        double diskActivity = Sensor(disk, SensorType.Load, "Total Activity") ?? 0;
        double diskRead = (Sensor(disk, SensorType.Throughput, "Read Rate") ?? 0) / 1024;   // B/s -> KB/s
        double diskWrite = (Sensor(disk, SensorType.Throughput, "Write Rate") ?? 0) / 1024;

        // --- Network (sum all active adapters; B/s -> KB/s) ---
        double netUp = 0, netDown = 0;
        foreach (var nic in all.Where(h => h.HardwareType == HardwareType.Network))
        {
            netUp += (Sensor(nic, SensorType.Throughput, "Upload Speed") ?? 0) / 1024;
            netDown += (Sensor(nic, SensorType.Throughput, "Download Speed") ?? 0) / 1024;
        }

        // --- Motherboard temps + fans (SuperIO; needs elevation to read) ---
        double mbTemp = 0, chipsetTemp = 0;
        var fans = new List<(string Name, double Rpm)>();
        foreach (var mb in all.Where(h => h.HardwareType == HardwareType.Motherboard))
        {
            foreach (var sub in mb.SubHardware)
            {
                mbTemp = Sensor(sub, SensorType.Temperature, "Motherboard")
                         ?? Sensor(sub, SensorType.Temperature, "System")
                         ?? MaxSensor(sub, SensorType.Temperature) ?? mbTemp;
                chipsetTemp = Sensor(sub, SensorType.Temperature, "Chipset") ?? chipsetTemp;
                fans.AddRange(sub.Sensors
                    .Where(s => s.SensorType == SensorType.Fan && (s.Value ?? 0) > 0)
                    .Select(s => (s.Name, (double)s.Value!)));
            }
        }

        return new Snapshot(
            cpuLoad, cpuTemp, cpuPkgTemp, cpuClock, cpuPower, cpuVolt,
            hasDedicated, gpuLoad, gpuTemp, gpuFan, gpuClock, gpuPower, gpuVolt,
            memTotalMb, memUsedMb, memLoad,
            diskTotalGb, diskUsedGb, diskLoad, diskActivity, diskTemp, diskRead, diskWrite,
            netUp, netDown, mbTemp, chipsetTemp, fans);
    }

    /// <summary>
    /// Overlay GPU readings from nvidia-smi onto the snapshot. LibreHardwareMonitor's in-process
    /// NVIDIA path stays disabled (its native NVML call crashes the whole process after a driver
    /// reset — an uncatchable AccessViolation), so GPU data comes from nvidia-smi run
    /// out-of-process: if that ever crashes it takes only its own short-lived child with it, never
    /// this service. No-op (leaves zeros) when nvidia-smi is absent or has no reading yet.
    /// </summary>
    /// <summary>Set the fan-header → friendly-name map (from settings). Applied to every snapshot
    /// so the panel's "Fan Speed &lt;label&gt;" widgets resolve to the right physical header.</summary>
    public void SetFanLabels(IReadOnlyDictionary<string, string> labels)
    {
        lock (_sync) { _fanLabels = labels ?? new Dictionary<string, string>(); }
    }

    /// <summary>Rename fans per the user's label map (e.g. "Fan #7" → "AIO Pump"), so widgets that
    /// reference a friendly name match the generic header the board actually reports.</summary>
    private Snapshot RelabelFans(Snapshot snap)
    {
        if (_fanLabels.Count == 0 || snap.Fans.Count == 0) return snap;
        var relabeled = snap.Fans
            .Select(f => (_fanLabels.TryGetValue(f.Name, out var label) && !string.IsNullOrWhiteSpace(label)
                ? label : f.Name, f.Rpm))
            .ToList();
        return snap with { Fans = relabeled };
    }

    private Snapshot AugmentWithGpu(Snapshot snap)
    {
        var g = _gpuReader.Read();
        if (g is null) return snap;
        return snap with
        {
            HasDedicatedGpu = true,
            GpuTemp = g.Value.Temp,
            GpuLoad = g.Value.Load,
            GpuPower = g.Value.Power,
            GpuClock = g.Value.Clock,
            GpuFan = g.Value.Fan,
        };
    }

    /// <summary>Human-readable dump of every hardware/sub-hardware/sensor currently visible, for
    /// diagnosing which sensors a given security context (e.g. the session-0 service) can read.</summary>
    public string DumpSensorTree()
    {
        lock (_sync)
        {
            if (_computer is null) return "(sensor stack not open)";
            var sb = new StringBuilder(2048);
            sb.Append("kernelAccess=").Append(HasKernelSensorAccess).Append('\n');
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                sb.Append(hw.HardwareType).Append(": ").Append(hw.Name).Append('\n');
                foreach (var s in hw.Sensors)
                    sb.Append("  [").Append(s.SensorType).Append("] ").Append(s.Name)
                      .Append(" = ").Append(s.Value?.ToString(CultureInfo.InvariantCulture) ?? "null").Append('\n');
                foreach (var sub in hw.SubHardware)
                {
                    sub.Update();
                    sb.Append("  SUB ").Append(sub.Name).Append('\n');
                    foreach (var s in sub.Sensors)
                        sb.Append("    [").Append(s.SensorType).Append("] ").Append(s.Name)
                          .Append(" = ").Append(s.Value?.ToString(CultureInfo.InvariantCulture) ?? "null").Append('\n');
                }
            }
            return sb.ToString();
        }
    }

    private static string RenderJson(Snapshot s)
    {
        var sb = new StringBuilder(768);
        sb.Append("{\"network\":{\"upload\":").Append(N(s.NetUp))
          .Append(",\"download\":").Append(N(s.NetDown))
          .Append("},\"memory\":{\"total\":").Append(s.MemTotalMb)
          .Append(",\"used\":").Append(s.MemUsedMb)
          .Append(",\"load\":").Append(N(s.MemLoad))
          .Append(",\"temperature\":0,\"speed\":0}")
          .Append(",\"cpu\":{\"load\":").Append(N(s.CpuLoad))
          .Append(",\"temperature\":").Append(N(s.CpuTemp))
          .Append(",\"temperaturePackage\":").Append(N(s.CpuPkgTemp))
          .Append(",\"speedAverage\":").Append(N(s.CpuClock))
          .Append(",\"power\":").Append(N(s.CpuPower))
          .Append(",\"voltage\":").Append(F(s.CpuVolt))
          .Append(",\"usage\":").Append(N(s.CpuLoad))
          .Append("},\"gpu\":{\"hasDedicated\":").Append(s.HasDedicatedGpu ? "true" : "false")
          .Append(",\"load\":").Append(N(s.GpuLoad))
          .Append(",\"temperature\":").Append(N(s.GpuTemp))
          .Append(",\"fan\":").Append(N(s.GpuFan))
          .Append(",\"speed\":").Append(N(s.GpuClock))
          .Append(",\"power\":").Append(N(s.GpuPower))
          .Append(",\"voltage\":").Append(F(s.GpuVolt))
          .Append("},\"disk\":{\"total\":").Append(s.DiskTotalGb)
          .Append(",\"used\":").Append(s.DiskUsedGb)
          .Append(",\"load\":").Append(N(s.DiskLoad))
          .Append(",\"activity\":").Append(N(s.DiskActivity))
          .Append(",\"temperature\":").Append(N(s.DiskTemp))
          .Append(",\"readSpeed\":").Append(N(s.DiskRead))
          .Append(",\"writeSpeed\":").Append(N(s.DiskWrite))
          .Append("},\"fans\":[");
        for (int i = 0; i < s.Fans.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"onBoard\":true,\"name\":\"")
              .Append(s.Fans[i].Name.Replace("\\", "\\\\").Replace("\"", "\\\""))
              .Append("\",\"value\":").Append(N(s.Fans[i].Rpm)).Append('}');
        }
        sb.Append("],\"motherboard\":{\"temperature\":").Append(N(s.MbTemp))
          .Append(",\"chipsetTemperature\":").Append(N(s.ChipsetTemp))
          .Append("},\"timestamp\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
          .Append('}');
        return sb.ToString();
    }

    // Integers for whole quantities (Info Hub sends them un-fractioned), 3-dp for voltages.
    private static string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);
    private static string F(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);

    private static double? Sensor(IHardware? hw, SensorType type, string name)
        => FirstValue(hw, s => s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static double? MaxSensor(IHardware? hw, SensorType type)
    {
        var values = AllSensors(hw).Where(s => s.SensorType == type && s.Value.HasValue)
            .Select(s => (double)s.Value!).ToList();
        return values.Count > 0 ? values.Max() : null;
    }

    private static double? AvgSensor(IHardware? hw, SensorType type, string exclude)
    {
        var values = AllSensors(hw)
            .Where(s => s.SensorType == type && s.Value.HasValue &&
                        !s.Name.Contains(exclude, StringComparison.OrdinalIgnoreCase))
            .Select(s => (double)s.Value!).ToList();
        return values.Count > 0 ? values.Average() : null;
    }

    private static double? FirstValue(IHardware? hw, Func<ISensor, bool> match)
    {
        var s = AllSensors(hw).FirstOrDefault(x => match(x) && x.Value.HasValue);
        return s?.Value is float v ? v : null;
    }

    private static IEnumerable<ISensor> AllSensors(IHardware? hw)
    {
        if (hw is null) yield break;
        foreach (var s in hw.Sensors) yield return s;
        foreach (var sub in hw.SubHardware)
            foreach (var s in sub.Sensors) yield return s;
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        lock (_sync)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}

/// <summary>
/// Reads GPU temperature / load / power / clock / fan from <c>nvidia-smi</c>, out-of-process, as a
/// crash-safe replacement for LibreHardwareMonitor's in-process NVML (which raises an uncatchable
/// AccessViolation after a driver reset and kills the host). Cached briefly so the ~150 ms
/// nvidia-smi launch runs at most once per poll; self-disables when nvidia-smi isn't present.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NvidiaGpuReader
{
    public readonly record struct GpuReading(double Temp, double Load, double Power, double Clock, double Fan);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2.5);

    private readonly ILogger _log;
    private readonly string? _exe;
    private GpuReading? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private bool _loggedUnavailable;

    public NvidiaGpuReader(ILogger log)
    {
        _log = log.ForContext<NvidiaGpuReader>();
        _exe = LocateSmi();
        if (_exe is null)
            _log.Information("nvidia-smi not found — GPU metrics will read 0 (no NVIDIA GPU or driver tools).");
    }

    private static string? LocateSmi()
    {
        // The driver installs nvidia-smi into System32 (on PATH); older setups use Program Files.
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(system)) return system;
        string progFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        if (File.Exists(progFiles)) return progFiles;
        return "nvidia-smi.exe";   // last resort: resolve via PATH (Read() self-disables if it fails)
    }

    public GpuReading? Read()
    {
        if (_exe is null) return null;
        if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cached;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--query-gpu=temperature.gpu,utilization.gpu,power.draw,clocks.gr,fan.speed");
            psi.ArgumentList.Add("--format=csv,noheader,nounits");

            using var proc = Process.Start(psi);
            if (proc is null) return _cached;
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { } return _cached; }

            // First GPU line, e.g. "42, 32, 45.59, 2800, 30" (fields may be "[N/A]").
            string? line = output.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (line is null) return _cached;
            var f = line.Split(',');
            double P(int i) => i < f.Length &&
                double.TryParse(f[i].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

            _cached = new GpuReading(P(0), P(1), P(2), P(3), P(4));
            _cachedAtUtc = DateTime.UtcNow;
            return _cached;
        }
        catch (Exception ex)
        {
            if (!_loggedUnavailable) { _loggedUnavailable = true; _log.Warning(ex, "nvidia-smi GPU read failed; GPU metrics unavailable."); }
            return _cached;
        }
    }
}
