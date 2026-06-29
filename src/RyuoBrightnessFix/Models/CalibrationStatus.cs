namespace RyuoBrightnessFix.Models;

/// <summary>Where the user is in getting the tool working. Drives the status badge and Setup tab.</summary>
public enum CalibrationStatus
{
    /// <summary>No target device resolved (not connected / filter wrong).</summary>
    NoDevice,

    /// <summary>Device found, but no brightness command captured yet.</summary>
    NeedsCapture,

    /// <summary>A command exists but the user hasn't confirmed it actually works.</summary>
    NeedsVerification,

    /// <summary>Device + command present and visually verified. The tool is ready.</summary>
    Calibrated,
}

public static class CalibrationStatusInfo
{
    public static string ToText(this CalibrationStatus s) => s switch
    {
        CalibrationStatus.NoDevice => "No device",
        CalibrationStatus.NeedsCapture => "Not calibrated",
        CalibrationStatus.NeedsVerification => "Needs verification",
        CalibrationStatus.Calibrated => "Calibrated",
        _ => "Unknown",
    };

    /// <summary>A hex colour for the badge (consumed by the view model, not WPF directly).</summary>
    public static string ToColorHex(this CalibrationStatus s) => s switch
    {
        CalibrationStatus.Calibrated => "#FF3FB950",       // green
        CalibrationStatus.NeedsVerification => "#FFD29922", // amber
        _ => "#FFE5534B",                                   // red
    };
}
