using System.Windows;

namespace MultiCopy.Services;

/// <summary>
/// 主题切换服务。通过替换 Application.Resources.MergedDictionaries 中的色板字典实现运行时切换。
/// 所有引用语义 Token 的 XAML 用 DynamicResource，切换时自动刷新。
/// 不持久化：每次启动默认暗色（CurrentTheme 初始 Dark）。
/// </summary>
public enum AppTheme { Dark, Light }

public static class ThemeService
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static event EventHandler? ThemeChanged;

    public static void Toggle() => Set(CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    public static void Set(AppTheme mode)
    {
        if (mode == CurrentTheme) return;
        CurrentTheme = mode;

        var md = Application.Current.Resources.MergedDictionaries;
        var newSource = new Uri($"pack://application:,,,/Themes/{mode}.xaml", UriKind.Absolute);

        for (int i = 0; i < md.Count; i++)
        {
            var src = md[i].Source?.OriginalString ?? "";
            if (src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                md[i] = new ResourceDictionary { Source = newSource };
                break;
            }
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
