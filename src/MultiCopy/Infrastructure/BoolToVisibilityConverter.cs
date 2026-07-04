using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MultiCopy.Infrastructure;

/// <summary>bool 转可见性：true→Visible，false→Collapsed。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static BoolToVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
