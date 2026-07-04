using System.Globalization;
using System.Windows.Data;

namespace MultiCopy.Infrastructure;

/// <summary>分组展开状态 → 箭头字符：展开▼ / 折叠▶。</summary>
public sealed class ExpandArrowConverter : IValueConverter
{
    public static ExpandArrowConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "▼" : "▶";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
