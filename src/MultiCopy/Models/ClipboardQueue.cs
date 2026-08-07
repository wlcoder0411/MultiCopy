using System.Collections.ObjectModel;

namespace MultiCopy.Models;

/// <summary>
/// 剪贴板队列。支持内容分组（QQ 风格）：
/// - UngroupedItems：未分组普通项（默认状态全部落此，UI 扁平渲染）。
/// - Groups：用户创建的分组列表，每个分组含自己的 Items。
/// - PinnedItems：置顶集合（不归属任何分组，不参与自动出队）。
/// 出队（Peek/Dequeue）范围由 activeGroupId 参数决定：
/// 选中分组时仅该分组内 FIFO；未选中（null）时退化为全局 FIFO（跨所有分组取 CreatedAt 最早）。
/// 全部访问在 UI 线程，故直接使用 ObservableCollection 兼做逻辑与绑定。
/// </summary>
public sealed class ClipboardQueue
{
    /// <summary>未分组普通项（默认状态全部落此，UI 扁平渲染）。</summary>
    public ObservableCollection<ClipboardItem> UngroupedItems { get; } = new();

    /// <summary>用户创建的分组列表。</summary>
    public ObservableCollection<ClipboardGroup> Groups { get; } = new();

    /// <summary>置顶集合（不参与自动出队，双击粘贴后保留）。不归属任何分组。</summary>
    public ObservableCollection<ClipboardItem> PinnedItems { get; } = new();

    /// <summary>是否存在任何分组（决定 UI 渲染模式：扁平 vs 分组）。</summary>
    public bool IsGrouping => Groups.Count > 0;

    public bool HasNormal => UngroupedItems.Count > 0 || AnyGroupHasItems();

    public bool HasUngrouped => UngroupedItems.Count > 0;

    public bool IsEmpty => !HasNormal && PinnedItems.Count == 0;

    /// <summary>普通项数量（不含置顶分组内的项，用于容量上限检查）。</summary>
    public int NormalCount => UngroupedItems.Count + SumNonPinnedGroupItems();

    /// <summary>队列内容变化时触发（用于刷新空状态提示、IsGrouping 等）。</summary>
    public event EventHandler? Changed;

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private bool AnyGroupHasItems()
    {
        foreach (var g in Groups)
            if (g.Items.Count > 0) return true;
        return false;
    }

    /// <summary>非置顶分组内的项数总和（置顶分组内的项不占用容量上限）。</summary>
    private int SumNonPinnedGroupItems()
    {
        int sum = 0;
        foreach (var g in Groups)
            if (!g.IsPinned) sum += g.Items.Count;
        return sum;
    }

    /// <summary>查找分组。</summary>
    public ClipboardGroup? FindGroup(string? id) =>
        string.IsNullOrEmpty(id) ? null : Groups.FirstOrDefault(g => g.Id == id);

    // ---------- 入队 ----------

    /// <summary>入队。groupId 非空且存在则归入该分组，否则归入未分组。</summary>
    public void Enqueue(ClipboardItem item, string? groupId = null)
    {
        if (FindGroup(groupId) is { } g)
            g.Items.Add(item);
        else
            UngroupedItems.Add(item);
        OnChanged();
    }

    // ---------- 出队（范围 FIFO：活动分组非空时仅该分组，否则全局） ----------

    /// <summary>
    /// 查看下一条待粘贴项（不移除）。
    /// 范围由 activeGroupId 决定：
    /// - 非空且分组存在：仅在该分组内取队首（分组内 FIFO，按 Items 顺序）。
    /// - null 或分组不存在：退化为全局 FIFO（跨未分组+所有分组取 CreatedAt 最早）。
    /// 设计意图：选中分组=粘贴范围隔离；未选中=保住"连续复制→连续粘贴"核心场景。
    /// </summary>
    public ClipboardItem? Peek(string? activeGroupId = null)
    {
        // 选中了某分组：只看这一组。分组内按 Items 顺序（入队顺序）出队。
        // 置顶分组不参与自动出队（组内项享有置顶语义，Ctrl+V 不消费）。
        if (FindGroup(activeGroupId) is { } g)
        {
            if (g.IsPinned) return null;
            return g.Items.Count > 0 ? g.Items[0] : null;
        }

        // 未选中分组：全局 FIFO，跨所有集合取 CreatedAt 最早项。
        // 跳过置顶分组（置顶分组内的项不参与自动出队）。
        ClipboardItem? best = null;
        if (UngroupedItems.Count > 0) best = UngroupedItems[0];
        foreach (var grp in Groups)
        {
            if (grp.IsPinned) continue;
            if (grp.Items.Count == 0) continue;
            var head = grp.Items[0];
            if (best == null || head.CreatedAt < best.CreatedAt)
                best = head;
        }
        return best;
    }

