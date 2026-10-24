using MultiCopy.Models;

namespace MultiCopy.ViewModels;

/// <summary>列表区域类型。</summary>
public enum ClipboardSectionKind
{
    /// <summary>全局置顶区。</summary>
    Pinned,
    /// <summary>未分组区（无分组时即扁平队列）。</summary>
    Ungrouped,
    /// <summary>用户分组。</summary>
    Group,
}

/// <summary>单一行列表中的条目基类：区域标题行或内容行。</summary>
public abstract class ClipboardListEntry
{
}

/// <summary>区域标题行（置顶 / 未分组 / 组头），不参与粘贴导航。</summary>
public sealed class ClipboardHeaderRow : ClipboardListEntry
{
    public ClipboardHeaderRow(ClipboardSectionKind kind, ClipboardGroup? group, string title)
    {
        Kind = kind;
        Group = group;
        Title = title;
    }

    public ClipboardSectionKind Kind { get; }

    /// <summary>组头对应的分组；置顶/未分组标题为 null。</summary>
    public ClipboardGroup? Group { get; }

    /// <summary>置顶/未分组标题文字；组头实际显示 Group.Name。</summary>
    public string Title { get; }

    public bool IsGroup => Kind == ClipboardSectionKind.Group;
}

/// <summary>内容行，承载一个剪贴板条目及其所属区域信息。</summary>
public sealed class ClipboardItemRow : ClipboardListEntry
{
    public ClipboardItemRow(ClipboardItem item, ClipboardSectionKind kind, ClipboardGroup? group)
    {
        Item = item;
        Kind = kind;
        Group = group;
    }

    public ClipboardItem Item { get; }

    public ClipboardSectionKind Kind { get; }

    public ClipboardGroup? Group { get; }

    /// <summary>是否属于置顶分组（组内项使用置顶视觉与双击保留语义）。</summary>
    public bool IsInPinnedGroup => Kind == ClipboardSectionKind.Group && Group?.IsPinned == true;

    /// <summary>是否使用置顶视觉（全局置顶或置顶分组内）。</summary>
    public bool IsPinnedContext => Item.IsPinned || IsInPinnedGroup;

    /// <summary>可置顶（普通项：未置顶且不在置顶分组）。</summary>
    public bool CanPin => !Item.IsPinned && !IsInPinnedGroup;

    /// <summary>可取消置顶（仅全局置顶项）。</summary>
    public bool CanUnpin => Item.IsPinned;

    /// <summary>可按行拆分（仅普通文本项）。</summary>
    public bool CanSplit => CanPin && Item is TextClipboardItem;

    /// <summary>可使用「移动到分组」等普通项操作。</summary>
    public bool CanManage => CanPin;
}
