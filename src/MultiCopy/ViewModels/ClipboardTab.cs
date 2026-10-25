using CommunityToolkit.Mvvm.ComponentModel;
using MultiCopy.Models;

namespace MultiCopy.ViewModels;

/// <summary>顶部标签栏的标签类型。</summary>
public enum ClipboardTabKind
{
    /// <summary>全部（置顶区 + 未分组 + 所有分组内容；对应"无活动分组"全局模式）。</summary>
    All,
    /// <summary>置顶项（纯查看筛选）。</summary>
    Pinned,
    /// <summary>用户分组（点击设为活动分组；置顶分组仅查看）。</summary>
    Group,
    /// <summary>智能筛选：非链接文本（纯查看）。</summary>
    TypeText,
    /// <summary>智能筛选：图片（纯查看）。</summary>
    TypeImage,
    /// <summary>智能筛选：链接文本（纯查看）。</summary>
    TypeLink,
}

/// <summary>
/// 顶部标签栏的一个标签。固定标签（全部/置顶/未分组/类型筛选）用 Kind+Title；
/// 分组标签持有 <see cref="Group"/> 引用，标题随 Group.Name 实时刷新。
/// IsSelected 驱动选中视觉；活动分组指示直接绑定 Group.IsActiveGroup（由 ViewModel 刷新）。
/// </summary>
public sealed partial class ClipboardTab : ObservableObject
{
    public ClipboardTabKind Kind { get; }

    /// <summary>分组标签对应的分组；其他类型为 null。</summary>
    public ClipboardGroup? Group { get; }

    /// <summary>固定标签的标题（分组标签的标题绑定 Group.Name，不用此属性）。</summary>
    public string Title { get; }

    /// <summary>是否当前选中（模板触发器上色用）。由 ViewModel 在 RebuildTabs 中统一维护。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>是否为置顶分组标签（显示 📌 标记，仅查看、不可设为活动分组）。</summary>
    public bool IsPinnedGroup => Kind == ClipboardTabKind.Group && Group?.IsPinned == true;

    /// <summary>是否显示 📌 标记（置顶标签或置顶分组标签）。</summary>
    public bool ShowPin => Kind == ClipboardTabKind.Pinned || IsPinnedGroup;

    /// <summary>是否为分组标签（决定右键菜单与标题绑定路径）。</summary>
    public bool IsGroupTab => Kind == ClipboardTabKind.Group;

    /// <summary>标签的稳定标识（RebuildTabs 恢复选中用）：分组用 Id，固定标签用 Kind。</summary>
    public string TabId => Kind == ClipboardTabKind.Group ? $"group:{Group?.Id}" : Kind.ToString();

    public ClipboardTab(ClipboardTabKind kind, string title)
    {
        Kind = kind;
        Title = title ?? string.Empty;
    }

    public ClipboardTab(ClipboardGroup group)
    {
        Kind = ClipboardTabKind.Group;
        Group = group ?? throw new ArgumentNullException(nameof(group));
        Title = group.Name;
    }
}
