using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RyuoBrightnessFix.Util;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Performs a real USB capture of the brightness command.
///
/// On a typical machine USBPcap is NOT registered as a Wireshark extcap, so tshark
/// cannot capture USB directly. The reliable path (and what Wireshark itself does) is:
///   1. start the USBPcap kernel driver (creates the \\.\USBPcapN control devices),
///   2. capture with USBPcapCMD.exe to a .pcap file,
///   3. read that file back with tshark -r and extract the HID OUT reports.
///
/// Steps 1–2 require Administrator. All of this is delegated to the real tools — we
/// never hand-parse a pcap.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CaptureService
{
    private readonly ILogger _log;

    public string? TsharkPath { get; }
    public string? UsbPcapCmdPath { get; }

    public CaptureService(ILogger log)
    {
        _log = log.ForContext<CaptureService>();
        TsharkPath = FindFirst(
            @"C:\Program Files\Wireshark\tshark.exe",
            @"C:\Program Files (x86)\Wireshark\tshark.exe");
        UsbPcapCmdPath = FindFirst(
            @"C:\Program Files\USBPcap\USBPcapCMD.exe",
            @"C:\Program Files (x86)\USBPcap\USBPcapCMD.exe");
    }

    public bool WiresharkInstalled => TsharkPath is not null;
    public bool UsbPcapInstalled => UsbPcapCmdPath is not null || CaptureToolDetector.IsUsbPcapInstalled();

    private static string? FindFirst(params string[] candidates)
        => candidates.FirstOrDefault(File.Exists);

    // ---------------------------------------------------------------- enumerate control devices

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDeviceW(string? lpDeviceName, char[] buffer, uint ucchMax);

    /// <summary>
    /// Enumerate the USBPcap control devices via the NT device-name table (no handle/access
    /// needed). Returns names like <c>\\.\USBPcap1</c>. Empty when the USBPcap filter is not
    /// attached to the USB controllers (i.e. a reboot after install is still required).
    /// </summary>
    public IReadOnlyList<string> GetControlDevices()
    {
        var result = new List<string>();
        try
        {
            uint size = 1 << 16;
            char[] buffer = new char[size];
            uint len = QueryDosDeviceW(null, buffer, size);
            while (len == 0 && Marshal.GetLastWin32Error() == 122 /* ERROR_INSUFFICIENT_BUFFER */)
            {
                size *= 2;
                buffer = new char[size];
                len = QueryDosDeviceW(null, buffer, size);
            }
            if (len == 0) return result;

            foreach (var name in new string(buffer, 0, (int)len).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // Match "USBPcap1", "USBPcap2", … exactly.
                if (name.StartsWith("USBPcap", StringComparison.OrdinalIgnoreCase) &&
                    name.Length > 7 && name[7..].All(char.IsDigit))
                {
                    result.Add(@"\\.\" + name);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not enumerate USBPcap control devices.");
        }
        return result;
    }

    /// <summary>Convenience for the UI: how many USBPcap capture devices are currently available.</summary>
    public int CountControlDevices() => GetControlDevices().Count;

    /// <summary>Start the USBPcap kernel driver so the control devices appear. Needs admin.</summary>
    private bool TryStartDriver()
    {
        try
        {
            var (exit, _, err) = Run("sc.exe", new[] { "start", "USBPcap" }, TimeSpan.FromSeconds(20));
            // 0 = started, 1056 = already running.
            if (exit == 0 || exit == 1056) return true;
            _log.Warning("sc start USBPcap returned {Exit}: {Err}", exit, err.Trim());
            return false;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to start USBPcap driver.");
            return false;
        }
    }

    public sealed record CaptureOutcome(bool Ok, string Message, IReadOnlyList<byte[]> Reports);

    /// <summary>A live capture in progress: the USBPcapCMD processes writing pcap files.</summary>
    public sealed class CaptureSession
    {
        internal List<Process> Procs { get; } = new();
        internal List<string> Files { get; } = new();
        internal string TempDir { get; init; } = "";
        internal int DeviceCount { get; init; }
    }

    /// <summary>
    /// Begin capturing now. The user changes the brightness whenever they're ready, then calls
    /// <see cref="StopAndParse"/>. Returns null + a message if capture can't start.
    /// </summary>
    public (CaptureSession? Session, string Message) StartCapture()
    {
        if (UsbPcapCmdPath is null)
            return (null, "USBPcap is not installed. Install it (Step 2), then re-check.");
        if (TsharkPath is null)
            return (null, "Wireshark (tshark) is not installed. Install it (Step 2), then re-check.");
        if (!AdminUtil.IsElevated())
            return (null, "USB capture needs Administrator. Use 'Relaunch as admin' (Step 2), then try again.");

        // Make sure the driver is running so the \\.\USBPcapN devices exist.
        TryStartDriver();
        var devices = GetControlDevices();
        for (int attempt = 0; attempt < 3 && devices.Count == 0; attempt++)
        {
            Thread.Sleep(400);
            devices = GetControlDevices();
        }
        if (devices.Count == 0)
        {
            return (null, "USBPcap is installed but its filter isn't attached to your USB controllers. " +
                          "USBPcap only attaches at boot — REBOOT Windows once, then try again. " +
                          "(If you've already rebooted, re-run the USBPcap installer and let it reboot.)");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "RyuoBrightnessFix", "cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var session = new CaptureSession { TempDir = tempDir, DeviceCount = devices.Count };

        int idx = 0;
        foreach (var dev in devices)
        {
            string file = Path.Combine(tempDir, $"cap{idx++}.pcap");
            session.Files.Add(file);
            var psi = new ProcessStartInfo
            {
                FileName = UsbPcapCmdPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-d", dev, "-o", file, "-A", "-b", "1048576" })
                psi.ArgumentList.Add(a);

            try { var p = Process.Start(psi); if (p is not null) session.Procs.Add(p); }
            catch (Exception ex) { _log.Warning(ex, "Could not start USBPcapCMD for {Dev}", dev); }
        }

        if (session.Procs.Count == 0)
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            return (null, "Could not start USBPcapCMD. Check that USBPcap is installed correctly.");
        }

        _log.Information("Capture started on {Count} USBPcap control device(s).", session.Procs.Count);
        return (session, $"Recording on {session.DeviceCount} USB controller(s).");
    }

    /// <summary>Stop a capture session, parse the pcaps, and return the distinct OUT reports.</summary>
    public CaptureOutcome StopAndParse(CaptureSession session)
    {
        try
        {
            foreach (var p in session.Procs)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            Thread.Sleep(400); // let the pcap files flush to disk

            var reports = new List<byte[]>();
            var seen = new HashSet<string>();
            foreach (var file in session.Files)
            {
                if (!File.Exists(file)) continue;
                try { if (new FileInfo(file).Length <= 24) continue; } catch { continue; } // just the pcap header

                foreach (var rep in ReadReportsFromPcap(file))
                {
                    var key = HexUtil.ToHex(rep);
                    if (seen.Add(key)) reports.Add(rep);
                }
            }

            // Diagnostic logging: decode EVERY captured OUT report so the real brightness command
            // can be found (it may not be the waterBlockScreenId one).
            int screenCount = 0;
            int idx = 0;
            foreach (var r in reports)
            {
                bool isScreen = RyuoScreenProtocol.Matches(r);
                if (isScreen) screenCount++;
                _log.Information("Captured report #{Idx} ({Len}B, screen={Screen}, opacity={Op}): {Text}",
                    idx++, r.Length, isScreen, RyuoScreenProtocol.ReadOpacity(r),
                    RyuoScreenProtocol.DescribeAny(r));
            }

            string message = reports.Count > 0
                ? $"Captured {reports.Count} distinct OUT report(s); {screenCount} look like the Ryuo screen command."
                : "No OUT reports were captured. Make sure you changed the brightness while recording.";
            _log.Information("Capture finished: {Msg}", message);
            return new CaptureOutcome(true, message, reports);
        }
        finally
        {
            foreach (var p in session.Procs) { try { p.Dispose(); } catch { } }
            try { Directory.Delete(session.TempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Run tshark over a captured pcap and extract host→device HID report bytes.</summary>
    private IReadOnlyList<byte[]> ReadReportsFromPcap(string pcapPath)
    {
        // Capture EVERY data-bearing host→device transfer (interrupt, control, bulk, …) and pull
        // every field the data might land in — so we don't miss a brightness command sent as a
        // feature report or bulk transfer.
        var args = new[]
        {
            "-r", pcapPath, "-Q",
            "-Y", "usb.capdata || usbhid.data || usb.data_fragment",
            "-T", "fields",
            "-e", "usb.capdata",
            "-e", "usbhid.data",
            "-e", "usb.data_fragment",
            "-e", "usb.transfer_type",
            "-e", "usb.endpoint_address",
        };
        var (_, stdout, _) = Run(TsharkPath!, args, TimeSpan.FromSeconds(30));
        return ParseReports(stdout);
    }

    /// <summary>Parse tshark field lines into distinct OUT report byte arrays.</summary>
    private static IReadOnlyList<byte[]> ParseReports(string stdout)
    {
        var seen = new HashSet<string>();
        var reports = new List<byte[]>();

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var fields = line.Split('\t');
            string capdata = fields.Length > 0 ? fields[0] : "";
            string hidData = fields.Length > 1 ? fields[1] : "";
            string frag = fields.Length > 2 ? fields[2] : "";
            string endpoint = fields.Length > 4 ? fields[4] : "";

            // Keep host→device only (OUT). bEndpointAddress high bit set => IN.
            // If the endpoint field is empty (some control transfers), keep it rather than drop.
            var ep = HexUtil.TryParseNumber(endpoint);
            if (ep is int epv && (epv & 0x80) != 0) continue;

            var dataHex = !string.IsNullOrWhiteSpace(capdata) ? capdata
                        : !string.IsNullOrWhiteSpace(hidData) ? hidData
                        : frag;
            if (string.IsNullOrWhiteSpace(dataHex)) continue;

            byte[] bytes;
            try { bytes = HexUtil.ParseHex(dataHex); }
            catch { continue; }
            if (bytes.Length == 0) continue;

            var key = HexUtil.ToHex(bytes);
            if (seen.Add(key)) reports.Add(bytes);
        }

        return reports;
    }

    private static CaptureOutcome Fail(string message) => new(false, message, Array.Empty<byte[]>());

    private static (int ExitCode, string StdOut, string StdErr) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        string outp = proc.StandardOutput.ReadToEnd();
        string err = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        return (proc.HasExited ? proc.ExitCode : -1, outp, err);
    }
}
