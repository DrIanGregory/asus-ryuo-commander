using System.Text;
using System.Text.RegularExpressions;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// The ASUS ROG Ryuo IV LCD is driven by an HTTP-like command over HID:
///
///   [reportId 0x5A][2-byte big-endian length][ "POST waterBlockScreenId 1\r\n
///   SeqNumber=NNNN\r\nDate=...\r\nContentType=json\r\nContentLength=LLL\r\n\r\n" + {JSON} ][2-byte trailer]
///
/// Brightness is the JSON field <c>"filter":{"opacity":N}</c> (N = 0..100). This is NOT a single
/// byte, so the byte-offset approach can't drive it.
///
/// We can't recompute the 2-byte trailer (an unknown checksum/CRC) from a single capture, so we do
/// NOT synthesize new brightness values byte-by-byte. Instead we REPLAY the exact command Armoury
/// Crate sent (which already carries the captured opacity), optionally giving it a fresh same-width
/// <c>SeqNumber</c> so the device doesn't dedupe it as a duplicate. That restores the captured
/// brightness — which is exactly the "make it bright again after resume" fix.
/// </summary>
public static class RyuoScreenProtocol
{
    public const string Marker = "waterBlockScreenId";

    private static readonly Regex SeqRegex = new(@"SeqNumber=(\d+)", RegexOptions.Compiled);
    private static readonly Regex OpacityRegex = new("\"opacity\":(\\d+)", RegexOptions.Compiled);

    private static readonly object SeqLock = new();
    private static int _seqCounter = -1;

    /// <summary>True if these bytes look like the Ryuo screen command.</summary>
    public static bool Matches(byte[] report)
        => report.Length > 0 && Decode(report).Contains(Marker, StringComparison.Ordinal);

    /// <summary>Read the brightness (opacity 0–100) carried by a captured command, if present.</summary>
    public static int? ReadOpacity(byte[] report)
    {
        var m = OpacityRegex.Match(Decode(report));
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    public static int? ReadSeq(byte[] report)
    {
        var m = SeqRegex.Match(Decode(report));
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    /// <summary>Readable rendering of ANY captured report (printable chars kept, others shown as '.'),
    /// trimmed of trailing zero padding — for diagnostic logging of unknown commands.</summary>
    public static string DescribeAny(byte[] report)
    {
        int end = report.Length;
        while (end > 0 && report[end - 1] == 0) end--;

        var sb = new StringBuilder(end + 16);
        for (int i = 0; i < end; i++)
        {
            byte b = report[i];
            if (b == 0x0D) sb.Append("\\r");
            else if (b == 0x0A) sb.Append("\\n");
            else if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else sb.Append('.');
        }
        return sb.ToString();
    }

    /// <summary>The readable POST+JSON text of a captured command (trimmed at the zero padding), for logging.</summary>
    public static string DescribeForLog(byte[] report)
    {
        string s = Decode(report);
        int postIdx = s.IndexOf("POST", StringComparison.Ordinal);
        string text = postIdx >= 0 ? s[postIdx..] : s;
        int nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        return text.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    /// <summary>
    /// Produce a report to send. With <paramref name="freshSequence"/> the SeqNumber is bumped to a
    /// new value of the SAME digit width (so total length, ContentLength and the trailer stay byte-
    /// identical and valid); otherwise the captured bytes are replayed verbatim.
    /// </summary>
    public static byte[] BuildResend(byte[] template, bool freshSequence)
    {
        if (!freshSequence)
            return (byte[])template.Clone();

        string s = Decode(template);
        var m = SeqRegex.Match(s);
        if (!m.Success)
            return (byte[])template.Clone();

        int width = m.Groups[1].Value.Length;
        int baseSeq = int.Parse(m.Groups[1].Value);
        int next = NextSeq(baseSeq, width);

        // Keep EXACTLY the same number of digits so nothing else in the packet shifts.
        string seqStr = next.ToString().PadLeft(width, '0');
        if (seqStr.Length > width) seqStr = seqStr[^width..];

        int idx = m.Groups[1].Index;
        string rebuilt = s[..idx] + seqStr + s[(idx + width)..];
        return Encoding.Latin1.GetBytes(rebuilt); // identical length to the template
    }

    private static int NextSeq(int baseSeq, int width)
    {
        lock (SeqLock)
        {
            if (_seqCounter < 0) _seqCounter = baseSeq;
            _seqCounter++;
            int mod = (int)Math.Pow(10, width);
            int v = ((_seqCounter % mod) + mod) % mod;
            if (v == 0) v = 1; // avoid an all-zero sequence
            return v;
        }
    }

    // Latin1 is a loss-less 1 byte ⇄ 1 char mapping, so we can edit the ASCII parts and re-encode
    // without disturbing the binary header/trailer bytes.
    private static string Decode(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
