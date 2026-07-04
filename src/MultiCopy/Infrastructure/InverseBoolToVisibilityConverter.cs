using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MultiCopy.Infrastructure;

/// <summary>bool 反转转可见性：true→Collapsed，false→Visible。（用于"为空时隐藏区块"）</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static InverseBoolToVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        return b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
