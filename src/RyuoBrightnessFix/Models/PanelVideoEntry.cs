namespace RyuoBrightnessFix.Models;

/// <summary>
/// One persisted media-library entry. <see cref="File"/> is the on-device name the panel
/// plays; <see cref="SourcePath"/> and <see cref="ScaleMode"/> record where the video came
/// from and how it was transcoded, so a scale-mode change can re-encode the library from
/// the originals (the mode is baked into the encoded pixels — it cannot be recovered from
/// the device file). Both are null for entries saved before v1.9 and for stock preset
/// videos; those can only be re-scaled by re-adding them.
/// </summary>
public sealed class PanelVideoEntry
{
    /// <summary>Device-side file name (in /sdcard/pcMedia, or a stock preset name).</summary>
    public string File { get; set; } = "";

    /// <summary>The local file this entry was transcoded from; null when unknown.</summary>
    public string? SourcePath { get; set; }

    /// <summary>The scale mode baked into the device file; null when unknown.</summary>
    public VideoScaleMode? ScaleMode { get; set; }
}
