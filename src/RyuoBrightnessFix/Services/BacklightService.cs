using System.Text;
using System.Runtime.Versioning;
using HidSharp;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// THE fix. The Ryuo IV LCD brightness is NOT the Linux sysfs backlight node and NOT the
/// Android <c>screen_brightness</c> setting — both are decoupled from this DSI panel. ASUS
/// Info Hub sets brightness over a vendor <b>USB HID</b> interface, and the on-device firmware
/// applies the value as the home-UI window's <c>WindowManager.LayoutParams.screenBrightness</c>
/// (a per-window override). This service speaks that same HID protocol directly — no adb, no
/// Info Hub required.
///
/// Protocol (reverse-engineered from the Info Hub app + on-device SerialService, and verified
/// live against the panel):
/// <list type="bullet">
/// <item>Device: USB <c>VID 0x0B05</c> / <c>PID 0x1C76</c>, interface MI_00 (vendor usage
/// page 0xFF00). Device side is <c>/dev/hidg0</c>, report length 1024.</item>
/// <item>Message (CRLF line endings): <c>POST brightness 1.0</c> + <c>SeqNumber</c>,
/// <c>ContentType=json</c>, <c>ContentLength</c> headers + JSON body <c>{"value":N}</c>,
/// N = 0..100.</item>
/// <item>Frame: <c>0x5A | uint16_BE(wireLen) | escape(payload) | escape(checksum) | 0x5A</c>,
/// checksum = additive sum of the un-escaped payload &amp; 0xFF, escape 0x5A→0x5B01 /
/// 0x5B→0x5B02, wireLen = escaped byte count.</item>
/// <item>Delivered as one HID output report: <c>[0x00] + frame + zero-pad</c> to the report
/// length.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BacklightService
{
    private const int VendorId = 0x0B05;   // ASUS
    private const int ProductId = 0x1C76;  // Ryuo IV LCD
    private const byte FrameByte = 0x5A;
    private const byte EscByte = 0x5B;

    private readonly ILogger _log;
    private int _seq = Environment.TickCount & 0x7FFFFFFF;

    public BacklightService(ILogger log)
    {
        _log = log.ForContext<BacklightService>();
    }

    /// <summary>True when the Ryuo IV HID interface is present on USB.</summary>
    public bool DeviceConnected() => FindDevice() is not null;

    /// <summary>
    /// Set the LCD brightness as a 0–100% value by sending the vendor HID command.
    /// Returns (ok, message). <paramref name="verify"/> is accepted for call-site parity but
    /// the HID channel is fire-and-forget (write-only), so success reflects the USB write.
    /// </summary>
    public (bool Ok, string Message) SetPercent(int percent, bool verify = true)
    {
        percent = Math.Clamp(percent, 0, 100);

        var device = FindDevice();
        if (device is null)
            return (false, "Ryuo IV LCD not found on USB (VID 0B05 / PID 1C76, interface MI_00).");

        int seq = unchecked(System.Threading.Interlocked.Increment(ref _seq)) & 0x7FFFFFFF;
        byte[] frame = BuildFrame(percent, seq);

        try
        {
            var options = new OpenConfiguration();
            options.SetOption(OpenOption.Interruptible, true);
            using HidStream stream = device.Open(options);

            int reportLen = device.GetMaxOutputReportLength();   // includes the report-id byte
            if (reportLen <= 1) reportLen = frame.Length + 1;
            var report = new byte[reportLen];
            report[0] = 0x00;                                    // report id (descriptor has none)
            if (frame.Length > reportLen - 1)
                return (false, $"Frame ({frame.Length}) larger than HID report ({reportLen - 1}).");
            Buffer.BlockCopy(frame, 0, report, 1, frame.Length);

            stream.Write(report);
            _log.Debug("HID write ok: percent={Percent} seq={Seq} frame={Frame}",
                percent, seq, Convert.ToHexString(frame));
            _log.Information("Brightness set to {Percent}% over USB HID.", percent);
            return (true, $"Brightness set to {percent}% over USB HID.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "HID write failed (percent={Percent}).", percent);
            return (false, "HID write failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- protocol

    /// <summary>Locate the Ryuo IV MI_00 vendor HID interface (usage page 0xFF00).</summary>
    private HidDevice? FindDevice()
    {
        try
        {
            HidDevice? fallback = null;
            foreach (var d in DeviceList.Local.GetHidDevices(VendorId, ProductId))
            {
                // The MI_00 interface is the vendor control channel; MI_01 is the adb/video path.
                bool isMi00 = d.DevicePath.Contains("mi_00", StringComparison.OrdinalIgnoreCase)
                              || d.DevicePath.Contains("&mi_00", StringComparison.OrdinalIgnoreCase);
                if (isMi00) return d;
                fallback ??= d;
            }
            // Some enumerations don't expose the interface in the path — fall back to a
            // 1024-byte-report device, then to any matching VID/PID device.
            if (fallback is not null)
                _log.Debug("HID MI_00 not matched by path; using fallback {Path}.", fallback.DevicePath);
            return fallback;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Enumerating HID devices failed.");
            return null;
        }
    }

    /// <summary>Build the framed brightness command for a 0–100 value.</summary>
    internal static byte[] BuildFrame(int percent, int seq)
    {
        string body = "{\"value\":" + percent + "}";
        int contentLength = Encoding.UTF8.GetByteCount(body);
        string text =
            "POST brightness 1.0\r\n" +
            "SeqNumber=" + seq + "\r\n" +
            "ContentType=json\r\n" +
            "ContentLength=" + contentLength + "\r\n" +
            "\r\n" +
            body;

        byte[] payload = Encoding.UTF8.GetBytes(text);

        byte checksum = 0;
        foreach (byte b in payload) checksum = unchecked((byte)(checksum + b));

        byte[] escPayload = Escape(payload);
        byte[] escChecksum = Escape(new[] { checksum });
        int wireLen = escPayload.Length + escChecksum.Length;

        var frame = new List<byte>(wireLen + 4)
        {
            FrameByte,
            (byte)((wireLen >> 8) & 0xFF),
            (byte)(wireLen & 0xFF),
        };
        frame.AddRange(escPayload);
        frame.AddRange(escChecksum);
        frame.Add(FrameByte);
        return frame.ToArray();
    }

    /// <summary>Byte-stuff: 0x5A → 0x5B 0x01, 0x5B → 0x5B 0x02.</summary>
    private static byte[] Escape(byte[] data)
    {
        var o = new List<byte>(data.Length + 4);
        foreach (byte b in data)
        {
            if (b == FrameByte) { o.Add(EscByte); o.Add(0x01); }
            else if (b == EscByte) { o.Add(EscByte); o.Add(0x02); }
            else o.Add(b);
        }
        return o.ToArray();
    }
}
