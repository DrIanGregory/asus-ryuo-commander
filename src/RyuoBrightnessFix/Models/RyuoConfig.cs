using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyuoBrightnessFix.Models;

/// <summary>
/// Root configuration object persisted as JSON (e.g. ryuo.json).
/// Hand-edited by the user after capturing the real brightness command.
/// </summary>
public sealed class RyuoConfig
{
    public DeviceFilter Device { get; set; } = new();

    /// <summary>
    /// The ordered list of HID reports that, when sent, set the LCD back to 100% brightness.
    /// Discovered via USBPcap/Wireshark capture (see README, Step B). Used by the
    /// "Restore 100%" action and as the fallback when no <see cref="BrightnessControl"/> exists.
    /// </summary>
    public List<HidCommand> Brightness100Sequence { get; set; } = new();

    /// <summary>
    /// Optional templated command that drives the brightness SLIDER (variable brightness).
    /// When present, the UI slider and the resume handler send this with the chosen percent.
    /// When null, only 100% is available (via <see cref="Brightness100Sequence"/>).
    /// </summary>
    public BrightnessControl? BrightnessControl { get; set; }

    /// <summary>Milliseconds to wait after a resume event before sending the sequence.</summary>
    public int ResumeDelayMs { get; set; } = 10_000;

    /// <summary>
    /// When true, commands are logged but never written to the device.
    /// Overridden by the --execute / --dry-run command-line flags.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Set true once the user has run the calibration wizard and visually confirmed the
    /// brightness command actually changes the LCD. Drives the "Calibrated" status.
    /// </summary>
    public bool Verified { get; set; }

    /// <summary>True when there is at least one usable brightness command configured.</summary>
    public bool HasBrightnessCommand =>
        BrightnessControl is not null || Brightness100Sequence.Count > 0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static RyuoConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}", path);

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<RyuoConfig>(json, JsonOptions)
                  ?? throw new InvalidDataException($"Config file deserialized to null: {path}");
        cfg.Validate();
        return cfg;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Structural validation. Throws <see cref="InvalidDataException"/> on problems.</summary>
    public void Validate()
    {
        if (Device is null)
            throw new InvalidDataException("Config 'device' section is missing.");

        for (int i = 0; i < Brightness100Sequence.Count; i++)
        {
            var c = Brightness100Sequence[i];
            if (string.IsNullOrWhiteSpace(c.Hex))
                throw new InvalidDataException($"Command #{i} ('{c.Name}') has empty hex payload.");
            if (c.ReportType is not (ReportType.Output or ReportType.Feature))
                throw new InvalidDataException($"Command #{i} ('{c.Name}') has invalid reportType.");
            // Force a parse so malformed hex fails fast at load time.
            _ = c.PayloadBytes;
        }
    }
}

public sealed class DeviceFilter
{
    /// <summary>Vendor ID as hex ("0x0B05") or decimal. ASUS = 0x0B05.</summary>
    public string? VendorId { get; set; }

    /// <summary>Product ID as hex ("0x1234") or decimal. May be "AUTO_OR_USER_FILLED" / null to match any.</summary>
    public string? ProductId { get; set; }

    public int? UsagePage { get; set; }
    public int? Usage { get; set; }

    /// <summary>Case-insensitive substring matched against the HID device path.</summary>
    public string? PathContains { get; set; }

    public string? SerialNumber { get; set; }

    public int? VendorIdValue => Util.HexUtil.TryParseNumber(VendorId);
    public int? ProductIdValue => Util.HexUtil.TryParseNumber(ProductId);
}

public enum ReportType
{
    Output,
    Feature,
}

public sealed class HidCommand
{
    public string Name { get; set; } = "unnamed";

    public ReportType ReportType { get; set; } = ReportType.Output;

    /// <summary>
    /// Report ID. Informational/validation only — the actual ID sent to the device is the
    /// first byte of <see cref="Hex"/>. Use 0 if the device has no report IDs.
    /// </summary>
    public int ReportId { get; set; }

    /// <summary>
    /// The complete report buffer as a hex string, INCLUDING the leading report-ID byte.
    /// Accepts spaces, commas, 0x prefixes, colons. Example: "00 AA BB CC".
    /// </summary>
    public string Hex { get; set; } = "";

    /// <summary>Milliseconds to sleep after this command before the next one.</summary>
    public int DelayMs { get; set; }

    [JsonIgnore]
    public byte[] PayloadBytes => Util.HexUtil.ParseHex(Hex);
}

