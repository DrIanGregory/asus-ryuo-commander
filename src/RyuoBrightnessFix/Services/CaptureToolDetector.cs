using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RyuoBrightnessFix.Services;

/// <summary>Detects whether the optional capture tools (USBPcap / Wireshark) are present,
/// so the Setup tab can tailor its guidance.</summary>
[SupportedOSPlatform("windows")]
public static class CaptureToolDetector
{
    public const string UsbPcapDownloadUrl = "https://desowin.org/usbpcap/";
    public const string WiresharkDownloadUrl = "https://www.wireshark.org/download.html";

    public static bool IsUsbPcapInstalled()
    {
        // Driver service registration is the most reliable signal.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBPcap");
            if (key is not null) return true;
        }
        catch { /* ignore */ }

        foreach (var p in new[]
                 {
                     @"C:\Program Files\USBPcap\USBPcapCMD.exe",
                     @"C:\Program Files (x86)\USBPcap\USBPcapCMD.exe",
                 })
        {
            try { if (File.Exists(p)) return true; } catch { /* ignore */ }
        }
        return false;
    }
}
