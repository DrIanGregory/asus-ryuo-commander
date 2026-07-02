namespace RyuoBrightnessFix.Models;

/// <summary>
/// How a source video is fitted into the panel's 1920×960 encode frame. The panel itself
/// stretches whatever frame it receives across the full 2240×1080 screen (SurfaceFlinger
/// maps the buffer to the whole display, center-cropping ~3% of height), so bars baked
/// into the encoded pixels are the only reason a video ever looks smaller than the LCD.
/// </summary>
public enum VideoScaleMode
{
    /// <summary>Crop the source to the panel's shape and fill every pixel (no bars).</summary>
    Fill,

    /// <summary>Show the whole source, padded with black bars where the shape differs.</summary>
    Fit,

    /// <summary>Distort the source to the panel's shape (no bars, no cropping).</summary>
    Stretch,
}
