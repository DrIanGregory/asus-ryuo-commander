namespace RyuoBrightnessFix.Models;

/// <summary>Whether the Ryuo IV LCD is reachable for brightness control.</summary>
public enum DeviceStatusKind
{
    NoDevice,
    Connected,
}

public static class DeviceStatusKindInfo
{
    public static string ToText(this DeviceStatusKind s) => s switch
    {
        DeviceStatusKind.Connected => "Connected",
        _ => "No device",
    };

    /// <summary>A hex colour for the status badge.</summary>
    public static string ToColorHex(this DeviceStatusKind s) => s switch
    {
        DeviceStatusKind.Connected => "#FF3FB950", // green
        _ => "#FFE5534B",                          // red
    };
}
