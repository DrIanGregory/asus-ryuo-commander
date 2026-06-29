using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RyuoBrightnessFix.Util;

/// <summary>Bool → Visibility, with an optional <see cref="Invert"/> for "show when false".</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
