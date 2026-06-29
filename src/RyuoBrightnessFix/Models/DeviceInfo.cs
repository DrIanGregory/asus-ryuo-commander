namespace RyuoBrightnessFix.Models;

/// <summary>
/// Flattened, printable / serializable snapshot of a HID device.
/// One DeviceInfo may correspond to one top-level usage collection on the interface.
/// </summary>
public sealed class DeviceInfo
{
    public int VendorId { get; set; }
    public int ProductId { get; set; }
    public string VendorIdHex => $"0x{VendorId:X4}";
    public string ProductIdHex => $"0x{ProductId:X4}";

    public int? UsagePage { get; set; }
    public int? Usage { get; set; }
    public string? UsagePageHex => UsagePage is null ? null : $"0x{UsagePage:X4}";
    public string? UsageHex => Usage is null ? null : $"0x{Usage:X4}";

    public string? DevicePath { get; set; }
    public string? Manufacturer { get; set; }
    public string? ProductName { get; set; }
    public string? SerialNumber { get; set; }

    public int MaxInputReportLength { get; set; }
    public int MaxOutputReportLength { get; set; }
    public int MaxFeatureReportLength { get; set; }

    /// <summary>Heuristic: does this look like an ASUS / ROG / Ryuo device?</summary>
    public bool LikelyAsus { get; set; }

    /// <summary>One-line label for combo boxes / pickers (distinguishes sibling interfaces).</summary>
    public string DisplayName
    {
        get
        {
            var parts = new List<string>
            {
                ProductName ?? "(unnamed)",
                $"{VendorIdHex}/{ProductIdHex}",
                $"out {MaxOutputReportLength}B",
            };
            var iface = InterfaceTag;
            if (iface.Length > 0) parts.Add(iface);
            if (UsagePageHex is not null) parts.Add($"UP {UsagePageHex}");
            if (LikelyAsus) parts.Add("ASUS");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>The "mi_NN" / "colNN" tokens from the device path, to tell sibling interfaces apart.</summary>
    public string InterfaceTag
    {
        get
        {
            if (string.IsNullOrEmpty(DevicePath)) return "";
            var tokens = new List<string>();
            var mi = System.Text.RegularExpressions.Regex.Match(DevicePath, "mi_[0-9a-fA-F]{2}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mi.Success) tokens.Add(mi.Value.ToLowerInvariant());
            var col = System.Text.RegularExpressions.Regex.Match(DevicePath, "col[0-9]{2}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (col.Success) tokens.Add(col.Value.ToLowerInvariant());
            return string.Join(" ", tokens);
        }
    }

    // Used by the device ComboBox (its custom template falls back to ToString()).
    public override string ToString() => DisplayName;

    public string ToConsoleBlock()
    {
        var lines = new List<string>
        {
            $"  VID/PID        : {VendorIdHex} / {ProductIdHex}{(LikelyAsus ? "   <-- likely ASUS/ROG" : "")}",
            $"  Usage Page     : {UsagePageHex ?? "(unknown)"}",
            $"  Usage          : {UsageHex ?? "(unknown)"}",
            $"  Manufacturer   : {Manufacturer ?? "(none)"}",
            $"  Product        : {ProductName ?? "(none)"}",
            $"  Serial         : {SerialNumber ?? "(none)"}",
            $"  Report lengths : in={MaxInputReportLength} out={MaxOutputReportLength} feature={MaxFeatureReportLength}",
            $"  Path           : {DevicePath}",
        };
        return string.Join(Environment.NewLine, lines);
    }
}
