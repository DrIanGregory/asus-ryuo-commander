using RyuoBrightnessFix.Models;
using RyuoBrightnessFix.Util;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Pure logic that turns two captured HID reports (one at a HIGH brightness, one at a
/// LOW brightness) into a calibrated <see cref="BrightnessControl"/> template:
///   1. diff the two reports to find the byte(s) that changed,
///   2. pick the brightness byte,
///   3. extrapolate the 0% and 100% raw values from the two known points.
///
/// No device I/O here — easy to reason about and test.
/// </summary>
public sealed class CalibrationService
{
    public sealed class Result
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<int> DifferingOffsets { get; init; } = Array.Empty<int>();
        public int ChosenOffset { get; init; } = -1;
        public BrightnessControl? Control { get; init; }
    }

    /// <param name="highHex">Report bytes captured at <paramref name="highPercent"/> brightness.</param>
    /// <param name="lowHex">Report bytes captured at <paramref name="lowPercent"/> brightness.</param>
    /// <param name="forcedOffset">If set, use this byte offset instead of auto-picking.</param>
    public Result Detect(string highHex, string lowHex, int highPercent, int lowPercent,
                         ReportType reportType, int? forcedOffset = null)
    {
        byte[] high, low;
        try
        {
            high = HexUtil.ParseHex(highHex);
            low = HexUtil.ParseHex(lowHex);
        }
        catch (FormatException ex)
        {
            return Fail("Could not parse the hex: " + ex.Message);
        }

        if (high.Length == 0)
            return Fail("Paste the captured report bytes.");

        // ASUS Ryuo screen protocol (JSON 'opacity') — replay, not byte-substitution. Only needs HIGH.
        if (RyuoScreenProtocol.Matches(high))
            return BuildScreenResult(high);
        if (low.Length > 0 && RyuoScreenProtocol.Matches(low))
            return BuildScreenResult(low);

        if (low.Length == 0)
            return Fail("Paste both reports (the bytes captured at high and low brightness).");
        if (high.Length != low.Length)
            return Fail($"The two reports have different lengths ({high.Length} vs {low.Length}). " +
                        "Capture the same report at both brightness levels.");
        if (highPercent == lowPercent)
            return Fail("The high and low brightness percentages must be different.");

        var offsets = new List<int>();
        for (int i = 0; i < high.Length; i++)
            if (high[i] != low[i]) offsets.Add(i);

        if (offsets.Count == 0)
            return Fail("The two reports are identical — nothing changed. Make sure you captured " +
                        "the report that carries the brightness value at two different levels.");

        int chosen = ChooseOffset(offsets, high, low, highPercent, lowPercent, forcedOffset);

        // Extrapolate raw byte values at 0% and 100% from the two captured points.
        double slope = (high[chosen] - low[chosen]) / (double)(highPercent - lowPercent);
        int rawAt0 = ClampByte((int)Math.Round(low[chosen] + slope * (0 - lowPercent)));
        int rawAt100 = ClampByte((int)Math.Round(low[chosen] + slope * (100 - lowPercent)));

        // Build the template from the HIGH report (a real, valid full report) with {b} at the offset.
        string template = BuildTemplate(high, chosen);

        var control = new BrightnessControl
        {
            ReportType = reportType,
            ReportId = high[0],
            HexTemplate = template,
            Scale = BrightnessScale.Raw0To255, // overridden by the explicit raw bounds below
            RawAtMin = rawAt0,
            RawAtMax = rawAt100,
            DelayMs = 0,
        };

        string extra = offsets.Count > 1
            ? $" ({offsets.Count} bytes changed; using offset {chosen} — pick another if the test fails)"
            : "";

        return new Result
        {
            Success = true,
            Message = $"Brightness byte at offset {chosen}: {low[chosen]:X2}@{lowPercent}% → {high[chosen]:X2}@{highPercent}%. " +
                      $"Maps 0%→{rawAt0:X2}, 100%→{rawAt100:X2}.{extra}",
            DifferingOffsets = offsets,
            ChosenOffset = chosen,
            Control = control,
        };
    }

    /// <summary>
    /// From two sets of captured OUT reports (one captured while setting HIGH brightness,
    /// one while setting LOW), find the single report pair that looks like the brightness
    /// command: same length, identical except for the byte(s) that carry brightness.
    /// Returns the two reports as hex strings (ready to feed into <see cref="Detect"/>),
    /// or null if nothing plausible is found.
    /// </summary>
    public (string HighHex, string LowHex)? FindBrightnessPair(
        IReadOnlyList<byte[]> highReports, IReadOnlyList<byte[]> lowReports,
        int highPercent, int lowPercent)
    {
        int pctDir = Math.Sign(highPercent - lowPercent);

        (byte[] h, byte[] l)? best = null;
        // Lower is better: (diffCount, directionMismatch, length).
        // Prefer the fewest changed bytes, then a byte that moves WITH brightness, then the
        // SHORTER report — the brightness opcode is a small control packet, not the big
        // 1024-byte image/config blobs that also fly past on this device.
        (int diff, int mismatch, int len) bestScore = (int.MaxValue, int.MaxValue, int.MaxValue);

        foreach (var h in highReports)
        {
            foreach (var l in lowReports)
            {
                if (h.Length != l.Length || h.Length == 0) continue;

                int diffs = 0, lastOffset = -1;
                for (int i = 0; i < h.Length; i++)
                    if (h[i] != l[i]) { diffs++; lastOffset = i; }

                if (diffs == 0) continue; // identical — not the brightness command

                // Direction check only meaningful for a single changed byte.
                int mismatch = 1;
                if (diffs == 1)
                    mismatch = Math.Sign(h[lastOffset] - l[lastOffset]) == pctDir ? 0 : 1;

                var score = (diffs, mismatch, h.Length);
                if (Less(score, bestScore))
                {
                    bestScore = score;
                    best = (h, l);
                }
            }
        }

        if (best is null) return null;
        return (HexUtil.ToHex(best.Value.h), HexUtil.ToHex(best.Value.l));
    }

    private static bool Less((int, int, int) a, (int, int, int) b)
    {
        if (a.Item1 != b.Item1) return a.Item1 < b.Item1;
        if (a.Item2 != b.Item2) return a.Item2 < b.Item2;
        return a.Item3 < b.Item3;
    }

    /// <summary>Find the first captured report that is an ASUS Ryuo screen command, if any.</summary>
    public byte[]? FindScreenTemplate(IReadOnlyList<byte[]> reports)
        => reports.FirstOrDefault(r => RyuoScreenProtocol.Matches(r) && RyuoScreenProtocol.ReadOpacity(r) is not null);

    private static Result BuildScreenResult(byte[] template)
    {
        int? opacity = RyuoScreenProtocol.ReadOpacity(template);
        var control = new BrightnessControl
        {
            Mode = BrightnessMode.RyuoScreen,
            ReportType = ReportType.Output,
            ReportId = template[0],
            HexTemplate = HexUtil.ToHex(template),
            FreshSequence = true,
        };
        return new Result
        {
            Success = true,
            Message = $"Detected the ASUS Ryuo screen command (JSON opacity = {opacity?.ToString() ?? "?"}%). " +
                      "Brightness is restored by resending this exact command with a fresh sequence number — " +
                      "variable levels aren't possible on this device (the packet has a checksum we can't recompute).",
            DifferingOffsets = Array.Empty<int>(),
            ChosenOffset = -1,
            Control = control,
        };
    }

    /// <summary>Build the full brightness-100 sequence for a calibrated control.</summary>
    public static List<HidCommand> Build100Sequence(BrightnessControl control)
    {
        var cmd = control.ToCommand(100);
        cmd.Name = "Set LCD brightness 100 (calibrated)";
        return new List<HidCommand> { cmd };
    }

    private static int ChooseOffset(List<int> offsets, byte[] high, byte[] low,
                                    int highPct, int lowPct, int? forced)
    {
        if (forced is int f && offsets.Contains(f)) return f;
        if (offsets.Count == 1) return offsets[0];

        int pctDir = Math.Sign(highPct - lowPct);

        // Prefer a byte whose value moves the same direction as brightness, with the biggest delta.
        return offsets
            .OrderByDescending(o => Math.Sign(high[o] - low[o]) == pctDir ? 1 : 0)
            .ThenByDescending(o => Math.Abs(high[o] - low[o]))
            .First();
    }

    private static string BuildTemplate(byte[] baseReport, int offset)
    {
        var parts = new string[baseReport.Length];
        for (int i = 0; i < baseReport.Length; i++)
            parts[i] = i == offset ? BrightnessControl.PlaceholderToken : baseReport[i].ToString("X2");
        return string.Join(" ", parts);
    }

    private static int ClampByte(int v) => Math.Clamp(v, 0, 255);

    private static Result Fail(string message) => new() { Success = false, Message = message };
}
