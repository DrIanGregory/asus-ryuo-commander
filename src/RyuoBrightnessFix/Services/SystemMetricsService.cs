using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;
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
/// and <see cref="HasKernelSensorAccess"/> is false — GPU stats, loads, memory, disk and
/// network still work.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemMetricsService : IDisposable
{
    private readonly ILogger _log;
    private readonly object _sync = new();
    private Computer? _computer;
    private bool _openFailed;

    public SystemMetricsService(ILogger log) => _log = log.ForContext<SystemMetricsService>();

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
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                    IsNetworkEnabled = true,
                };
                computer.Open();
                _computer = computer;
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
                        .Select(s => s.Name));
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
                return Render(_computer);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Collecting metrics failed; skipping this snapshot.");
                return null;
            }
        }
    }

    // ---------------------------------------------------------------- rendering

    private static string Render(Computer computer)
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

        var sb = new StringBuilder(768);
        sb.Append("{\"network\":{\"upload\":").Append(N(netUp))
          .Append(",\"download\":").Append(N(netDown))
          .Append("},\"memory\":{\"total\":").Append(memTotalMb)
          .Append(",\"used\":").Append(memUsedMb)
          .Append(",\"load\":").Append(N(memLoad))
          .Append(",\"temperature\":0,\"speed\":0}")
          .Append(",\"cpu\":{\"load\":").Append(N(cpuLoad))
          .Append(",\"temperature\":").Append(N(cpuTemp))
          .Append(",\"temperaturePackage\":").Append(N(cpuPkgTemp))
          .Append(",\"speedAverage\":").Append(N(cpuClock))
          .Append(",\"power\":").Append(N(cpuPower))
          .Append(",\"voltage\":").Append(F(cpuVolt))
          .Append(",\"usage\":").Append(N(cpuLoad))
          .Append("},\"gpu\":{\"hasDedicated\":").Append(hasDedicated ? "true" : "false")
          .Append(",\"load\":").Append(N(gpuLoad))
          .Append(",\"temperature\":").Append(N(gpuTemp))
          .Append(",\"fan\":").Append(N(gpuFan))
          .Append(",\"speed\":").Append(N(gpuClock))
          .Append(",\"power\":").Append(N(gpuPower))
          .Append(",\"voltage\":").Append(F(gpuVolt))
          .Append("},\"disk\":{\"total\":").Append(diskTotalGb)
          .Append(",\"used\":").Append(diskUsedGb)
          .Append(",\"load\":").Append(N(diskLoad))
          .Append(",\"activity\":").Append(N(diskActivity))
          .Append(",\"temperature\":").Append(N(diskTemp))
          .Append(",\"readSpeed\":").Append(N(diskRead))
          .Append(",\"writeSpeed\":").Append(N(diskWrite))
          .Append("},\"fans\":[");
        for (int i = 0; i < fans.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"onBoard\":true,\"name\":\"")
              .Append(fans[i].Name.Replace("\\", "\\\\").Replace("\"", "\\\""))
              .Append("\",\"value\":").Append(N(fans[i].Rpm)).Append('}');
        }
        sb.Append("],\"motherboard\":{\"temperature\":").Append(N(mbTemp))
          .Append(",\"chipsetTemperature\":").Append(N(chipsetTemp))
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
        lock (_sync)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}
