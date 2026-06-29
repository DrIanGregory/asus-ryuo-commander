using System.Globalization;
using System.Windows.Data;

namespace RyuoBrightnessFix.Util;

/// <summary>Bool → one of two strings (e.g. a "done" vs "pending" glyph).</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "✔";
    public string FalseText { get; set; } = "○";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? TrueText : FalseText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
