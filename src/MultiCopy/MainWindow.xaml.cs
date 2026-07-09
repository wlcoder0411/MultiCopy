using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MultiCopy.Infrastructure;
using MultiCopy.Models;
using MultiCopy.Native;
using MultiCopy.State;
using MultiCopy.ViewModels;

using MultiCopy.Views;

namespace MultiCopy;

/// <summary>
/// 主窗口。置顶 + WS_EX_NOACTIVATE（点击列表项不抢目标应用焦点）。
/// 普通项单击=粘贴并移除；置顶项双击=粘贴并保留。
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _searchActive; // 搜索框是否持有激活状态（避免重复切换 WS_EX_NOACTIVATE）
    private IntPtr _targetHwndBeforeSearch = IntPtr.Zero; // 搜索框获焦前的目标应用窗口（粘贴时还原焦点用）

    public MainWindow()
    {
        InitializeComponent();
        // 拦截最小化：WS_EX_NOACTIVATE + ShowInTaskbar=False 窗口最小化会缩到屏幕左下角
        // （没有任务栏按钮接收最小化状态）。改为隐藏到托盘，与关闭按钮行为一致；
        // Hide()/Show() 自动保留原位置和大小，托盘双击或右键"显示/隐藏窗口"即可恢复。
        StateChanged += MainWindow_StateChanged;
        // Esc 键清空搜索框
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        // 窗口失活（用户切到其他窗口）时恢复 NOACTIVATE 并更新目标窗口。
        // 解决 SearchBox_LostFocus 在窗口失活时不触发的问题：调出 MultiCopy 后移除了 NOACTIVATE，
        // 若用户切换到其他窗口而 LostFocus 未触发，NOACTIVATE 不会恢复，点击列表项时 MultiCopy
        // 会抢占前台，导致 GetForegroundWindow() 返回自身、无法识别用户实际切换的目标窗口。
        Deactivated += (_, _) =>
        {
            if (_searchActive)
            {
                // Deactivated 触发时新前台窗口已就绪，记录为粘贴目标
                IntPtr newFg = Win32.GetForegroundWindow();
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (newFg != IntPtr.Zero && newFg != hwnd)
                {
                    _targetHwndBeforeSearch = newFg;
                }
                _searchActive = false;
                ApplyNoActivate();
            }
        };
        // 窗口可见性联动监控开关：隐藏至托盘→关监控，恢复显示→开监控
        // 覆盖所有入口（最小化按钮/关闭按钮/托盘菜单/快捷键），无需逐处替换 Hide()
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                // 恢复显示时自动开启监控（联动：最小化至托盘关、恢复显示开）
                AppState.Instance.IsMonitoring = true;
            }
            else
            {
                // 隐藏至托盘时自动关闭监控（避免日常复制粘贴被拦截）
                AppState.Instance.IsMonitoring = false;
                if (_searchActive)
                {
                    _searchActive = false;
                    ApplyNoActivate();
                }
            }
            _targetHwndBeforeSearch = IntPtr.Zero;
        };
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = string.Empty;
            // 绑定 UpdateSourceTrigger=PropertyChanged 会自动同步到 SearchText
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal; // 先复位，避免下次 Show 仍是最小化态
            Hide();                           // 隐藏到托盘
        }
    }

    public void SetViewModel(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        try { Icon = IconFactory.CreateAppIcon(); }
        catch { /* 图标生成失败不影响运行 */ }
    }

    // ---------- 支持作者：点击标题栏触发弹窗 ----------
    private void SupportAuthor_Click(object sender, MouseButtonEventArgs e)
    {
        SupportDialog.Show(this);
    }

    // ---------- 设置：点击齿轮按钮弹出设置对话框 ----------
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var app = Application.Current as App;
        if (app?.HotkeyService != null)
            SettingsDialog.Show(this, app.HotkeyService);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyNoActivate();
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        (Application.Current as App)?.StartBackendServices(hwnd);
    }

    /// <summary>
    /// 从托盘/全局快捷键调出窗口：显示 + 强制激活 + 聚焦搜索框。
    /// 复用 SearchBox_GotFocus 的激活机制（移除 NOACTIVATE + AttachThreadInput），
    /// 但在激活前先记录当前前台窗口为目标应用（供后续点列表项粘贴还原焦点）。
    /// 必须在 SetForegroundWindow 之前记录目标，否则 _targetHwndBeforeSearch 会被清零。
    /// </summary>
    public void BringUpAndFocusSearch()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // 1. 先记录当前前台窗口（用户所在应用）—— 必须在 SetForegroundWindow 之前
            if (!_searchActive)
            {
                _targetHwndBeforeSearch = Win32.GetForegroundWindow();
                if (_targetHwndBeforeSearch == hwnd) _targetHwndBeforeSearch = IntPtr.Zero;
                _searchActive = true; // 置位，使后续 SearchBox_GotFocus 跳过重复记录
            }

            // 2. 移除 WS_EX_NOACTIVATE（窗口才能被激活、搜索框才能接收键盘输入）
            IntPtr ex = Win32.GetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE);
            int style = ex.ToInt32() & ~Constants.WS_EX_NOACTIVATE;
            Win32.SetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE, new IntPtr(style));

            // 3. AttachThreadInput 跨进程强制激活（与 SearchBox_GotFocus 一致）
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != hwnd)
            {
                uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
                uint ourThread = Win32.GetCurrentThreadId();
                if (fgThread != ourThread)
                {
                    Win32.AttachThreadInput(fgThread, ourThread, true);
                    Win32.SetForegroundWindow(hwnd);
                    Win32.AttachThreadInput(fgThread, ourThread, false);
                }
                else
                {
                    Win32.SetForegroundWindow(hwnd);
                }
            }
            else
            {
                Win32.SetForegroundWindow(hwnd);
            }

            // 4. 聚焦搜索框（_searchActive 已 true，GotFocus 不会重复记录目标）
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }
        catch
        {
            // 失败不影响其他功能（与 SearchBox_GotFocus 容错策略一致）
        }
    }

    /// <summary>追加 WS_EX_NOACTIVATE：窗口接收鼠标点击但不被激活，目标应用保有焦点。</summary>
    private void ApplyNoActivate()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            IntPtr ex = Win32.GetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE);
            int style = ex.ToInt32() | Constants.WS_EX_NOACTIVATE;
            Win32.SetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE, new IntPtr(style));
        }
        catch
        {
            // 失败则退化为普通置顶窗口（点选粘贴焦点处理可能不稳，但不崩溃）
        }
    }

    /// <summary>移除 WS_EX_NOACTIVATE 并强制激活窗口，让搜索框能接收键盘输入。</summary>
    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!_searchActive)
        {
            // 首次获焦：记录当前前台窗口（目标应用），后续点列表项粘贴时把焦点还给它
            _targetHwndBeforeSearch = Win32.GetForegroundWindow();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (_targetHwndBeforeSearch == hwnd) _targetHwndBeforeSearch = IntPtr.Zero;
            _searchActive = true;
        }
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            // 1. 移除 WS_EX_NOACTIVATE
            IntPtr ex = Win32.GetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE);
            int style = ex.ToInt32() & ~Constants.WS_EX_NOACTIVATE;
            Win32.SetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE, new IntPtr(style));

            // 2. 强制激活窗口：用 AttachThreadInput 把前台线程的输入状态附加到本线程
            //    （SetForegroundWindow 在跨进程时通常被 Windows 限制，AttachThreadInput 是经典绕过）
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != hwnd)
            {
                uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
                uint ourThread = Win32.GetCurrentThreadId();
                if (fgThread != ourThread)
                {
                    Win32.AttachThreadInput(fgThread, ourThread, true);
                    Win32.SetForegroundWindow(hwnd);
                    Win32.AttachThreadInput(fgThread, ourThread, false);
                }
                else
                {
                    Win32.SetForegroundWindow(hwnd);
                }
            }
            else
            {
                Win32.SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // 失败不影响其他功能
        }
    }

    /// <summary>搜索框失焦后恢复 WS_EX_NOACTIVATE（点选粘贴不抢焦点的核心机制）。</summary>
    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_searchActive) return;
        _searchActive = false;
        ApplyNoActivate(); // 加回 WS_EX_NOACTIVATE
        // 不在这里还原目标应用焦点：下一步可能是点列表项粘贴（由 RestoreTargetFocusBeforePaste 处理）
        // 若是其他场景（点主题按钮/Esc 等），目标应用焦点自然会在用户下次点击时切换
    }

    /// <summary>
    /// 点列表项粘贴前调用：把焦点还给目标应用，让 SendInput Ctrl+V 到达目标。
    /// 目标窗口选择优先级：当前前台窗口（用户调出 MultiCopy 后可能切换了目标）> _targetHwndBeforeSearch（调出时记录的）。
    /// 这样用户在调出 MultiCopy 后切换到网页粘贴时，不会误把焦点切回复制源软件。
    /// 调用后强制重置 _searchActive，确保下次搜索框 GotFocus 重新记录目标。
    /// </summary>
    private void RestoreTargetFocusBeforePaste()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        // 优先使用当前前台窗口：用户调出 MultiCopy 后可能切换了粘贴目标（如切到网页对话框）。
        // 若仍用 _targetHwndBeforeSearch（记录的是调出 MultiCopy 时的前台窗口），会把焦点误切回复制源软件。
        IntPtr currentFg = Win32.GetForegroundWindow();
        if (currentFg != IntPtr.Zero && currentFg != hwnd)
        {
            _targetHwndBeforeSearch = currentFg;
        }

        if (_targetHwndBeforeSearch == IntPtr.Zero)
        {
            _searchActive = false; // 兜底：确保状态一致
            return;
        }
        try
        {
            if (_targetHwndBeforeSearch == hwnd)
            {
                _targetHwndBeforeSearch = IntPtr.Zero;
                _searchActive = false;
                return;
            }
            uint targetThread = Win32.GetWindowThreadProcessId(_targetHwndBeforeSearch, out _);
            uint ourThread = Win32.GetCurrentThreadId();
            if (targetThread != ourThread)
            {
                Win32.AttachThreadInput(ourThread, targetThread, true);
                Win32.SetForegroundWindow(_targetHwndBeforeSearch);
                Win32.AttachThreadInput(ourThread, targetThread, false);
            }
            else
            {
                Win32.SetForegroundWindow(_targetHwndBeforeSearch);
            }
            // 给 Windows 一点时间完成前台切换（SendInput 紧随其后）
            // 30ms 是经验值：足够 SetForegroundWindow 生效，又不至于让用户感知卡顿
            System.Threading.Thread.Sleep(30);
        }
        catch
        {
            // 失败不阻塞粘贴
        }
        _targetHwndBeforeSearch = IntPtr.Zero;
        _searchActive = false; // 强制下次 GotFocus 重新记录目标应用
        ApplyNoActivate();     // 恢复 NOACTIVATE（粘贴后窗口回到不抢焦点状态）
    }

    // ---------- 普通项：单击粘贴并移除 ----------
    private void NormalItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        // 点击落在图钉/删除按钮上时不处理（按钮已吞掉 MouseLeftButtonDown）
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;

        if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
        {
            RestoreTargetFocusBeforePaste(); // 搜索框曾获焦时把焦点还给目标应用
            _vm.PasteNormalItem(item);
        }
    }

    // ---------- 置顶项：双击粘贴并保留（ClickCount==2 为双击；单击不做任何事） ----------
    private void PinnedItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (e.ClickCount < 2) return; // 仅双击触发
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;

        if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
        {
            RestoreTargetFocusBeforePaste();
            _vm.PastePinnedItem(item);
        }
    }

    // ---------- 分组头：单击设为活动分组（新复制内容归入此组） ----------
    private void GroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        // 箭头列已自行处理并标记 Handled，这里不会再收到；按钮命中不处理
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;

        if (sender is FrameworkElement fe && fe.DataContext is ClipboardGroup g)
        {
            _vm.SetActiveGroupCommand.Execute(g);
        }
    }

    // ---------- 分组箭头：单击切换折叠/展开（阻止冒泡到分组头） ----------
    private void GroupArrow_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        e.Handled = true; // 阻止冒泡到 GroupHeader_Click
        if (sender is FrameworkElement fe && fe.DataContext is ClipboardGroup g)
        {
            _vm.ToggleGroupExpandCommand.Execute(g);
        }
    }

    // ---------- 普通项右键菜单：动态构建"移动到分组…"子菜单 ----------
    // 在 ContextMenu.Opened 时重建子菜单，实时反映当前分组列表。
    // 右键不触发 MouseLeftButtonDown，故不会触发粘贴——天然防误触。
    private void ItemContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (sender is not ContextMenu ctxMenu) return;

        // 被右键的列表项（PlacementTarget.DataContext = ClipboardItem）
        if (ctxMenu.PlacementTarget is not FrameworkElement fe || fe.DataContext is not ClipboardItem item)
            return;

        // 找到"移动到分组…"MenuItem（第一个）
        MenuItem? moveMenu = null;
        foreach (var mi in ctxMenu.Items)
        {
            if (mi is MenuItem m && m.Header is string s && s.StartsWith("移动到分组"))
            {
                moveMenu = m;
                break;
            }
        }
        if (moveMenu == null) return;

        // 查询项当前所在分组（用于标记当前位置 + 禁用移到自身）
        string? currentGroupId = _vm.GetGroupIdOf(item);

        moveMenu.Items.Clear();

        // "未分组（默认）"选项
        var ungroupedItem = new MenuItem { Header = "未分组（默认）" };
        if (currentGroupId == null)
        {
            ungroupedItem.IsChecked = true;
            ungroupedItem.IsEnabled = false; // 已在未分组，无需移动
        }
        else
        {
            var capturedItem = item;
            ungroupedItem.Click += (_, _) => _vm.MoveItem(capturedItem, null);
        }
        moveMenu.Items.Add(ungroupedItem);

        // 各分组
        if (_vm.Groups.Count > 0)
        {
            moveMenu.Items.Add(new Separator());
            foreach (var g in _vm.Groups)
            {
                var groupItem = new MenuItem { Header = g.Name };
                if (g.Id == currentGroupId)
                {
                    groupItem.IsChecked = true;
                    groupItem.IsEnabled = false; // 已在此分组，无需移动
                }
                else
                {
                    var capturedItem = item;
                    var capturedGroupId = g.Id;
                    groupItem.Click += (_, _) => _vm.MoveItem(capturedItem, capturedGroupId);
                }
                moveMenu.Items.Add(groupItem);
            }
        }

        // 无任何分组时，子菜单只有"未分组"，整个菜单可禁用提示先建分组
        if (_vm.Groups.Count == 0)
        {
            moveMenu.ToolTip = "暂无分组，请先点顶部「+分组」创建";
        }
        else
        {
            moveMenu.ToolTip = null;
        }
    }

    // ---------- 关闭→最小化到托盘（除非强制退出） ----------
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        var app = Application.Current as App;
        if (app != null && !app.ForceExit)
        {
            e.Cancel = true;
            Hide(); // 最小化到托盘
        }
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
