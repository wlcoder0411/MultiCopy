using System.Windows;

namespace MultiCopy.Infrastructure;

/// <summary>
/// 附加属性：当 IsSelected 变为 true 时让宿主元素滚动到可见区域。
/// 用于键盘导航（列表嵌套在 ScrollViewer 内，容器可直接命中）。
/// </summary>
public static class BringIntoViewBehavior
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached(
            "IsSelected",
            typeof(bool),
            typeof(BringIntoViewBehavior),
            new PropertyMetadata(false, OnIsSelectedChanged));

    public static void SetIsSelected(DependencyObject element, bool value)
        => element.SetValue(IsSelectedProperty, value);

    public static bool GetIsSelected(DependencyObject element)
        => (bool)element.GetValue(IsSelectedProperty);

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && d is FrameworkElement element)
        {
            element.BringIntoView();
        }
    }
}
