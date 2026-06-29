using RyuoBrightnessFix.Models;
using RyuoBrightnessFix.Util;
using Serilog;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// High-level operations: resolve the configured device, open it, and send HID
/// reports (the 100% sequence, or a slider-driven brightness percent), honouring
/// dry-run and per-command delays.
/// </summary>
public sealed class BrightnessFixer
{
    private readonly DeviceDiscoveryService _discovery;
    private readonly ILogger _log;

    public BrightnessFixer(DeviceDiscoveryService discovery, ILogger log)
    {
        _discovery = discovery;
        _log = log.ForContext<BrightnessFixer>();
    }

    /// <summary>Send the full brightness-100 sequence (optionally just one named command).</summary>
    public bool SendSequence(RyuoConfig config, bool dryRun, string? onlyName = null, CancellationToken ct = default)
    {
        var commands = config.Brightness100Sequence;
        if (onlyName is not null)
        {
            commands = commands
                .Where(c => string.Equals(c.Name, onlyName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (commands.Count == 0)
            {
                _log.Error("No command named '{Name}' found in brightness100Sequence.", onlyName);
                return false;
            }
        }

        if (commands.Count == 0)
        {
            _log.Error("brightness100Sequence is empty. Capture the real command first (see README, Step B).");
            return false;
        }

        return SendCommands(config, commands, dryRun, ct).Ok;
    }

    /// <summary>
    /// Set the LCD to a 0–100% brightness. Uses the templated
    /// <see cref="RyuoConfig.BrightnessControl"/> when present; otherwise only 100% is
    /// supported (falls back to the brightness-100 sequence).
    /// </summary>
    public bool SetBrightness(RyuoConfig config, int percent, bool dryRun, CancellationToken ct = default)
        => SetBrightnessReport(config, percent, dryRun, ct).Ok;

    /// <summary>
    /// Like <see cref="SetBrightness"/> but returns a human-readable outcome message for the UI
    /// (which bytes were sent, or exactly why it failed).
    /// </summary>
    public (bool Ok, string Message) SetBrightnessReport(RyuoConfig config, int percent, bool dryRun, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);

        if (config.BrightnessControl is null)
        {
            if (percent == 100)
                return SendCommands(config, config.Brightness100Sequence, dryRun, ct);

            const string msg = "No brightness template configured, so only 100% is available.";
            _log.Error(msg);
            return (false, msg);
        }

        HidCommand cmd;
        try
        {
            cmd = config.BrightnessControl.ToCommand(percent);

            // Ryuo screen protocol: replay the captured JSON-opacity command, with a fresh
            // SeqNumber so the device doesn't treat it as a duplicate and ignore it.
            if (config.BrightnessControl.Mode == BrightnessMode.RyuoScreen)
            {
                var template = HexUtil.ParseHex(cmd.Hex);
                var bytes = RyuoScreenProtocol.BuildResend(template, config.BrightnessControl.FreshSequence);
                cmd.Hex = HexUtil.ToHex(bytes);
                int? cap = RyuoScreenProtocol.ReadOpacity(template);
                if (cap is int o && o != percent)
                    _log.Information("Ryuo screen command carries opacity {Opacity}% (variable brightness isn't " +
                                     "supported on this device; resending to restore that level).", o);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not build the brightness command for {Percent}%.", percent);
            return (false, "Could not build the command: " + ex.Message);
        }

        return SendCommands(config, new[] { cmd }, dryRun, ct);
    }

    /// <summary>Resolve + open the device and send the given commands in order.</summary>
    private (bool Ok, string Message) SendCommands(RyuoConfig config, IReadOnlyList<HidCommand> commands, bool dryRun, CancellationToken ct)
    {
        if (commands.Count == 0)
            return (false, "Nothing to send — no command configured.");

        DeviceInfo info;
        HidSharp.HidDevice device;
        try
        {
            (info, device) = _discovery.ResolveSingle(config.Device);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not resolve the target device.");
            return (false, "Device not found: " + ex.Message);
        }

        _log.Information("Target device: {Vid}/{Pid} '{Product}'", info.VendorIdHex, info.ProductIdHex, info.ProductName);
        if (dryRun)
            _log.Warning("DRY-RUN mode: commands will be logged but NOT written to the device.");

        using var session = new HidDeviceSession(device, info, _log);
        try
        {
            if (!dryRun) session.Open();

            var sent = new List<string>();
            for (int i = 0; i < commands.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cmd = commands[i];
                _log.Information("[{Index}/{Total}] {Name}", i + 1, commands.Count, cmd.Name);
                var buffer = session.Send(cmd, dryRun);
                sent.Add(HexUtil.ToHex(buffer));

                if (cmd.DelayMs > 0)
                    Task.Delay(cmd.DelayMs, ct).Wait(ct);
            }

            _log.Information("Sequence completed ({Count} command(s), dryRun={DryRun}).", commands.Count, dryRun);
            string verb = dryRun ? "Would send" : "Sent";
            return (true, $"{verb} to {info.ProductName ?? "device"}: {string.Join("  |  ", sent)}");
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Send cancelled.");
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send command(s).");
            return (false, ex.Message);
        }
    }
}
