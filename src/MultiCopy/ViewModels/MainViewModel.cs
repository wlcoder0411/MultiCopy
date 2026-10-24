using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiCopy.Models;
using MultiCopy.Services;
using MultiCopy.State;
using MultiCopy.Views;

namespace MultiCopy.ViewModels;

/// <summary>
/// 主窗口视图模型。负责队列列表绑定、模式开关双向同步、内容分组管理、各命令。
/// 点选粘贴由视图调用 <see cref="PasteNormalItem"/> / <see cref="PastePinnedItem"/>
/// （因单击/双击区分需在视图侧处理）。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ClipboardQueue _queue;
    private readonly PasteExecutor _pasteExecutor;
    private readonly AppState _state = AppState.Instance;

    // 缓存固定集合的默认视图，避免反复调用 CollectionViewSource.GetDefaultView。
    // 分组视图按需获取（分组动态创建/删除）。
    private readonly ICollectionView _pinnedView;
    private readonly ICollectionView _ungroupedView;
    private readonly ICollectionView _groupsView;

    /// <summary>未分组普通项（默认状态全部落此）。</summary>
    public ObservableCollection<ClipboardItem> UngroupedItems => _queue.UngroupedItems;

    /// <summary>用户创建的分组列表。</summary>
    public ObservableCollection<ClipboardGroup> Groups => _queue.Groups;

    public ObservableCollection<ClipboardItem> PinnedItems => _queue.PinnedItems;

    [ObservableProperty]
    private bool _modeOn;

    /// <summary>剪贴板监控开关（双向同步 AppState.IsMonitoring）。</summary>
    [ObservableProperty]
    private bool _isMonitoring;

    /// <summary>是否为亮色主题（绑定主题按钮图标显示）。</summary>
    [ObservableProperty]
    private bool _isLightTheme;

    /// <summary>是否存在任何分组（决定渲染模式：扁平 vs 分组）。</summary>
    [ObservableProperty]
    private bool _isGrouping;

    /// <summary>未分组项是否为空（控制分组模式下未分组区显示）。</summary>
    [ObservableProperty]
    private bool _ungroupedEmpty = true;

    /// <summary>普通项是否全部为空（含分组，用于空状态）。</summary>
    [ObservableProperty]
    private bool _normalEmpty = true;

    /// <summary>置顶区是否为空。</summary>
    [ObservableProperty]
    private bool _pinnedEmpty = true;

    /// <summary>全部为空（用于空状态提示）。</summary>
    [ObservableProperty]
    private bool _allEmpty = true;

    /// <summary>搜索关键字（双向绑定搜索框，UpdateSourceTrigger=PropertyChanged）。</summary>
    [ObservableProperty]
    private string? _searchText;

    /// <summary>键盘导航当前选中项（null=无选中）。</summary>
    [ObservableProperty]
    private ClipboardItem? _selectedItem;

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

    /// <summary>最近一次选择在可导航列表中的序号，用于出队后自动续选。</summary>
    private int _selectedIndexHint;

    /// <summary>当前是否在搜索状态（SearchText 非空白）。</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>搜索无结果（关键字非空且过滤后全空）。UI 据此显示"未找到"提示。</summary>
    public bool SearchNoResult =>
        IsSearching
        && GetFilteredCount(PinnedItems) == 0
        && GetFilteredCount(UngroupedItems) == 0
        && !AnyGroupHasFilteredMatch();

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

        // 缓存固定集合的默认视图（Pinned/Ungrouped/Groups），后续直接复用
        _pinnedView = CollectionViewSource.GetDefaultView(PinnedItems);
        _ungroupedView = CollectionViewSource.GetDefaultView(UngroupedItems);
        _groupsView = CollectionViewSource.GetDefaultView(Groups);

        _modeOn = _state.ModeOn;
        _state.ModeChanged += OnAppStateModeChanged;
        _isMonitoring = _state.IsMonitoring;
        _state.MonitoringChanged += OnAppStateMonitoringChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        IsLightTheme = ThemeService.CurrentTheme == AppTheme.Light;
        _queue.Changed += OnQueueChanged;
        _queue.Groups.CollectionChanged += OnGroupsChanged;
        UpdateStates();
    }

    private void OnAppStateModeChanged(object? sender, bool modeOn) => ModeOn = modeOn;

    private void OnAppStateMonitoringChanged(object? sender, bool value) => IsMonitoring = value;

    private void OnThemeChanged(object? sender, EventArgs e)
        => IsLightTheme = ThemeService.CurrentTheme == AppTheme.Light;

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
    {
        if (IsSearching)
        {
            // 入队/出队/移动后，组级别可见性可能变化（某组从无匹配→有匹配或反之），刷新 Groups 视图
            _groupsView.Refresh();
        }
        // 先刷新视图再重算状态/序号，保证 GroupHasMatch 过滤后的可见集合正确
        UpdateStates();
        RestoreSelectionAfterChange();
    }

    /// <summary>新建分组时给其 Items 默认视图附加 Filter（仅搜索状态下需要）。</summary>
    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && IsSearching && e.NewItems != null)
        {
            string keyword = SearchText!.Trim();
            foreach (ClipboardGroup g in e.NewItems)
            {
                CollectionViewSource.GetDefaultView(g.Items).Filter =
                    obj => ItemMatches(obj, keyword);
            }
        }
    }

    /// <summary>SearchText 变更时重新应用过滤并刷新相关计算属性。</summary>
    partial void OnSearchTextChanged(string? value)
    {
        ApplySearch();
        // 选中项被搜索条件过滤掉时清除选择，避免对不可见项执行粘贴
        EnsureSelectionVisible();
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(SearchNoResult));
        UpdateStates();
    }

    /// <summary>
    /// 对所有列表的默认视图设置/清除 Filter。
    /// keyword==null 时清除 Filter（显示全部）；否则设置 ItemMatches 谓词。
    /// Groups 视图额外用 GroupHasMatch 判断整组是否显示。
    /// </summary>
    private void ApplySearch()
    {
        string? keyword = IsSearching ? SearchText!.Trim() : null;

        // 1. Pinned / Ungrouped 默认视图（使用缓存视图）
        _pinnedView.Filter = keyword == null ? null : obj => ItemMatches(obj, keyword);
        _ungroupedView.Filter = keyword == null ? null : obj => ItemMatches(obj, keyword);

        // 2. Groups 默认视图（组级别：该组是否有任意匹配项）
        _groupsView.Filter = keyword == null ? null : obj => GroupHasMatch((ClipboardGroup)obj, keyword);

        // 3. 每个 group.Items 默认视图（项级别，分组动态创建故按需获取）
        foreach (var g in Groups)
        {
            CollectionViewSource.GetDefaultView(g.Items).Filter =
                keyword == null ? null : obj => ItemMatches(obj, keyword);
        }
    }

    /// <summary>
    /// 条目是否包含关键字（部分匹配，不区分大小写）。
    /// 文本项按 Text 过滤；图片项按元数据（SourceApp + Preview，如"截图 2026-07-02 14:30 截图 1920x1080"）过滤。
    /// 图片不支持内容搜索（不引入 OCR）。
    /// </summary>
    private static bool ItemMatches(object item, string keyword)
    {
        if (item is not ClipboardItem ci) return false;
        return ci switch
        {
            TextClipboardItem text => text.Text != null && text.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase),
            ImageClipboardItem img => (img.SourceApp + " " + img.Preview).Contains(keyword, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>分组内是否有任意条目匹配关键字（直接遍历原始 Items，不依赖视图 Count）。</summary>
    private static bool GroupHasMatch(ClipboardGroup g, string keyword)
    {
        foreach (var ci in g.Items)
            if (ItemMatches(ci, keyword))
                return true;
        return false;
    }

    /// <summary>
    /// 获取集合默认视图过滤后的项数。
    /// 优先用 ListCollectionView.Count（内部维护过滤后计数，O(1)），
    /// 回退到手动遍历（防御性，理论上不会走到）。
    /// </summary>
    private static int GetFilteredCount(IEnumerable source)
    {
        var view = CollectionViewSource.GetDefaultView(source);
        if (view is ListCollectionView lcv) return lcv.Count;
        int n = 0;
        foreach (var _ in view) n++;
        return n;
    }

    /// <summary>是否存在任意分组的过滤后视图非空。</summary>
    private bool AnyGroupHasFilteredMatch()
    {
        foreach (var g in Groups)
            if (GetFilteredCount(g.Items) > 0)
                return true;
        return false;
    }

    private void UpdateStates()
    {
        IsGrouping = _queue.IsGrouping;
        // 搜索时基于过滤后视图计数；不搜索时 Filter=null，GetFilteredCount == 原始 Count
        UngroupedEmpty = GetFilteredCount(UngroupedItems) == 0;
        PinnedEmpty = GetFilteredCount(PinnedItems) == 0;
        NormalEmpty = UngroupedEmpty && !AnyGroupHasFilteredMatch();
        AllEmpty = NormalEmpty && PinnedEmpty;
        // 容量指示（口径与实际上限检查一致：NormalCount 排除置顶分组）
        int normalCount = _queue.NormalCount;
        CapacityText = $"{normalCount}/{AppState.MaxQueueItems}";
        IsCapacityWarning = normalCount >= AppState.MaxQueueItems;
        // 撤销按钮可用性
        CanUndo = _queue.CanUndo;
        // 底部动态提示：空队列→引导收集；有可撤销操作→提示撤销；否则常规快捷键
        OperationHint = _queue.IsEmpty
            ? EmptyOperationHint
            : (CanUndo ? "可撤销上一步 · Ctrl+Z 撤销 · 点击此处查看全部快捷键" : DefaultOperationHint);
        // 列表展示序号（置顶 → 未分组 → 各分组，连续编号）
        RefreshDisplayIndexes();
        // 搜索专属属性刷新
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(SearchNoResult));
    }

    // ---------- 键盘导航 ----------

    /// <summary>选中项变化时同步所有条目的高亮标记（含被移出队列的旧选中项，避免撤销后重复高亮）。</summary>
    partial void OnSelectedItemChanged(ClipboardItem? oldValue, ClipboardItem? newValue)
    {
        if (oldValue != null)
            oldValue.IsKeyboardSelected = false;

        foreach (var item in AllItems())
        {
            if (!ReferenceEquals(item, newValue))
                item.IsKeyboardSelected = false;
        }
        if (newValue != null)
        {
            newValue.IsKeyboardSelected = true;
            // 同步续选提示索引，确保鼠标/撤销等非方向键来源的选择也能正确续选
            int index = BuildNavigableList().IndexOf(newValue);
            if (index >= 0) _selectedIndexHint = index;
        }
    }

    private IEnumerable<ClipboardItem> AllItems()
    {
        foreach (var item in PinnedItems) yield return item;
        foreach (var item in UngroupedItems) yield return item;
        foreach (var g in Groups)
            foreach (var item in g.Items)
                yield return item;
    }

    /// <summary>
    /// 构建键盘可导航列表（按视觉顺序）：置顶项 → 未分组项 → 各展开分组项。
    /// 各段均通过集合视图枚举，天然尊重当前搜索过滤。
    /// </summary>
    private List<ClipboardItem> BuildNavigableList()
    {
        var list = new List<ClipboardItem>();

        foreach (var obj in _pinnedView)
            if (obj is ClipboardItem pinned) list.Add(pinned);

        foreach (var obj in _ungroupedView)
            if (obj is ClipboardItem ungrouped) list.Add(ungrouped);

        foreach (var obj in _groupsView)
        {
            if (obj is not ClipboardGroup g || !g.IsExpanded) continue;
            foreach (var itemObj in CollectionViewSource.GetDefaultView(g.Items))
                if (itemObj is ClipboardItem item) list.Add(item);
        }

        return list;
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

    /// <summary>按方向移动键盘选择（delta=+1 向下，-1 向上）。</summary>
    public void MoveSelection(int delta)
    {
        var list = BuildNavigableList();
        if (list.Count == 0)
        {
            SelectedItem = null;
            _selectedIndexHint = 0;
            return;
        }

        int current = SelectedItem == null ? -1 : list.IndexOf(SelectedItem);
        int next = current < 0
            ? (delta > 0 ? 0 : list.Count - 1)
            : Math.Clamp(current + delta, 0, list.Count - 1);

        SelectedItem = list[next];
        _selectedIndexHint = next;
    }

    /// <summary>选中项被粘贴/删除后，按原序号自动续选下一条，支持连续键盘操作。</summary>
    private void RestoreSelectionAfterChange()
    {
        if (SelectedItem == null) return;
        if (ContainsAnywhere(SelectedItem)) return;

        var list = BuildNavigableList();
        if (list.Count == 0)
        {
            SelectedItem = null;
            _selectedIndexHint = 0;
            return;
        }

        int index = Math.Clamp(_selectedIndexHint, 0, list.Count - 1);
        SelectedItem = list[index];
        _selectedIndexHint = index;
    }

    private bool ContainsAnywhere(ClipboardItem item)
    {
        if (PinnedItems.Contains(item) || UngroupedItems.Contains(item)) return true;
        foreach (var g in Groups)
            if (g.Items.Contains(item)) return true;
        return false;
    }

    /// <summary>搜索/折叠变化后，若选中项已不可见则清除选择，避免对不可见项执行粘贴。</summary>
    private void EnsureSelectionVisible()
    {
        if (SelectedItem == null) return;
        if (BuildNavigableList().Contains(SelectedItem)) return;
        SelectedItem = null;
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
    private void DeleteNormal(ClipboardItem? item)
    {
        if (item != null) _queue.RemoveNormal(item);
    }

    [RelayCommand]
    private void DeletePinned(ClipboardItem? item)
    {
        if (item != null) _queue.RemovePinned(item);
    }

    [RelayCommand]
    private void ClearNormal() => _queue.ClearNormal();

    [RelayCommand]
    private void ClearAll() => _queue.ClearAll();

    /// <summary>撤销最近一次粘贴出队或删除（最多回退 8 步），并高亮被恢复的条目。</summary>
    [RelayCommand]
    private void UndoLastRemoval()
    {
        var restored = _queue.UndoLastRemoval();
        if (restored != null)
            SelectedItem = restored;
    }

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
        // 折叠后选中项可能被隐藏，清掉以免 Enter 粘到不可见项
        EnsureSelectionVisible();
        // 折叠/展开会改变可见条目集合，序号需重排
        RefreshDisplayIndexes();
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
