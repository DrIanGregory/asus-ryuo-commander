using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RyuoBrightnessFix.Models;

namespace RyuoBrightnessFix.Views;

/// <summary>
/// Maps a <see cref="VideoScaleMode"/> to the WPF <see cref="Stretch"/> that reproduces how
/// the panel will show the encoded result: Fill = crop to cover (UniformToFill),
/// Fit = letterbox (Uniform), Stretch = distort (Fill).
/// </summary>
public sealed class ScaleModeToStretchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            VideoScaleMode.Fill => Stretch.UniformToFill,
            VideoScaleMode.Stretch => Stretch.Fill,
            _ => Stretch.Uniform,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