/// <summary>How a 0–100% brightness maps to the raw byte written into the report.</summary>
public enum BrightnessScale
{
    /// <summary>0% → 0x00, 100% → 0xFF (linear over a full byte).</summary>
    Raw0To255,

    /// <summary>0% → 0, 100% → 100 (the percent value is the raw byte).</summary>
    Raw0To100,
}

/// <summary>How the brightness command is produced.</summary>
public enum BrightnessMode
{
    /// <summary>Substitute a single brightness byte into a fixed report (the <c>{b}</c> template).</summary>
    ByteValue,

    /// <summary>
    /// Replay the captured ASUS Ryuo screen command (an HTTP-like POST with a JSON <c>opacity</c>
    /// field). The full captured report is the template; brightness is restored by resending it.
    /// </summary>
    RyuoScreen,
}

/// <summary>
/// A templated brightness command. The <see cref="HexTemplate"/> contains a single
/// placeholder token <c>{b}</c> (case-insensitive) that is replaced by the two-hex-digit
/// brightness byte computed from the requested percent.
///
/// Example: <c>"00 31 {b} 64"</c> with <see cref="Scale"/> = Raw0To255 sends
/// <c>00 31 80 64</c> at 50% and <c>00 31 FF 64</c> at 100%.
/// </summary>
public sealed class BrightnessControl
{
    public const string PlaceholderToken = "{b}";

    public ReportType ReportType { get; set; } = ReportType.Output;
    public int ReportId { get; set; }
    public string HexTemplate { get; set; } = "";
    public BrightnessScale Scale { get; set; } = BrightnessScale.Raw0To255;

    /// <summary>How the command is built. RyuoScreen replays the captured JSON-opacity command.</summary>
    public BrightnessMode Mode { get; set; } = BrightnessMode.ByteValue;

    /// <summary>(RyuoScreen) give each resend a fresh SeqNumber so the device doesn't ignore a duplicate.</summary>
    public bool FreshSequence { get; set; } = true;

    /// <summary>True only when this control can produce arbitrary brightness levels (byte mode).</summary>
    [JsonIgnore]
    public bool SupportsVariable => Mode == BrightnessMode.ByteValue;

    /// <summary>Optional explicit raw byte at 0% (overrides <see cref="Scale"/> when both raw bounds are set).</summary>
    public int? RawAtMin { get; set; }

    /// <summary>Optional explicit raw byte at 100% (overrides <see cref="Scale"/> when both raw bounds are set).</summary>
    public int? RawAtMax { get; set; }

    public int DelayMs { get; set; }

    /// <summary>Compute the raw brightness byte for a 0–100% value.</summary>
    public byte RawForPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        double raw;
        if (RawAtMin is int lo && RawAtMax is int hi)
            raw = lo + (hi - lo) * (percent / 100.0);
        else
            raw = Scale switch
            {
                BrightnessScale.Raw0To255 => percent * 255.0 / 100.0,
                BrightnessScale.Raw0To100 => percent,
                _ => percent * 255.0 / 100.0,
            };

        return (byte)Math.Clamp((int)Math.Round(raw), 0, 255);
    }

    /// <summary>Materialise a concrete <see cref="HidCommand"/> for a 0–100% value.</summary>
    public HidCommand ToCommand(int percent)
    {
        if (string.IsNullOrWhiteSpace(HexTemplate))
            throw new InvalidDataException("brightnessControl.hexTemplate is empty.");

        // Ryuo screen protocol: the template IS the full captured command — replay it as-is.
        // (The fixer applies a fresh SeqNumber before sending.)
        if (Mode == BrightnessMode.RyuoScreen)
        {
            var bytes = Util.HexUtil.ParseHex(HexTemplate);
            return new HidCommand
            {
                Name = $"Restore Ryuo screen brightness",
                ReportType = ReportType,
                ReportId = bytes.Length > 0 ? bytes[0] : ReportId,
                Hex = HexTemplate,
                DelayMs = DelayMs,
            };
        }

        int idx = HexTemplate.IndexOf(PlaceholderToken, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            throw new InvalidDataException(
                $"brightnessControl.hexTemplate must contain the '{PlaceholderToken}' placeholder.");

        byte raw = RawForPercent(percent);
        string hex = HexTemplate[..idx] + raw.ToString("X2") + HexTemplate[(idx + PlaceholderToken.Length)..];

        return new HidCommand
        {
            Name = $"Set LCD brightness {percent}%",
            ReportType = ReportType,
            ReportId = ReportId,
            Hex = hex,
            DelayMs = DelayMs,
        };
    }
}
