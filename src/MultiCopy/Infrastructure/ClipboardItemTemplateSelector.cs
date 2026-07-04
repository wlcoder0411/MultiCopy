using System.Windows;
using System.Windows.Controls;
using MultiCopy.Models;

namespace MultiCopy.Infrastructure;

/// <summary>
/// 按 ClipboardItem 子类型选择 DataTemplate。
/// 文本项用 TextTemplate；图片项用 ImageTemplate。
/// 不区分普通/置顶上下文——由调用方在 XAML 中向不同列表注入不同的 selector 实例
/// （PinnedItems 注入置顶模板，UngroupedItems/Groups.Items 注入普通模板）。
/// </summary>
public sealed class ClipboardItemTemplateSelector : DataTemplateSelector
{
    /// <summary>文本项模板（普通或置顶，由 XAML 注入）。</summary>
    public DataTemplate? TextTemplate { get; set; }

    /// <summary>图片项模板（普通或置顶，由 XAML 注入）。</summary>
    public DataTemplate? ImageTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            ImageClipboardItem => ImageTemplate,
            TextClipboardItem => TextTemplate,
            _ => base.SelectTemplate(item, container)
        };
    }
}
