using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiCopy.Models;

/// <summary>
/// 一个内容分组（QQ 风格）。承载 Normal 项；置顶分组（IsPinned=true）内的项享有置顶语义
/// （双击粘贴并保留、不参与自动出队、跨会话保留），但仍归属本分组（不移到全局 PinnedItems）。
/// 默认状态不创建任何分组，所有项在 ClipboardQueue.UngroupedItems 中扁平展示。
/// </summary>
public sealed partial class ClipboardGroup : ObservableObject
{
    /// <summary>唯一标识。</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name;

    /// <summary>是否展开（显示组内项）。</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>是否为当前活动分组（新复制内容归入此组）。由 ViewModel 切换时刷新。</summary>
    [ObservableProperty]
    private bool _isActiveGroup;

    /// <summary>是否为置顶分组（整体置顶，组内项双击粘贴并保留，不参与自动出队）。</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>置顶时间（用于置顶分组间排序，先置顶在上）。非置顶时为 null。</summary>
    public DateTime? PinnedAt { get; set; }

    /// <summary>组内普通项（FIFO，按入队顺序）。</summary>
    public ObservableCollection<ClipboardItem> Items { get; } = new();

    /// <summary>创建时间，用于分组排序（先建在上）。</summary>
    public DateTime CreatedAt { get; } = DateTime.Now;

    public ClipboardGroup(string name)
    {
        _name = name ?? "未命名分组";
    }
}
