using System.Globalization;
using System.Text;

namespace RyuoBrightnessFix.Util;

/// <summary>
/// Tolerant hex parsing/formatting helpers. Accepts the many shapes a human
/// copies out of Wireshark / a hex editor / Armoury Crate logs.
/// </summary>
public static class HexUtil
{
    /// <summary>
    /// Parse a hex string into bytes. Tolerant of: spaces, tabs, newlines, commas,
    /// colons, dashes, and "0x" prefixes. "00 AA BB", "00AABB", "0x00,0xAA",
    /// "00:aa:bb" all parse identically.
    /// </summary>
    public static byte[] ParseHex(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Array.Empty<byte>();

        var sb = new StringBuilder(input.Length);
        // Strip "0x"/"0X" prefixes first (token-aware enough for our separators).
        var cleaned = input.Replace("0x", " ", StringComparison.OrdinalIgnoreCase);
        foreach (var ch in cleaned)
        {
            if (Uri.IsHexDigit(ch))
                sb.Append(ch);
            else if (ch is ' ' or '\t' or '\r' or '\n' or ',' or ':' or '-' or ';' or '_')
                continue; // separator
            else
                throw new FormatException($"Invalid character '{ch}' in hex string.");
        }

        var hex = sb.ToString();
        if (hex.Length % 2 != 0)
            throw new FormatException(
                $"Hex string has an odd number of nibbles ({hex.Length}). " +
                "Each byte needs two hex digits.");

        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return result;
    }

    /// <summary>Format bytes as space-separated uppercase hex ("00 AA BB").</summary>
    public static string ToHex(IReadOnlyList<byte> bytes)
    {
        if (bytes.Count == 0) return "(empty)";
        var sb = new StringBuilder(bytes.Count * 3);
        for (int i = 0; i < bytes.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a number that may be hex ("0x0B05") or decimal ("2821").
    /// Returns null for null/empty/unparseable input (e.g. "AUTO_OR_USER_FILLED").
    /// </summary>
    public static int? TryParseNumber(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim();
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(input.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hv)
                ? hv : null;
        }

        return int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv) ? dv : null;
    }
}
