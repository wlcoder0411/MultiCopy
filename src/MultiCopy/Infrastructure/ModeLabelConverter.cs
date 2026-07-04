using System.Globalization;
using System.Windows.Data;

namespace MultiCopy.Infrastructure;

/// <summary>模式开关文字：true→"队列粘贴：开"，false→"队列粘贴：关"。</summary>
public sealed class ModeLabelConverter : IValueConverter
{
    public static ModeLabelConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? (b ? "队列粘贴：开" : "队列粘贴：关") : "队列粘贴：关";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
