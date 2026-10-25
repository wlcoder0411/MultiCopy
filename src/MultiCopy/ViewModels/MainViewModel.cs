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

    /// <summary>顶部标签栏（全部/置顶/未分组/各分组/类型筛选）。由 RebuildTabs 随队列变化重建。</summary>
    public ObservableCollection<ClipboardTab> Tabs { get; } = new();

    /// <summary>当前选中的标签。仅由 SelectTab（用户点击）与 RebuildTabs（重建恢复）设置。</summary>
    [ObservableProperty]
    private ClipboardTab? _selectedTab;

    /// <summary>空状态主文案（随当前标签类型变化）。</summary>
    [ObservableProperty]
    private string _emptyStateText = "暂无复制内容";

    /// <summary>空状态副文案（操作指引，随标签类型变化）。</summary>
    [ObservableProperty]
    private string _emptyStateHint = "在任意应用按 Ctrl+C 即可收集到这里";

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
    /// 重建单一行列表：按当前标签的范围过滤内容行，仅「全部」保留置顶小节标题行。
    /// 队列变化、搜索、标签切换都走这里。
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

        var kind = SelectedTab?.Kind ?? ClipboardTabKind.All;
        // 分组标签指向已删除的分组（理论上是 RebuildTabs 之前的瞬间状态）：退回全部
        var tabGroup = SelectedTab?.Group;
        if (kind == ClipboardTabKind.Group && (tabGroup == null || _queue.FindGroup(tabGroup.Id) == null))
            kind = ClipboardTabKind.All;

        switch (kind)
        {
            case ClipboardTabKind.All:
            {
                // 置顶区：有可见项才显示标题；其余内容按展示顺序平铺（不分区，避免页面过长）
                var pinned = FilterItems(PinnedItems, keyword);
                if (pinned.Count > 0)
                {
                    Rows.Add(new ClipboardHeaderRow(ClipboardSectionKind.Pinned, null,
                        "置顶（双击粘贴 · 不会被 Ctrl+V 消耗）"));
                    foreach (var item in pinned)
                        Rows.Add(new ClipboardItemRow(item, ClipboardSectionKind.Pinned, null));
                }
                AddRows(EnumerateNormalWithGroup(), keyword);
                break;
            }
            case ClipboardTabKind.Pinned:
                AddRows(PinnedItems.Select(i => (i, (ClipboardGroup?)null)), keyword);
                break;

            case ClipboardTabKind.Group:
                AddRows(tabGroup!.Items.Select(i => (i, (ClipboardGroup?)tabGroup)), keyword);
                break;

            default:
                // 类型筛选（文本/图像/链接）：跨置顶 + 未分组 + 所有分组
                AddRows(EnumerateAllWithGroup().Where(p => MatchesTabKind(kind, p.Item)), keyword);
                break;
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

    /// <summary>按展示顺序枚举普通项及其所属分组（未分组 → 各分组）。</summary>
    private IEnumerable<(ClipboardItem Item, ClipboardGroup? Group)> EnumerateNormalWithGroup()
    {
        foreach (var item in UngroupedItems) yield return (item, null);
        foreach (var g in Groups)
            foreach (var item in g.Items)
                yield return (item, g);
    }

    /// <summary>按展示顺序枚举全部项（含置顶）及其所属分组。</summary>
    private IEnumerable<(ClipboardItem Item, ClipboardGroup? Group)> EnumerateAllWithGroup()
    {
        foreach (var item in PinnedItems) yield return (item, null);
        foreach (var pair in EnumerateNormalWithGroup()) yield return pair;
    }

    /// <summary>类型筛选标签的匹配规则（文本与链接互斥：URL 文本只归「链接」）。</summary>
    private static bool MatchesTabKind(ClipboardTabKind kind, ClipboardItem item) => kind switch
    {
        ClipboardTabKind.TypeText => item is TextClipboardItem t && !t.IsLink,
        ClipboardTabKind.TypeImage => item is ImageClipboardItem,
        ClipboardTabKind.TypeLink => item is TextClipboardItem { IsLink: true },
        _ => true
    };

    /// <summary>把 (条目, 分组) 序列按关键字过滤后加入行列表；行 Kind 按归属推导，保证置顶/分组视觉正确。</summary>
    private void AddRows(IEnumerable<(ClipboardItem Item, ClipboardGroup? Group)> source, string? keyword)
    {
        foreach (var (item, group) in source)
        {
            if (keyword != null && !ItemMatches(item, keyword)) continue;
            Rows.Add(new ClipboardItemRow(item,
                group != null ? ClipboardSectionKind.Group
                    : item.IsPinned ? ClipboardSectionKind.Pinned
                    : ClipboardSectionKind.Ungrouped,
                group));
        }
    }

    private void RefreshAll(bool advanceIfMissing)
    {
        // 先重建标签栏（选中项按 TabId 恢复），再按选中标签的范围重建行
        RebuildTabs();
        RebuildRows(advanceIfMissing);

        int normalCount = _queue.NormalCount;
        CapacityText = $"{normalCount}/{AppState.MaxQueueItems}";
        IsCapacityWarning = normalCount >= AppState.MaxQueueItems;
        CanUndo = _queue.CanUndo;

        AllEmpty = !Rows.OfType<ClipboardItemRow>().Any();
        ShowEmptyState = AllEmpty && !Rows.OfType<ClipboardHeaderRow>().Any();
        SearchNoResult = IsSearching && AllEmpty;
        OnPropertyChanged(nameof(IsSearching));

        var (emptyText, emptyHint) = BuildEmptyStateCopy();
        EmptyStateText = emptyText;
        EmptyStateHint = emptyHint;

        OperationHint = _queue.IsEmpty
            ? EmptyOperationHint
            : (CanUndo ? "可撤销上一步 · Ctrl+Z 撤销 · 点击此处查看全部快捷键" : DefaultOperationHint);

        RefreshDisplayIndexes();
        UpdateRelativeTimes();
    }

    /// <summary>
    /// 重建标签栏：全部 → 置顶 → 各分组（Groups 顺序，置顶分组在前）→ 文本/图像/链接智能筛选。
    /// 未分组内容统一由「全部」承载（全局模式下新复制内容本就落入未分组集合）。
    /// 按 TabId 恢复选中；被删分组的选中回退到「全部」。
    /// </summary>
    private void RebuildTabs()
    {
        var previousId = SelectedTab?.TabId;
        Tabs.Clear();

        Tabs.Add(new ClipboardTab(ClipboardTabKind.All, "全部"));
        Tabs.Add(new ClipboardTab(ClipboardTabKind.Pinned, "置顶"));
        foreach (var g in Groups)
            Tabs.Add(new ClipboardTab(g));
        Tabs.Add(new ClipboardTab(ClipboardTabKind.TypeText, "文本"));
        Tabs.Add(new ClipboardTab(ClipboardTabKind.TypeImage, "图像"));
        Tabs.Add(new ClipboardTab(ClipboardTabKind.TypeLink, "链接"));

        var selected = previousId == null
            ? Tabs[0]
            : Tabs.FirstOrDefault(t => t.TabId == previousId) ?? Tabs[0];
        foreach (var t in Tabs)
            t.IsSelected = ReferenceEquals(t, selected);
        SelectedTab = selected;
    }

    /// <summary>按当前标签生成空状态文案（搜索无结果时由 SearchNoResult 分支接管，不走此文案）。</summary>
    private (string Text, string Hint) BuildEmptyStateCopy() => (SelectedTab?.Kind ?? ClipboardTabKind.All) switch
    {
        ClipboardTabKind.Pinned => ("暂无置顶内容", "右键普通条目选择「置顶」，常驻内容会跨会话保留"),
        ClipboardTabKind.Group => ("该分组暂无内容", "选中此分组标签后，新复制的内容会归入该组"),
        ClipboardTabKind.TypeText => ("暂无文本内容", "复制文本后会出现在这里"),
        ClipboardTabKind.TypeImage => ("暂无图像内容", "复制或截图后会出现在这里；选中后按空格可预览大图"),
        ClipboardTabKind.TypeLink => ("暂无链接内容", "复制 http(s) 链接后会出现在这里"),
        _ => ("暂无复制内容", "在任意应用按 Ctrl+C 即可收集到这里"),
    };

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
        // 并选中新分组的标签（RebuildTabs 已重建标签集合，按 Id 找到对应标签）
        var tab = Tabs.FirstOrDefault(t => t.Kind == ClipboardTabKind.Group && t.Group?.Id == g.Id);
        if (tab != null) SelectTab(tab);
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

    /// <summary>
    /// 切换顶部标签（标签栏点击）。语义映射：
    /// - 分组标签 = 设为活动分组（置顶分组除外：仅查看，不改变采集目标）；
    /// - 全部 = 清空活动分组（回到全局采集 + 全局 FIFO）；
    /// - 置顶 / 文本 / 图像 / 链接 = 纯查看筛选，不改变采集目标。
    /// 退出分组模式的通道 = 点「全部」。
    /// </summary>
    [RelayCommand]
    private void SelectTab(ClipboardTab? tab)
    {
        if (tab == null || ReferenceEquals(tab, SelectedTab)) return;

        SelectedTab = tab;
        foreach (var t in Tabs)
            t.IsSelected = ReferenceEquals(t, tab);

        switch (tab.Kind)
        {
            case ClipboardTabKind.All:
                _state.ActiveGroupId = null;
                break;
            case ClipboardTabKind.Group when tab.Group is { IsPinned: false } g:
                _state.ActiveGroupId = g.Id;
                break;
        }
        RefreshActiveGroupFlags();
        RefreshAll(advanceIfMissing: false);
    }

    /// <summary>把分组设为活动分组（新复制内容归入此组）。置顶分组不可设为活动分组。</summary>
    private void SetActiveGroup(ClipboardGroup g)
    {
        // 置顶分组不允许成为活动分组：组内项享有置顶语义、不参与自动出队，
        // 一旦设为活动，新复制内容会持续落入该组，而 NormalCount 排除置顶分组，
        // 既绕过 MaxQueueItems 上限无限增长，又让新内容 Ctrl+V 粘不出来（"消失"）。
        if (g.IsPinned) return;
        _state.ActiveGroupId = g.Id;
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
