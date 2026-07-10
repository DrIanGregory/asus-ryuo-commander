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
    private readonly object _writeSync = new();
    private int _seq = Environment.TickCount & 0x7FFFFFFF;

    private HidStream? _stream;
    private Thread? _reader;
    private volatile bool _readerRun;
    private bool _holdWanted;   // hold requested by the caller; sessions self-heal while true
    private int _outputReportLength;
    private int _inputReportLength;
    private bool _disposed;
    private long _lastInputReportTicks;   // UTC ticks of the last device→host report (Interlocked)

    /// <summary>Raised (on an arbitrary thread) whenever the machine's HID device list changes —
    /// the cue to re-check whether the Ryuo IV has arrived or left.</summary>
    public event Action? DeviceListChanged;

    /// <summary>
    /// Raised each time a NEW hold session opens (startup, self-heal after a failure, or the
    /// panel re-enumerating after a reboot/recovery). The panel forgets its screen config when
    /// it reboots, so this is the cue to re-assert the video + brightness. May fire while the
    /// service's internal lock is held — handlers MUST queue work (e.g. Task.Run) and never
    /// call back into this service synchronously.
    /// </summary>
    public event Action? SessionOpened;

    public BacklightService(ILogger log)
    {
        _log = log.ForContext<BacklightService>();
        DeviceList.Local.Changed += OnHidListChanged;
    }

    private void OnHidListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        try { DeviceListChanged?.Invoke(); }
        catch (Exception ex) { _log.Warning(ex, "Device-list change handler failed."); }
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
        lock (_sync)
        {
            _holdWanted = true;
            return EnsureOpenLocked() is not null;
        }
    }

    /// <summary>Close the persistent session (stops the read-drain; panel may then dim on its own).</summary>
    public void StopHold()
    {
        lock (_sync) { _holdWanted = false; }
        CloseSession();
    }

    /// <summary>
    /// Force a fresh HID handle: close the current hold session and immediately reopen it so the
    /// panel firmware sees a brand-new host→device pipe. Whenever the host stops reading the
    /// device's input stream — the PC sleeping does exactly this — the firmware nulls its own HID
    /// handle, after which our writes "succeed" locally but are silently discarded and the panel
    /// stays dim. A stale handle can't be un-wedged in place; only a fresh open re-establishes
    /// delivery, which is precisely what restarting the whole app does. Reopening fires
    /// <see cref="SessionOpened"/>, so the saved playlist + brightness get re-asserted over the new
    /// handle. No-op (returns false) when no hold is wanted; returns true if a fresh session is open.
    /// </summary>
    public bool RecycleHold()
    {
        if (!WantHold()) return false;
        lock (_writeSync)
        {
            CloseSessionCore();
            lock (_sync)
            {
                if (_disposed || !_holdWanted) return false;
                return EnsureOpenLocked() is not null;
            }
        }
    }

    /// <summary>
    /// How long since the device last sent us an input report over the hold session, or null
    /// when no session is open. A healthy panel streams reports continuously (~10/s); writes
    /// that "succeed" while this grows large mean the panel firmware has wedged its own HID
    /// handle (it does this whenever the host stops reading — app restarts, PC sleep) and is
    /// silently discarding everything until its SerialService is restarted.
    /// </summary>
    public TimeSpan? TimeSinceLastInputReport
    {
        get
        {
            lock (_sync) { if (_stream is null) return null; }
            long ticks = Interlocked.Read(ref _lastInputReportTicks);
            if (ticks == 0) return null;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Set the LCD brightness as a 0–100% value by sending the vendor HID command. If a hold
    /// session is open the command goes over it; otherwise it opens a one-shot connection.
    /// Pass <paramref name="quiet"/> = true for the keep-alive so it logs at Debug.
    /// </summary>
    public (bool Ok, string Message) SetPercent(int percent, bool quiet = false)
    {
        percent = Math.Clamp(percent, 0, 100);
        int seq = Interlocked.Increment(ref _seq) & 0x7FFFFFFF;
        byte[] frame = BuildFrame("brightness", "{\"value\":" + percent + "}", seq);

        var (ok, msg) = SendFrame(frame, "brightness " + percent + "%");
        if (ok)
        {
            LogSet(percent, quiet);
            return (true, $"Brightness set to {percent}% over USB HID.");
        }
        return (false, msg);
    }

    /// <summary>
    /// Make the panel play a video file already present in <c>/sdcard/pcMedia</c> (or one of
    /// the stock presets in <c>/sdcard/pcMediaPreset</c> — the firmware resolves preset names
    /// itself). Sends the same <c>waterBlockScreenId</c> config Info Hub uses: id=Customization,
    /// Full Screen, single-loop, the given file. <paramref name="sysinfoDisplay"/> fills the
    /// six metric widget slots (tokens like "CPU Temperature", "Fan Speed AIO Pump",
    /// "Date&amp;Time"); null/empty slots hide the widget. The values behind the widgets come
    /// from the <c>STATE all</c> telemetry stream (<see cref="SendSysinfo"/>).
    /// </summary>
    public (bool Ok, string Message) SetPanelVideo(string deviceFileName, string?[]? sysinfoDisplay = null)
        => SetPanelPlaylist(new[] { deviceFileName }, "Single", sysinfoDisplay);

    /// <summary>
    /// Make the panel loop a playlist of videos (each already present in /sdcard/pcMedia or
    /// the stock preset dir). <paramref name="playMode"/> is the firmware enum (from Info
    /// Hub's source, each verified live): "Single" loops the FIRST video only, "Cycle" plays
    /// the list in order, "Random" shuffles. The optional hex colors style the metric
    /// widgets' title/value text.
    /// </summary>
    public (bool Ok, string Message) SetPanelPlaylist(
        IReadOnlyList<string> deviceFileNames, string playMode = "Cycle", string?[]? sysinfoDisplay = null,
        string? titleColor = null, string? contentColor = null)
    {
        if (deviceFileNames.Count == 0)
            return (false, "Playlist is empty — nothing to activate.");

        var slots = new string?[6];
        if (sysinfoDisplay is not null)
            for (int i = 0; i < Math.Min(6, sysinfoDisplay.Length); i++) slots[i] = sysinfoDisplay[i];
        string slotsJson = string.Join(",", slots.Select(s => JsonString(s ?? "")));
        string mediaJson = string.Join(",", deviceFileNames.Select(JsonString));
        if (playMode is not ("Single" or "Cycle" or "Random")) playMode = "Cycle";
        string title = SafeHexColor(titleColor);
        string content = SafeHexColor(contentColor);

        // Matches the captured Info Hub message exactly (id "Customization" + CamelCase playMode).
        string body =
            "{\"id\":\"Customization\",\"screenMode\":\"Full Screen\",\"playMode\":\"" + playMode + "\"," +
            "\"media\":[" + mediaJson + "]," +
            "\"settings\":{\"titleColor\":" + JsonString(title) + ",\"contentColor\":" + JsonString(content) + "," +
            "\"filter\":{\"value\":null,\"opacity\":100},\"badges\":[]}," +
            "\"sysinfoDisplay\":[" + slotsJson + "]}";

        // The device re-asserts its persisted config every few seconds, so a single send can
        // lose the race. Assert a few times (fresh SeqNumber each) to reliably take over.
        (bool Ok, string Message) last = (false, "not sent");
        for (int i = 0; i < 4; i++)
        {
            int seq = Interlocked.Increment(ref _seq) & 0x7FFFFFFF;
            last = SendFrame(BuildFrame("waterBlockScreenId", body, seq), "panel video");
            if (!last.Ok) break;
            if (i < 3) Thread.Sleep(600);
        }
        string described = deviceFileNames.Count == 1
            ? deviceFileNames[0]
            : $"{deviceFileNames.Count} videos ({playMode})";
        if (last.Ok)
        {
            _log.Information("Panel playlist set: {What}.", described);
            return (true, $"Panel playlist set: {described}.");
        }
        _log.Error("Panel playlist set failed ({What}): {Msg}", described, last.Message);
        return last;
    }

    /// <summary>
    /// Stream one live sysinfo snapshot to the panel — the <c>STATE all 1</c> message Info Hub
    /// sends every few seconds. The panel's metric widgets (configured via
    /// <see cref="SetPanelVideo"/>'s sysinfoDisplay slots) render these values. Quiet: logs at
    /// Debug only, since it runs on a timer.
    /// </summary>
    public (bool Ok, string Message) SendSysinfo(string allJson)
    {
        int seq = Interlocked.Increment(ref _seq) & 0x7FFFFFFF;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (ok, msg) = SendFrame(BuildStateFrame("all", allJson, seq, now), "sysinfo");
        if (ok) _log.Debug("Sysinfo snapshot streamed ({Bytes} bytes).", allJson.Length);
        return (ok, msg);
    }

    /// <summary>
    /// Deliver one framed message with the full self-heal ladder: write over the hold session
    /// (reopening it first if the hold is wanted but the session died), retry once over a
    /// freshly reopened session, then fall back to a one-shot connection.
    /// </summary>
    private (bool Ok, string Message) SendFrame(byte[] frame, string what)
    {
        lock (_writeSync)
        {
            // Fast path: write over the open hold session. If a hold is wanted but the session
            // died (earlier write failure, device re-enumerated over sleep), reopen it here —
            // the read-drain is what keeps the panel out of standby, so a one-shot fallback is
            // never a substitute for the session.
            HidStream? held;
            int heldLen;
            lock (_sync)
            {
                held = _holdWanted ? EnsureOpenLocked() : _stream;
                heldLen = _outputReportLength;
            }
            if (held is not null)
            {
                try
                {
                    WriteReport(held, frame, heldLen);
                    return (true, "ok");
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "HID write over hold session failed ({What}); reopening.", what);
                    CloseSessionCore();
                }

                // Reopen the hold session once and retry over it, so the read-drain comes back
                // immediately rather than waiting for the next keep-alive tick.
                if (WantHold())
                {
                    lock (_sync)
                    {
                        held = EnsureOpenLocked();
                        heldLen = _outputReportLength;
                    }
                    if (held is not null)
                    {
                        try
                        {
                            WriteReport(held, frame, heldLen);
                            return (true, "ok (session reopened)");
                        }
                        catch (Exception ex)
                        {
                            _log.Error(ex, "HID write failed again over the reopened session ({What}).", what);
                            CloseSessionCore();
                            // fall through to a one-shot attempt
                        }
                    }
                }
            }

            // One-shot path (no hold session, or the held write just failed).
            var dev = FindDevice();
            if (dev is null)
                return (false, "Ryuo IV LCD not found on USB (VID 0B05 / PID 1C76, interface MI_00).");
            try
            {
                var options = new OpenConfiguration();
                options.SetOption(OpenOption.Interruptible, true);
                using HidStream stream = dev.Open(options);
                WriteReport(stream, frame, SafeLen(dev.GetMaxOutputReportLength));
                return (true, "ok (one-shot)");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "HID write failed ({What}).", what);
                return (false, "HID write failed: " + ex.Message);
            }
        }
    }

    /// <summary>Minimal JSON string literal (quotes + backslash escaping) for file names.</summary>
    private static string JsonString(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Validate a "#rrggbb" hex color, falling back to Info Hub's stock cyan.</summary>
    private static string SafeHexColor(string? color)
        => color is not null && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")
            ? color.ToLowerInvariant()
            : "#25cfe5";

    private bool WantHold()
    {
        lock (_sync) { return _holdWanted && !_disposed; }
    }

    private void LogSet(int percent, bool quiet)
    {
        if (quiet) _log.Debug("Keep-alive: brightness re-applied at {Percent}%.", percent);
        else _log.Information("Brightness set to {Percent}% over USB HID.", percent);
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
        // Baseline the silence clock at open so a fresh session isn't instantly "wedged".
        Interlocked.Exchange(ref _lastInputReportTicks, DateTime.UtcNow.Ticks);
        _readerRun = true;
        _reader = new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "RyuoHidReader" };
        _reader.Start();
        _log.Information("HID session opened (out={Out}, in={In}); read-drain active to keep the panel awake.",
            _outputReportLength, _inputReportLength);
        try { SessionOpened?.Invoke(); }
        catch (Exception ex) { _log.Warning(ex, "SessionOpened handler failed."); }
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
                Interlocked.Exchange(ref _lastInputReportTicks, DateTime.UtcNow.Ticks);
            }
            catch (TimeoutException)
            {
                // No report within ReadTimeout — normal, keep draining.
            }
            catch (Exception ex)
            {
                // _readerRun still true means this wasn't a deliberate CloseSession() — the
                // device dropped the stream. Without the drain the panel idle-dims, so tear
                // the dead session down (no self-join!) and let SetPercent's self-heal reopen it.
                bool unexpected = false;
                lock (_sync)
                {
                    if (_readerRun && ReferenceEquals(_stream, stream))
                    {
                        unexpected = true;
                        try { _stream.Dispose(); } catch { }
                        _stream = null;
                        _reader = null;
                    }
                }
                if (unexpected)
                    _log.Warning(ex, "HID read-drain stopped unexpectedly (device gone?); " +
                                     "session will reopen on the next brightness write.");
                break;
            }
        }
    }

    private void CloseSession()
    {
        lock (_writeSync)
        {
            CloseSessionCore();
        }
    }

    private void CloseSessionCore()
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
        DeviceList.Local.Changed -= OnHidListChanged;
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

    /// <summary>Build a framed <c>POST &lt;cmdType&gt;</c> message with a JSON body.</summary>
    internal static byte[] BuildFrame(string cmdType, string body, int seq)
        => BuildFrameCore("POST", cmdType, "1.0", body, seq, null);

    /// <summary>
    /// Build a framed <c>STATE &lt;cmdType&gt;</c> message — the telemetry variant Info Hub
    /// uses for the live sysinfo stream (<c>STATE all 1</c> + a <c>Date</c> header).
    /// </summary>
    internal static byte[] BuildStateFrame(string cmdType, string body, int seq, long unixMillis)
        => BuildFrameCore("STATE", cmdType, "1", body, seq, unixMillis);

    private static byte[] BuildFrameCore(
        string requestState, string cmdType, string version, string body, int seq, long? dateMillis)
    {
        int contentLength = Encoding.UTF8.GetByteCount(body);
        string text =
            requestState + " " + cmdType + " " + version + "\r\n" +
            "SeqNumber=" + seq + "\r\n" +
            (dateMillis is long d ? "Date=" + d + "\r\n" : "") +
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
