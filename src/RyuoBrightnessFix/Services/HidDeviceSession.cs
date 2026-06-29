using HidSharp;
using RyuoBrightnessFix.Models;
using RyuoBrightnessFix.Util;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// An opened HID device. Wraps a HidSharp <see cref="HidStream"/> and provides
/// safe, length-validated output/feature writes plus input reads.
///
/// All writes are explicit; nothing is sent on construction. Wrong HID commands
/// can confuse a device until reboot/replug — callers must opt in per command.
/// </summary>
public sealed class HidDeviceSession : IDisposable
{
    private readonly HidDevice _device;
    private readonly DeviceInfo _info;
    private readonly ILogger _log;
    private HidStream? _stream;

    public DeviceInfo Info => _info;

    public HidDeviceSession(HidDevice device, DeviceInfo info, ILogger log)
    {
        _device = device;
        _info = info;
        _log = log.ForContext<HidDeviceSession>();
    }

    public void Open()
    {
        if (_stream is not null) return;

        var options = new OpenConfiguration();
        // Be permissive about sharing; Armoury Crate may also hold a handle.
        options.SetOption(OpenOption.Interruptible, true);

        if (!_device.TryOpen(options, out var stream))
        {
            throw new IOException(
                $"Could not open HID device {_info.DevicePath}. " +
                "Another process (e.g. Armoury Crate / ASUS Info Hub) may hold an exclusive handle, " +
                "or this process lacks permission. Try closing ASUS background apps, or run elevated.");
        }

        _stream = stream;
        _stream.ReadTimeout = 1000;
        _stream.WriteTimeout = 2000;
        _log.Information("Opened HID device {Path}", _info.DevicePath);
    }

    /// <summary>
    /// Build a length-correct buffer for a report. The payload (which already
    /// starts with the report-ID byte) is padded with zeros up to the device's
    /// max report length for that report type. Throws if the payload is longer
    /// than the device will accept.
    /// </summary>
    private byte[] BuildBuffer(HidCommand cmd, byte[] payload)
    {
        int max = cmd.ReportType switch
        {
            ReportType.Output => _info.MaxOutputReportLength,
            ReportType.Feature => _info.MaxFeatureReportLength,
            _ => 0,
        };

        if (payload.Length == 0)
            throw new InvalidOperationException($"Command '{cmd.Name}' has an empty payload.");

        if (max > 0 && payload.Length > max)
        {
            throw new InvalidOperationException(
                $"Command '{cmd.Name}' payload is {payload.Length} bytes but the device's max " +
                $"{cmd.ReportType} report length is {max}. Refusing to send a too-long report.");
        }

        // Validate the declared reportId matches the first byte (catches copy/paste mistakes).
        if (cmd.ReportId != payload[0])
        {
            _log.Warning(
                "Command '{Name}': declared reportId 0x{Declared:X2} != first payload byte 0x{Actual:X2}. " +
                "The first payload byte is what is actually sent.",
                cmd.Name, cmd.ReportId, payload[0]);
        }

        if (max > 0 && payload.Length < max)
        {
            var padded = new byte[max];
            Array.Copy(payload, padded, payload.Length);
            return padded;
        }

        return payload;
    }

    /// <summary>
    /// Send a single command. When <paramref name="dryRun"/> is true, the buffer is
    /// logged but never written. Returns the exact bytes that were (or would be) sent.
    /// </summary>
    public byte[] Send(HidCommand cmd, bool dryRun)
    {
        var payload = cmd.PayloadBytes;
        var buffer = BuildBuffer(cmd, payload);

        if (dryRun)
        {
            _log.Information("[DRY-RUN] {Type} report '{Name}' ({Len} bytes): {Hex}",
                cmd.ReportType, cmd.Name, buffer.Length, HexUtil.ToHex(buffer));
            return buffer;
        }

        EnsureOpen();

        switch (cmd.ReportType)
        {
            case ReportType.Output:
                _stream!.Write(buffer);
                break;
            case ReportType.Feature:
                _stream!.SetFeature(buffer);
                break;
            default:
                throw new InvalidOperationException($"Unsupported report type {cmd.ReportType}.");
        }

        _log.Information("Sent {Type} report '{Name}' ({Len} bytes): {Hex}",
            cmd.ReportType, cmd.Name, buffer.Length, HexUtil.ToHex(buffer));
        return buffer;
    }

    /// <summary>
    /// Read input reports for up to <paramref name="duration"/>. Returns the raw reports read.
    /// Useful for understanding what the device emits (e.g. while toggling settings).
    /// </summary>
    public IReadOnlyList<byte[]> ReadInputReports(TimeSpan duration, CancellationToken ct = default)
    {
        EnsureOpen();
        var reports = new List<byte[]>();
        var deadline = DateTime.UtcNow + duration;
        var buffer = new byte[Math.Max(_info.MaxInputReportLength, 64)];

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                int count = _stream!.Read(buffer, 0, buffer.Length);
                if (count > 0)
                {
                    var report = new byte[count];
                    Array.Copy(buffer, report, count);
                    reports.Add(report);
                    _log.Information("Input report ({Len} bytes): {Hex}", count, HexUtil.ToHex(report));
                }
            }
            catch (TimeoutException)
            {
                // Normal when the device is idle — keep polling until the deadline.
            }
            catch (IOException ex)
            {
                _log.Warning(ex, "I/O error while reading input reports; stopping read loop.");
                break;
            }
        }

        return reports;
    }

    private void EnsureOpen()
    {
        if (_stream is null)
            throw new InvalidOperationException("HID device is not open. Call Open() first.");
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