    /// <summary>出队下一条（Ctrl+V 自动粘贴消费）。范围语义同 <see cref="Peek"/>。</summary>
    public ClipboardItem? Dequeue(string? activeGroupId = null)
    {
        var item = Peek(activeGroupId);
        if (item == null) return null;
        RemoveNormal(item);
        return item;
    }

    // ---------- 移除 ----------

    /// <summary>移除指定普通项（点选粘贴 / Dequeue 共用）。跨所有集合查找移除。</summary>
    public bool RemoveNormal(ClipboardItem item)
    {
        if (UngroupedItems.Remove(item)) { OnChanged(); return true; }
        foreach (var g in Groups)
        {
            if (g.Items.Remove(item)) { OnChanged(); return true; }
        }
        return false;
    }

    /// <summary>查询项当前所属分组 Id（null=未分组或不属于任何集合）。</summary>
    public string? GetGroupIdOf(ClipboardItem item)
    {
        if (UngroupedItems.Contains(item)) return null;
        foreach (var g in Groups)
            if (g.Items.Contains(item)) return g.Id;
        return null;
    }

    /// <summary>
    /// 移动普通项到目标分组。跨集合查找源位置移除，再加到目标集合末尾。
    /// targetGroupId=null 移到未分组；指向不存在的分组则视为未分组。
    /// </summary>
    public void MoveItem(ClipboardItem item, string? targetGroupId)
    {
        bool removed = UngroupedItems.Remove(item);
        if (!removed)
        {
            foreach (var g in Groups)
            {
                if (g.Items.Remove(item)) { removed = true; break; }
            }
        }
        if (!removed) return; // 项不在任何普通集合（可能已置顶或已删除）

        if (FindGroup(targetGroupId) is { } tg)
            tg.Items.Add(item);
        else
            UngroupedItems.Add(item);
        OnChanged();
    }

    // ---------- 置顶 / 取消置顶 ----------

    /// <summary>
    /// 从持久化存储加载置顶项（应用启动时调用）。
    /// 直接加到 PinnedItems 末尾，不触发 OnChanged（启动阶段 UI 尚未就绪）。
    /// </summary>
    public void LoadPinned(IEnumerable<ClipboardItem> pinnedItems)
    {
        foreach (var item in pinnedItems)
        {
            if (item.IsPinned && !PinnedItems.Contains(item))
                PinnedItems.Add(item);
        }
    }

    /// <summary>从持久化存储加载分组列表（应用启动时调用）。不触发 OnChanged（启动阶段 UI 尚未就绪）。</summary>
    public void LoadGroups(IEnumerable<ClipboardGroup> groups)
    {
        foreach (var g in groups)
        {
            if (!Groups.Contains(g))
                Groups.Add(g);
        }
    }

    /// <summary>置顶：从普通队列（未分组或某分组）移到置顶集合。</summary>
    public void Pin(ClipboardItem item)
    {
        if (item.IsPinned) return;
        if (RemoveNormalInternal(item))
        {
            item.IsPinned = true;
            PinnedItems.Add(item);
            OnChanged();
        }
    }

    /// <summary>取消置顶：回到活动分组（或未分组）末尾。</summary>
    public void Unpin(ClipboardItem item, string? activeGroupId = null)
    {
        if (!item.IsPinned) return;
        if (PinnedItems.Remove(item))
        {
            item.IsPinned = false;
            if (FindGroup(activeGroupId) is { } g)
                g.Items.Add(item);
            else
                UngroupedItems.Add(item);
            OnChanged();
        }
    }

