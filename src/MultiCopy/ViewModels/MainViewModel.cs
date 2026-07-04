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
    private void ToggleTheme() => ThemeService.Toggle();

    // ObservableProperty 生成的 ModeOn setter 变化时同步回 AppState
    partial void OnModeOnChanged(bool value) => _state.ModeOn = value;

    // ObservableProperty 生成的 IsMonitoring setter 变化时同步回 AppState
    partial void OnIsMonitoringChanged(bool value) => _state.IsMonitoring = value;

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        UpdateStates();
        if (IsSearching)
        {
            // 入队/出队/移动后，组级别可见性可能变化（某组从无匹配→有匹配或反之），刷新 Groups 视图
            _groupsView.Refresh();
        }
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
        // 搜索专属属性刷新
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(SearchNoResult));
    }

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
    }

    [RelayCommand]
    private void SetActiveGroup(ClipboardGroup? g)
    {
        _state.ActiveGroupId = g?.Id;
        RefreshActiveGroupFlags();
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
