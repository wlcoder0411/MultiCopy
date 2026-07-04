using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MultiCopy.Infrastructure;

/// <summary>
/// 字符串转可见性：null 或空字符串 → Visible（显示 Watermark），非空 → Collapsed。
/// 用于 TextBox Watermark 占位文字的显示/隐藏。
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public static StringToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