    public void RemovePinned(ClipboardItem item)
    {
        if (PinnedItems.Remove(item)) OnChanged();
    }

    // ---------- 清空 ----------

    public void ClearNormal()
    {
        bool changed = UngroupedItems.Count > 0;
        UngroupedItems.Clear();
        foreach (var g in Groups)
        {
            if (g.IsPinned) continue; // 置顶分组不被清空
            if (g.Items.Count > 0) changed = true;
            g.Items.Clear();
        }
        if (changed) OnChanged();
    }

    public void ClearAll()
    {
        UngroupedItems.Clear();
        foreach (var g in Groups) g.Items.Clear();
        PinnedItems.Clear();
        OnChanged();
    }

    // ---------- 分组 CRUD ----------

    public ClipboardGroup AddGroup(string name)
    {
        var g = new ClipboardGroup(name);
        Groups.Add(g);
        OnChanged();
        return g;
    }

    public void RenameGroup(ClipboardGroup g, string newName)
    {
        if (!Groups.Contains(g)) return;
        g.Name = newName ?? g.Name;
    }

    /// <summary>删除分组。moveItemsToUngrouped=true 时组内项移至未分组，否则连同删除。</summary>
    public void RemoveGroup(ClipboardGroup g, bool moveItemsToUngrouped = true)
    {
        if (!Groups.Contains(g)) return;
        if (moveItemsToUngrouped)
        {
            foreach (var it in g.Items)
                UngroupedItems.Add(it);
        }
        g.Items.Clear();
        Groups.Remove(g);
        OnChanged();
    }

    public void ClearGroup(ClipboardGroup g)
    {
        if (!Groups.Contains(g)) return;
        g.Items.Clear();
        OnChanged();
    }

    // ---------- 分组置顶 / 取消置顶 ----------

    /// <summary>
    /// 置顶分组：标记 IsPinned=true，记录 PinnedAt，移到 Groups 中所有已置顶分组之后、普通分组之前。
    /// 组内项享有置顶语义（双击粘贴并保留、不参与自动出队、跨会话保留），但仍归属本分组。
    /// </summary>
    public void PinGroup(ClipboardGroup g)
    {
        if (!Groups.Contains(g) || g.IsPinned) return;
        g.IsPinned = true;
        g.PinnedAt = DateTime.Now;
        // 移到所有已置顶分组之后、普通分组之前（即置顶分组的末尾位置）
        // 注意：此时 g.IsPinned 已为 true，CountPinnedGroups 包含 g 自身
        int targetIdx = CountPinnedGroups() - 1;
        int curIdx = Groups.IndexOf(g);
        if (curIdx != targetIdx)
            Groups.Move(curIdx, targetIdx);
        OnChanged();
    }

    /// <summary>取消置顶分组：标记 IsPinned=false，移到所有置顶分组之后（普通分组的开头）。</summary>
    public void UnpinGroup(ClipboardGroup g)
    {
        if (!Groups.Contains(g) || !g.IsPinned) return;
        g.IsPinned = false;
        g.PinnedAt = null;
        // 移到所有置顶分组之后（此时 g.IsPinned 已为 false，CountPinnedGroups 不含 g）
        int targetIdx = CountPinnedGroups();
        int curIdx = Groups.IndexOf(g);
        if (curIdx != targetIdx)
            Groups.Move(curIdx, targetIdx);
        OnChanged();
    }

    /// <summary>统计当前置顶分组数量。</summary>
    private int CountPinnedGroups()
    {
        int n = 0;
        foreach (var g in Groups)
            if (g.IsPinned) n++;
        return n;
    }

    // ---------- 内部：跨集合移除不触发 OnChanged（供 Pin 复用） ----------

    private bool RemoveNormalInternal(ClipboardItem item)
    {
        if (UngroupedItems.Remove(item)) return true;
        foreach (var g in Groups)
            if (g.Items.Remove(item)) return true;
        return false;
    }
}
