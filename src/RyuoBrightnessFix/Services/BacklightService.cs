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
///
/// <para><b>Session / read-drain.</b> The panel's firmware only stays out of its low-power
/// standby (~1%) while the host is actively <em>reading</em> the device's HID input stream —
/// that's what ASUS Info Hub does. A write-only client works only until the device's input
/// buffer backs up, after which it dims. So <see cref="StartHold"/> opens a persistent HID
/// session and continuously drains the input reports (discarding them) to keep the session
/// alive; <see cref="SetPercent"/> then writes brightness over that same session.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BacklightService : IDisposable
{
    private const int VendorId = 0x0B05;   // ASUS
    private const int ProductId = 0x1C76;  // Ryuo IV LCD
    private const byte FrameByte = 0x5A;
    private const byte EscByte = 0x5B;

    private readonly ILogger _log;
    private readonly object _sync = new();
    private int _seq = Environment.TickCount & 0x7FFFFFFF;

    private HidStream? _stream;
    private Thread? _reader;
    private volatile bool _readerRun;
    private int _outputReportLength;
    private int _inputReportLength;
    private bool _disposed;

    public BacklightService(ILogger log)
    {
        _log = log.ForContext<BacklightService>();
    }

    /// <summary>True when a session is open, or the Ryuo IV HID interface is present on USB.</summary>
    public bool DeviceConnected()
    {
        lock (_sync) { if (_stream is not null) return true; }
        return FindDevice() is not null;
    }

    /// <summary>
    /// Open a persistent HID session and start draining the device's input stream so the panel
    /// stays out of standby. Idempotent. Returns true if a session is open.
    /// </summary>
    public bool StartHold()
    {
        lock (_sync) { return EnsureOpenLocked() is not null; }
    }

    /// <summary>Close the persistent session (stops the read-drain; panel may then dim on its own).</summary>
    public void StopHold() => CloseSession();

    /// <summary>
    /// Set the LCD brightness as a 0–100% value by sending the vendor HID command. If a hold
    /// session is open the command goes over it; otherwise it opens a one-shot connection.
    /// Pass <paramref name="quiet"/> = true for the keep-alive so it logs at Debug.
    /// </summary>
    public (bool Ok, string Message) SetPercent(int percent, bool quiet = false)
    {
        percent = Math.Clamp(percent, 0, 100);
        byte[] frame = BuildFrame("brightness", "{\"value\":" + percent + "}");
        var (ok, msg) = SendFrame(frame);
        if (ok)
        {
            if (quiet) _log.Debug("Keep-alive: brightness re-applied at {Percent}%.", percent);
            else _log.Information("Brightness set to {Percent}% over USB HID.", percent);
            return (true, $"Brightness set to {percent}% over USB HID.");
        }
        _log.Error("Brightness set failed (percent={Percent}): {Msg}", percent, msg);
        return (ok, msg);
    }

    /// <summary>
    /// Make the panel play a video file already present in <c>/sdcard/pcMedia</c> (put it there
    /// first via <see cref="MediaService"/>'s adb push). Sends the same <c>waterBlockScreenId</c>
    /// config Info Hub uses: id=Customization, Full Screen, single-loop, the given file.
    /// </summary>
    public (bool Ok, string Message) SetPanelVideo(string deviceFileName)
    {
        // Matches the captured Info Hub message exactly (id "Customization" + CamelCase playMode).
        string body =
            "{\"id\":\"Customization\",\"screenMode\":\"Full Screen\",\"playMode\":\"Single\"," +
            "\"media\":[" + JsonString(deviceFileName) + "]," +
            "\"settings\":{\"titleColor\":\"#25cfe5\",\"contentColor\":\"#25cfe5\"," +
            "\"filter\":{\"value\":null,\"opacity\":100},\"badges\":[]}," +
            "\"sysinfoDisplay\":[\"\",\"\",\"\",\"\",\"\",\"\"]}";
        // The device re-asserts its persisted config every few seconds, so a single send can
        // lose the race. Assert a few times (rebuilding the frame each time for a fresh
        // SeqNumber) to reliably override the previous video.
        (bool Ok, string Message) last = (false, "not sent");
        for (int i = 0; i < 4; i++)
        {
            last = SendFrame(BuildFrame("waterBlockScreenId", body));
            if (!last.Ok) break;
            if (i < 3) Thread.Sleep(600);
        }
        if (last.Ok) _log.Information("Panel video set to {File}.", deviceFileName);
        else _log.Error("Panel video set failed ({File}): {Msg}", deviceFileName, last.Message);
        return last.Ok ? (true, $"Panel video set to {deviceFileName}.") : last;
    }

    /// <summary>Write a framed message over the hold session, or a one-shot connection.</summary>
    private (bool Ok, string Message) SendFrame(byte[] frame)
    {
        HidStream? held;
        int heldLen;
        lock (_sync) { held = _stream; heldLen = _outputReportLength; }
        if (held is not null)
        {
            try
            {
                WriteReport(held, frame, heldLen);
                return (true, "ok");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "HID write over hold session failed; reopening.");
                CloseSession();
            }
        }

        var dev = FindDevice();
        if (dev is null)
            return (false, "Ryuo IV LCD not found on USB (VID 0B05 / PID 1C76, interface MI_00).");
        try
        {
            var options = new OpenConfiguration();
            options.SetOption(OpenOption.Interruptible, true);
            using HidStream stream = dev.Open(options);
            WriteReport(stream, frame, SafeLen(dev.GetMaxOutputReportLength));
            return (true, "ok");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "HID write failed.");
            return (false, "HID write failed: " + ex.Message);
        }
    }

    private static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c >= ' ') sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private void WriteReport(HidStream stream, byte[] frame, int reportLen)
    {
        if (reportLen <= 1) reportLen = frame.Length + 1;
        if (frame.Length > reportLen - 1)
            throw new InvalidOperationException($"Frame ({frame.Length}) larger than HID report ({reportLen - 1}).");
        var report = new byte[reportLen];
        report[0] = 0x00;   // report id (descriptor has none)
        Buffer.BlockCopy(frame, 0, report, 1, frame.Length);
        stream.Write(report);
    }

    // ---------------------------------------------------------------- session

    private HidStream? EnsureOpenLocked()   // caller holds _sync
    {
        if (_disposed) return null;
        if (_stream is not null) return _stream;

        var dev = FindDevice();
        if (dev is null) return null;

        var options = new OpenConfiguration();
        options.SetOption(OpenOption.Interruptible, true);
        var stream = dev.Open(options);
        stream.ReadTimeout = 400;
        stream.WriteTimeout = 2000;

        _outputReportLength = SafeLen(dev.GetMaxOutputReportLength);
        _inputReportLength = SafeLen(dev.GetMaxInputReportLength);
        _stream = stream;
        _readerRun = true;
        _reader = new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "RyuoHidReader" };
        _reader.Start();
        _log.Information("HID session opened (out={Out}, in={In}); read-drain active to keep the panel awake.",
            _outputReportLength, _inputReportLength);
        return _stream;
    }

    /// <summary>
    /// Continuously read (and discard) the device's HID input reports. This is what keeps the
    /// panel out of standby — without an active reader it dims to ~1% within seconds.
    /// </summary>
    private void ReadLoop(HidStream stream)
    {
        int len = _inputReportLength > 0 ? _inputReportLength : 1025;
        var buf = new byte[len];
        while (_readerRun)
        {
            try
            {
                stream.Read(buf);   // drains one device→host report; throws TimeoutException if none
            }
            catch (TimeoutException)
            {
                // No report within ReadTimeout — normal, keep draining.
            }
            catch (Exception ex)
            {
                if (_readerRun) _log.Debug(ex, "HID read-drain loop ended (device gone / session closed).");
                break;
            }
        }
    }

    private void CloseSession()
    {
        Thread? reader;
        lock (_sync)
        {
            _readerRun = false;
            try { _stream?.Close(); } catch { }
            try { _stream?.Dispose(); } catch { }
            _stream = null;
            reader = _reader;
            _reader = null;
        }
        // Let the read loop unwind (it will throw on the closed stream and exit).
        try { reader?.Join(TimeSpan.FromMilliseconds(800)); } catch { }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; }
        CloseSession();
    }

    // ---------------------------------------------------------------- device / protocol

    private static int SafeLen(Func<int> f) { try { return f(); } catch { return 0; } }

    /// <summary>Locate the Ryuo IV MI_00 vendor HID interface (usage page 0xFF00).</summary>
    private HidDevice? FindDevice()
    {
        try
        {
            HidDevice? fallback = null;
            foreach (var d in DeviceList.Local.GetHidDevices(VendorId, ProductId))
            {
                // The MI_00 interface is the vendor control channel; MI_01 is the adb/video path.
                if (d.DevicePath.Contains("mi_00", StringComparison.OrdinalIgnoreCase))
                    return d;
                fallback ??= d;
            }
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

    /// <summary>Build a framed <c>POST &lt;cmdType&gt;</c> command carrying a JSON body.</summary>
    internal byte[] BuildFrame(string cmdType, string body)
    {
        int seq = Interlocked.Increment(ref _seq) & 0x7FFFFFFF;
        int contentLength = Encoding.UTF8.GetByteCount(body);
        string text =
            "POST " + cmdType + " 1.0\r\n" +
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
