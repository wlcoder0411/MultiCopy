using System.Windows;
using System.Windows.Controls;
using MultiCopy.Models;
using MultiCopy.ViewModels;

namespace MultiCopy.Infrastructure;

/// <summary>
/// 单一行列表的模板选择器：标题行 → HeaderTemplate；
/// 内容行按条目类型选择文本或图片模板。
/// </summary>
public sealed class ClipboardEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }

    public DataTemplate? TextTemplate { get; set; }

    public DataTemplate? ImageTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            ClipboardHeaderRow => HeaderTemplate,
            ClipboardItemRow { Item: ImageClipboardItem } => ImageTemplate,
            ClipboardItemRow => TextTemplate,
            _ => base.SelectTemplate(item, container)
        };
    }
}
