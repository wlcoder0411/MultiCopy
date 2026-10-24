using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiCopy.Models;
using MultiCopy.Services;
using MultiCopy.State;
using MultiCopy.Views;

namespace MultiCopy.ViewModels;

/// <summary>
/// 主窗口视图模型。维护单一的扁平显示行集合（<see cref="Rows"/>），
/// 由视图用一个 ListBox 统一渲染；选择、粘贴、分组操作都作用在行上。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ClipboardQueue _queue;
    private readonly PasteExecutor _pasteExecutor;
    private readonly AppState _state = AppState.Instance;

    /// <summary>未分组普通项（默认状态全部落此）。</summary>
    public ObservableCollection<ClipboardItem> UngroupedItems => _queue.UngroupedItems;

    /// <summary>用户创建的分组列表。</summary>
    public ObservableCollection<ClipboardGroup> Groups => _queue.Groups;

    public ObservableCollection<ClipboardItem> PinnedItems => _queue.PinnedItems;

    /// <summary>扁平显示行：区域标题行 + 内容行，直接绑定到 ListBox。</summary>
    public ObservableCollection<ClipboardListEntry> Rows { get; } = new();

    [ObservableProperty]
    private bool _modeOn;

    /// <summary>剪贴板监控开关（双向同步 AppState.IsMonitoring）。</summary>
    [ObservableProperty]
    private bool _isMonitoring;

    /// <summary>是否为亮色主题（用于主题切换菜单的动态文案）。</summary>
    public bool IsLightTheme => ThemeService.CurrentTheme == AppTheme.Light;

    /// <summary>全部为空（用于空状态提示）。</summary>
    [ObservableProperty]
    private bool _allEmpty = true;

    /// <summary>是否显示空状态覆盖层（空队列或搜索无结果；仅剩空分组标题时不覆盖）。</summary>
    [ObservableProperty]
    private bool _showEmptyState;

    /// <summary>搜索无结果（关键字非空且没有任何内容行）。</summary>
    [ObservableProperty]
    private bool _searchNoResult;

    /// <summary>搜索关键字（双向绑定搜索框，UpdateSourceTrigger=PropertyChanged）。</summary>
    [ObservableProperty]
    private string? _searchText;

    /// <summary>当前选中的列表行（可能为标题行；粘贴操作只认内容行）。</summary>
    [ObservableProperty]
    private ClipboardListEntry? _selectedEntry;

    /// <summary>相对时间刷新定时器（30 秒）。</summary>
    private readonly DispatcherTimer _relativeTimeTimer;

    /// <summary>容量指示文本（如 "18/30"）。</summary>
    [ObservableProperty]
    private string _capacityText = "0/30";

    /// <summary>容量是否已达上限（用于警示色）。</summary>
    [ObservableProperty]
    private bool _isCapacityWarning;

    /// <summary>是否存在可撤销的上一步操作（控制撤销按钮可用性）。</summary>
    [ObservableProperty]
    private bool _canUndo;

    /// <summary>是否显示首次使用引导。</summary>
    [ObservableProperty]
    private bool _showOnboarding;

    /// <summary>底部动态操作提示。</summary>
    [ObservableProperty]
    private string _operationHint = DefaultOperationHint;

    private const string DefaultOperationHint =
        "模式开：Ctrl+V 按序出队 · 窗口内：↑↓ 选择、Enter 粘贴、Ctrl+Z 撤销 · 点击此处查看全部快捷键";

    private const string EmptyOperationHint =
        "在任意应用 Ctrl+C 收集内容 · Alt+Z 随时调出 · 点击此处查看全部快捷键";

    /// <summary>当前是否在搜索状态（SearchText 非空白）。</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>当前活动分组 Id（null=未分组/默认）。双向同步 AppState。</summary>
    public string? ActiveGroupId
    {
        get => _state.ActiveGroupId;
        set => _state.ActiveGroupId = value;
    }

    public MainViewModel(ClipboardQueue queue, PasteExecutor pasteExecutor)
    {
        _queue = queue;
        _pasteExecutor = pasteExecutor;

        _modeOn = _state.ModeOn;
        _state.ModeChanged += OnAppStateModeChanged;
        _isMonitoring = _state.IsMonitoring;
        _state.MonitoringChanged += OnAppStateMonitoringChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        _queue.Changed += OnQueueChanged;

        // 相对时间每 30 秒刷新一次，保证「刚刚 / N 分钟前」近似实时
        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _relativeTimeTimer.Tick += (_, _) => UpdateRelativeTimes();
        _relativeTimeTimer.Start();

        RefreshAll(advanceIfMissing: false);
    }

    private void OnAppStateModeChanged(object? sender, bool modeOn) => ModeOn = modeOn;

    private void OnAppStateMonitoringChanged(object? sender, bool value) => IsMonitoring = value;

    /// <summary>主题变化时刷新溢出菜单中主题项的可视性。</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(IsLightTheme));

    [RelayCommand]
    private void ToggleTheme()
    {
        ThemeService.Toggle();
        // 主题属于用户偏好，切换即持久化，避免每次启动回到暗色
        SettingsStorageService.Update(s => s.Theme = ThemeService.CurrentTheme);
    }

    // ObservableProperty 生成的 ModeOn setter 变化时同步回 AppState
    partial void OnModeOnChanged(bool value) => _state.ModeOn = value;

    // ObservableProperty 生成的 IsMonitoring setter 变化时同步回 AppState
    partial void OnIsMonitoringChanged(bool value) => _state.IsMonitoring = value;

    private void OnQueueChanged(object? sender, EventArgs e)
        => RefreshAll(advanceIfMissing: true);

    partial void OnSearchTextChanged(string? value)
        => RefreshAll(advanceIfMissing: false);

    partial void OnSelectedEntryChanged(ClipboardListEntry? value)
        => OnPropertyChanged(nameof(SelectedItem));

    /// <summary>当前选中的条目（来自内容行；标题行选中时为 null）。</summary>
    public ClipboardItem? SelectedItem => (SelectedEntry as ClipboardItemRow)?.Item;

    /// <summary>
    /// 重建单一行列表：区域标题行 + 内容行。队列变化、搜索、分组折叠都走这里。
    /// advanceIfMissing=true 时，若原选中项已被移除，按原位置自动续选下一项。
    /// </summary>
    public void RebuildRows(bool advanceIfMissing)
    {
        var previousItem = SelectedItem;
        var rowsBefore = Rows.OfType<ClipboardItemRow>().ToList();
        int previousItemIndex = previousItem == null
            ? -1
            : rowsBefore.FindIndex(r => ReferenceEquals(r.Item, previousItem));

        Rows.Clear();
        string? keyword = IsSearching ? SearchText!.Trim() : null;

        // 1. 置顶区：有可见项才显示标题
        var pinned = FilterItems(PinnedItems, keyword);
        if (pinned.Count > 0)
        {
            Rows.Add(new ClipboardHeaderRow(ClipboardSectionKind.Pinned, null,
                "置顶（双击粘贴 · 不会被 Ctrl+V 消耗）"));
            foreach (var item in pinned)
                Rows.Add(new ClipboardItemRow(item, ClipboardSectionKind.Pinned, null));
        }

        // 2. 未分组区：无分组时作为扁平队列始终显示；有分组时仅在非空时显示
        var ungrouped = FilterItems(UngroupedItems, keyword);
        if (Groups.Count == 0)
        {
            Rows.Add(new ClipboardHeaderRow(ClipboardSectionKind.Ungrouped, null,
                "队列（开模式后按 Ctrl+V 按序粘贴 · 单击直接粘贴）"));
            foreach (var item in ungrouped)
                Rows.Add(new ClipboardItemRow(item, ClipboardSectionKind.Ungrouped, null));
        }
        else if (ungrouped.Count > 0)
        {
            Rows.Add(new ClipboardHeaderRow(ClipboardSectionKind.Ungrouped, null,
                "未分组（单击粘贴 · 开模式 Ctrl+V 按序出队）"));
            foreach (var item in ungrouped)
                Rows.Add(new ClipboardItemRow(item, ClipboardSectionKind.Ungrouped, null));
        }

        // 3. 各分组：标题始终显示（空分组也可管理）；搜索时只显示有匹配项的分组
        foreach (var g in Groups)
        {
            var groupItems = FilterItems(g.Items, keyword);
            if (keyword != null && groupItems.Count == 0) continue;

            Rows.Add(new ClipboardHeaderRow(ClipboardSectionKind.Group, g, g.Name));
            if (!g.IsExpanded) continue;
            foreach (var item in groupItems)
                Rows.Add(new ClipboardItemRow(item, ClipboardSectionKind.Group, g));
        }

        // 4. 选择恢复
        if (previousItem == null) return;

        var itemRows = Rows.OfType<ClipboardItemRow>().ToList();
        var same = itemRows.FirstOrDefault(r => ReferenceEquals(r.Item, previousItem));
        if (same != null)
        {
            SelectedEntry = same;
            return;
        }
        if (!advanceIfMissing || itemRows.Count == 0)
        {
            SelectedEntry = null;
            return;
        }
        SelectedEntry = itemRows[Math.Clamp(previousItemIndex, 0, itemRows.Count - 1)];
    }

    private void RefreshAll(bool advanceIfMissing)
    {
        RebuildRows(advanceIfMissing);

        int normalCount = _queue.NormalCount;
        CapacityText = $"{normalCount}/{AppState.MaxQueueItems}";
        IsCapacityWarning = normalCount >= AppState.MaxQueueItems;
        CanUndo = _queue.CanUndo;

        AllEmpty = !Rows.OfType<ClipboardItemRow>().Any();
        ShowEmptyState = AllEmpty && !Rows.OfType<ClipboardHeaderRow>().Any(h => h.IsGroup);
        SearchNoResult = IsSearching && AllEmpty;
        OnPropertyChanged(nameof(IsSearching));

        OperationHint = _queue.IsEmpty
            ? EmptyOperationHint
            : (CanUndo ? "可撤销上一步 · Ctrl+Z 撤销 · 点击此处查看全部快捷键" : DefaultOperationHint);

        RefreshDisplayIndexes();
        UpdateRelativeTimes();
    }

    private static List<ClipboardItem> FilterItems(IEnumerable<ClipboardItem> source, string? keyword)
        => keyword == null
            ? source.ToList()
            : source.Where(item => ItemMatches(item, keyword)).ToList();

    /// <summary>
    /// 条目是否包含关键字（部分匹配，不区分大小写）。
    /// 文本项按 Text 过滤；图片项按元数据（SourceApp + Preview）过滤，不做 OCR。
    /// </summary>
    private static bool ItemMatches(ClipboardItem item, string keyword) => item switch
    {
        TextClipboardItem text => text.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase),
        ImageClipboardItem img => (img.SourceApp + " " + img.Preview).Contains(keyword, StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    // ---------- 显示辅助 ----------

    private IEnumerable<ClipboardItem> AllItems()
    {
        foreach (var item in PinnedItems) yield return item;
        foreach (var item in UngroupedItems) yield return item;
        foreach (var g in Groups)
            foreach (var item in g.Items)
                yield return item;
    }

    /// <summary>
    /// 按展示顺序为条目分配 1..N 的序号（置顶 → 未分组 → 各分组）。
    /// 编号不随搜索过滤或分组折叠变化。
    /// </summary>
    private void RefreshDisplayIndexes()
    {
        int index = 1;
        foreach (var item in _queue.EnumerateIndexedItems())
        {
            item.DisplayIndex = index++;
        }
    }

    /// <summary>刷新所有条目的相对时间文本。</summary>
    private void UpdateRelativeTimes()
    {
        var now = DateTime.Now;
        foreach (var item in AllItems())
            item.RelativeTime = FormatRelativeTime(item.CreatedAt, now);
    }

    /// <summary>把复制时间格式化为「刚刚 / N 分钟前 / N 小时前 / 昨天 / 日期」。</summary>
    private static string FormatRelativeTime(DateTime createdAt, DateTime now)
    {
        TimeSpan span = now - createdAt;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        if (createdAt.Date == now.Date.AddDays(-1)) return "昨天";
        if (createdAt.Year == now.Year) return $"{createdAt.Month}月{createdAt.Day}日";
        return $"{createdAt.Year}/{createdAt.Month}/{createdAt.Day}";
    }

    /// <summary>按方向移动选择（仅内容行，自动跳过标题行）。</summary>
    public void MoveSelection(int delta)
    {
        var itemRows = Rows.OfType<ClipboardItemRow>().ToList();
        if (itemRows.Count == 0)
        {
            SelectedEntry = null;
            return;
        }

        int current = SelectedEntry is ClipboardItemRow row ? itemRows.IndexOf(row) : -1;
        int next = current < 0
            ? (delta > 0 ? 0 : itemRows.Count - 1)
            : Math.Clamp(current + delta, 0, itemRows.Count - 1);
        SelectedEntry = itemRows[next];
    }

    /// <summary>选中指定条目对应的行（撤销恢复后使用）。</summary>
    private void SelectItem(ClipboardItem item)
    {
        var row = Rows.OfType<ClipboardItemRow>().FirstOrDefault(r => ReferenceEquals(r.Item, item));
        if (row != null) SelectedEntry = row;
    }

    /// <summary>该条目粘贴后是否保留：全局置顶项与置顶分组内的项均保留。</summary>
    public bool IsKeepAfterPaste(ClipboardItem item)
        => item.IsPinned || _queue.Groups.Any(g => g.IsPinned && g.Items.Contains(item));

    /// <summary>刷新各分组的 IsActiveGroup 标记（UI 高亮用）。</summary>
    private void RefreshActiveGroupFlags()
    {
        var activeId = _state.ActiveGroupId;
        foreach (var g in _queue.Groups)
            g.IsActiveGroup = g.Id == activeId;
    }

    // ---------- 模式 / 置顶 / 删除 / 清空（原有） ----------

    [RelayCommand]
    private void ToggleMode() => ModeOn = !ModeOn;

    /// <summary>重置前缀序号到起始值。</summary>
    [RelayCommand]
    private void ResetSequence() => _state.ResetPrefixSequence();

    /// <summary>清空搜索框（清除按钮和 Esc 键调用）。</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void Pin(ClipboardItem? item)
    {
        if (item != null) _queue.Pin(item);
    }

    [RelayCommand]
    private void Unpin(ClipboardItem? item)
    {
        if (item != null) _queue.Unpin(item, _state.ActiveGroupId);
    }

    [RelayCommand]
    private void DeleteItem(ClipboardItem? item)
    {
        if (item == null) return;
        if (item.IsPinned) _queue.RemovePinned(item);
        else _queue.RemoveNormal(item);
    }

    [RelayCommand]
    private void ClearNormal() => _queue.ClearNormal();

    [RelayCommand]
    private void ClearAll() => _queue.ClearAll();

    /// <summary>撤销最近一次粘贴出队或删除（最多回退 8 步），并选中被恢复的条目。</summary>
    [RelayCommand]
    private void UndoLastRemoval()
    {
        var restored = _queue.UndoLastRemoval();
        if (restored != null)
            SelectItem(restored);
    }

    /// <summary>把文本条目按行拆分为多条（去掉空行与首尾空白，最多 100 行）。</summary>
    [RelayCommand(CanExecute = nameof(CanSplitLines))]
    private void SplitLines(ClipboardItem? item)
    {
        if (item is not TextClipboardItem textItem) return;

        var lines = textItem.Text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count <= 1) return;
        if (lines.Count > 100)
        {
            MessageBox.Show(Application.Current?.MainWindow,
                "单条内容最多拆分为 100 行，请先手动精简。",
                "按行拆分", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _queue.SplitIntoLines(textItem, lines);
    }

    private static bool CanSplitLines(ClipboardItem? item) => item is TextClipboardItem;

    /// <summary>关闭首次使用引导，并持久化「已看过」标记。</summary>
    [RelayCommand]
    private void DismissOnboarding()
    {
        ShowOnboarding = false;
        SettingsStorageService.Update(s => s.HasSeenOnboarding = true);
    }

    // ---------- 分组命令 ----------

    [RelayCommand]
    private void CreateGroup()
    {
        var owner = Application.Current?.MainWindow;
        var name = GroupInputDialog.PromptForInput(owner, "新建分组", "输入分组名称：", $"分组{_queue.Groups.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var g = _queue.AddGroup(name);
        // 新建分组自动设为活动分组（符合"新建后复制内容归入此组"场景）
        SetActiveGroup(g);
    }

    [RelayCommand]
    private void RenameGroup(ClipboardGroup? g)
    {
        if (g == null) return;
        var owner = Application.Current?.MainWindow;
        var name = GroupInputDialog.PromptForInput(owner, "重命名分组", "输入新的分组名称：", g.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _queue.RenameGroup(g, name);
    }

    [RelayCommand]
    private void DeleteGroup(ClipboardGroup? g)
    {
        if (g == null) return;
        var owner = Application.Current?.MainWindow;
        var msg = $"删除分组 \"{g.Name}\"？\n组内 {g.Items.Count} 项将移至未分组，不会被删除。";
        if (MessageBox.Show(owner, msg, "确认删除分组", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        bool wasActive = g.Id == _state.ActiveGroupId;
        _queue.RemoveGroup(g, moveItemsToUngrouped: true);
        if (wasActive)
        {
            _state.ActiveGroupId = null;
            RefreshActiveGroupFlags();
        }
    }

    [RelayCommand]
    private void ClearGroup(ClipboardGroup? g)
    {
        if (g == null) return;
        _queue.ClearGroup(g);
    }

    [RelayCommand]
    private void ToggleGroupExpand(ClipboardGroup? g)
    {
        if (g != null) g.IsExpanded = !g.IsExpanded;
        // 折叠/展开会改变可见行集合与选择，整体重建
        RefreshAll(advanceIfMissing: false);
    }

    [RelayCommand]
    private void SetActiveGroup(ClipboardGroup? g)
    {
        // 置顶分组不允许成为活动分组：组内项享有置顶语义、不参与自动出队，
        // 一旦设为活动，新复制内容会持续落入该组，而 NormalCount 排除置顶分组，
        // 既绕过 MaxQueueItems 上限无限增长，又让新内容 Ctrl+V 粘不出来（“消失”）。
        if (g is { IsPinned: true }) return;

        // 再次点击当前活动分组 = 取消选中，回到“未分组 / 全局 FIFO”模式。
        // 此前该命令恒把 ActiveGroupId 设为点击分组，用户一旦选中分组就没有退出通道
        // （新复制内容持续入组、Ctrl+V 只消耗该组），只剩删除/置顶分组两条路，属可用性死角。
        var targetId = g?.Id;
        _state.ActiveGroupId = (targetId != null && _state.ActiveGroupId == targetId) ? null : targetId;
        RefreshActiveGroupFlags();
    }

    [RelayCommand]
    private void PinGroup(ClipboardGroup? g)
    {
        if (g == null) return;
        // 置顶分组不参与自动出队（Peek 跳过置顶分组）。若它当前是活动分组，
        // 必须先解除活动状态：否则用户新复制的内容会进入置顶分组但 Ctrl+V 粘不出来（"消失"），
        // 且置顶分组不占容量上限，会绕过 MaxQueueItems 检查无限增长。
        if (_state.ActiveGroupId == g.Id)
        {
            _state.ActiveGroupId = null;
            RefreshActiveGroupFlags();
        }
        _queue.PinGroup(g);
    }

    [RelayCommand]
    private void UnpinGroup(ClipboardGroup? g)
    {
        if (g != null) _queue.UnpinGroup(g);
    }

    // ---------- 点选粘贴（视图调用） ----------

    /// <summary>普通项单击：粘贴并移除。</summary>
    public void PasteNormalItem(ClipboardItem item) => _pasteExecutor.Execute(item, false, _queue);

    /// <summary>普通项右键"移动到分组"：移到目标分组（视图调用，因需动态构建子菜单）。</summary>
    public void MoveItem(ClipboardItem item, string? targetGroupId) => _queue.MoveItem(item, targetGroupId);

    /// <summary>查询项当前所属分组 Id（null=未分组）。供视图构建移动子菜单时标记当前位置。</summary>
    public string? GetGroupIdOf(ClipboardItem item) => _queue.GetGroupIdOf(item);

    /// <summary>置顶项双击：粘贴并保留。</summary>
    public void PastePinnedItem(ClipboardItem item) => _pasteExecutor.Execute(item, true, _queue);
}
