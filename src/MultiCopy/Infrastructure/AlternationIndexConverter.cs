using System.Globalization;
using System.Windows.Data;

namespace MultiCopy.Infrastructure;

/// <summary>把 ItemsControl.AlternationIndex（0 基）转为 1 基显示序号。</summary>
public sealed class AlternationIndexConverter : IValueConverter
{
    public static AlternationIndexConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i ? (i + 1).ToString() : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
