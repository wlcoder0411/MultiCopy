using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiCopy.Infrastructure;
using MultiCopy.Models;
using MultiCopy.Native;
using MultiCopy.Services;
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

    // 窗口状态写盘去重：避免每次隐藏到托盘都重写 settings.json
    private Rect _lastSavedBounds;
    private bool _lastSavedTopmost;
    private bool _hasSavedWindowState;

    // 拖拽导出状态
    private Point _dragStart;
    private ClipboardItem? _dragItem;
    private bool _suppressNextClick;
    private long _normalClickCooldownUntil;

    // 图像空格预览窗（ShowActivated=False + WS_EX_NOACTIVATE，不抢目标应用焦点）
    private ImagePreviewWindow? _imagePreview;

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
                CloseImagePreview(); // 预览窗不独立存活，随主窗口隐藏一并关闭
                SaveWindowState();
            }
            _targetHwndBeforeSearch = IntPtr.Zero;
        };
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;

        // Esc：先关预览，再清空搜索，最后隐藏窗口
        if (e.Key == Key.Escape)
        {
            if (_imagePreview != null)
            {
                CloseImagePreview();
            }
            else if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = string.Empty;
                SearchBox.Focus();
            }
            else
            {
                Hide();
            }
            e.Handled = true;
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // 上下方向键：驱动列表选择。保持搜索框焦点，保证继续输入筛选不中断。
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            _vm.MoveSelection(e.Key == Key.Down ? 1 : -1);
            if (_vm.SelectedEntry != null)
                ResultsList.ScrollIntoView(_vm.SelectedEntry);
            e.Handled = true;
            return;
        }

        bool searchFocused = SearchBox.IsKeyboardFocusWithin;
        bool searchHasText = !string.IsNullOrEmpty(SearchBox.Text);

        // Ctrl+Z：搜索框有内容时保留 TextBox 自带的文本撤销
        if (ctrl && e.Key == Key.Z && (!searchFocused || !searchHasText))
        {
            _vm.UndoLastRemovalCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // 空格：预览选中的图像项（PasteNow 式）。焦点在搜索框时是输入字符、
        // 在按钮上时是激活按钮，均不拦截；非图像项保持系统默认行为。
        if (e.Key == Key.Space
            && !searchFocused
            && Keyboard.FocusedElement is not ButtonBase
            && _vm.SelectedItem is ImageClipboardItem previewTarget)
        {
            ToggleImagePreview(previewTarget);
            e.Handled = true;
            return;
        }

        // 焦点在工具栏按钮上时不劫持 Enter/Delete，避免误触发粘贴或删除
        if (Keyboard.FocusedElement is ButtonBase) return;

        if (e.Key == Key.Enter)
        {
            ActivateSelectedItem();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.SelectedItem != null)
        {
            // 搜索框有文本时 Delete 用于删除字符，不删除条目
            if (searchFocused && searchHasText) return;
            _vm.DeleteItemCommand.Execute(_vm.SelectedItem);
            e.Handled = true;
        }
    }

    /// <summary>粘贴当前键盘选中项：置顶项粘贴后保留，普通项出队。</summary>
    private void ActivateSelectedItem()
    {
        if (_vm?.SelectedItem is not { } item) return;

        if (!RestoreTargetFocusBeforePaste())
        {
            (Application.Current as App)?.Tray?.ShowPasteBlockedTip();
            return;
        }

        if (_vm.IsKeepAfterPaste(item))
            _vm.PastePinnedItem(item);
        else
            _vm.PasteNormalItem(item);
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
        vm.PropertyChanged += OnViewModelPropertyChanged;
        try { Icon = IconFactory.CreateAppIcon(); }
        catch { /* 图标生成失败不影响运行 */ }
    }

    /// <summary>选择变化（自动续选/撤销恢复）时把对应行滚动到可见区域；预览打开时同步换图。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedEntry)) return;

        if (_vm?.SelectedEntry != null)
            ResultsList.ScrollIntoView(_vm.SelectedEntry);

        // 预览窗跟随选择：切到其他图像换大图，切到非图像项则关闭
        if (_imagePreview == null) return;
        if (_vm?.SelectedItem is ImageClipboardItem imageItem)
            _imagePreview.SetItem(imageItem);
        else
            CloseImagePreview();
    }

    // ---------- 支持作者：溢出菜单触发弹窗 ----------
    private void SupportAuthorMenu_Click(object sender, RoutedEventArgs e)
    {
        SupportDialog.Show(this);
    }

    /// <summary>「⋯」按钮：打开溢出菜单（低频操作收纳于此）。</summary>
    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu == null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    // ---------- 快捷键说明 ----------
    private void Help_Click(object sender, MouseButtonEventArgs e)
    {
        // 底部栏内嵌了撤销按钮，点按钮时不要同时弹出说明
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
        e.Handled = true;
        HelpDialog.Open(this);
    }

    private void OpenHelp_Click(object sender, RoutedEventArgs e) => HelpDialog.Open(this);

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
    /// 点列表项粘贴前调用：把焦点交给目标应用，让 SendInput Ctrl+V 到达目标。
    /// 目标窗口选择优先级：当前前台窗口（用户调出 MultiCopy 后可能切换了目标）> _targetHwndBeforeSearch（调出时记录的）。
    /// 这样用户在调出 MultiCopy 后切换到网页粘贴时，不会误把焦点切回复制源软件。
    /// 调用后强制重置 _searchActive，确保下次搜索框 GotFocus 重新记录目标。
    /// 返回粘贴是否可执行：true=前台已就绪为预期目标；false=前台仍是 MultiCopy 自身
    /// （Ctrl+V 会发给自己，粘贴必然失败），调用方应跳过粘贴并保留该项待用户重试。
    /// </summary>
    private bool RestoreTargetFocusBeforePaste()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        // 优先使用当前前台窗口：用户调出 MultiCopy 后可能切换了粘贴目标（如切到网页对话框）。
        // 若仍用 _targetHwndBeforeSearch（记录的是调出 MultiCopy 时的前台窗口），会把焦点误切回复制源软件。
        IntPtr currentFg = Win32.GetForegroundWindow();
        if (currentFg != IntPtr.Zero && currentFg != hwnd)
        {
            _targetHwndBeforeSearch = currentFg;
        }

        IntPtr target = _targetHwndBeforeSearch;
        _searchActive = false; // 强制下次 GotFocus 重新记录目标应用
        ApplyNoActivate();     // 恢复 NOACTIVATE（粘贴后窗口回到不抢焦点状态）

        // 目标无效或已销毁（窗口被关闭后句柄悬空）：清零丢弃，
        // 仅当前台非自身时粘贴才可能送达
        if (target == IntPtr.Zero || target == hwnd || !Win32.IsWindow(target))
        {
            _targetHwndBeforeSearch = IntPtr.Zero;
            return Win32.GetForegroundWindow() != hwnd;
        }

        // 目标已是前台：完全跳过切换。AttachThreadInput 会短暂合并/重置两线程的
        // 输入状态，对已就绪的焦点是无谓扰动——曾导致紧随的 Ctrl+V 偶发无法
        // 到达目标（项已出队却没粘上，内容静默丢失）。
        if (Win32.GetForegroundWindow() == target)
        {
            _targetHwndBeforeSearch = IntPtr.Zero; // 粘贴目标已就绪，消费掉记录
            return true;
        }

        try
        {
            uint targetThread = Win32.GetWindowThreadProcessId(target, out _);
            uint ourThread = Win32.GetCurrentThreadId();
            if (targetThread != ourThread)
            {
                Win32.AttachThreadInput(ourThread, targetThread, true);
                Win32.SetForegroundWindow(target);
                Win32.AttachThreadInput(ourThread, targetThread, false);
            }
            else
            {
                Win32.SetForegroundWindow(target);
            }

            // 轮询等待前台切换真正完成（上限 200ms，切换成功即提前退出）。
            // 固定 Sleep(30) 不验证切换结果：SetForegroundWindow 在前台锁下可能
            // 静默失败，此时注入的 Ctrl+V 会发给切换前的窗口（甚至 MultiCopy 自身），
            // 造成"出队了却没粘上"。识别失败则返回 false，让调用方保留该项。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Win32.GetForegroundWindow() != target && sw.ElapsedMilliseconds < 200)
            {
                System.Threading.Thread.Sleep(5);
            }
        }
        catch
        {
            // 失败不阻塞粘贴（尽力发送）
        }

        bool ok = Win32.GetForegroundWindow() == target;
        // 仅成功时消费记录；失败时保留 _targetHwndBeforeSearch，下次点击列表项
        // 自动重试切换——避免"一次失败后 target 被清零、前台仍是自身、后续点击
        // 恒返回 false"的软死循环（用户反复点击无反应）。
        if (ok) _targetHwndBeforeSearch = IntPtr.Zero;
        return ok;
    }

    // ---------- 条目：单击粘贴（普通项出队）/ 双击粘贴（置顶项保留） ----------
    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (e.ChangedButton != MouseButton.Left) return;

        // 拖拽结束后的鼠标抬起不再触发粘贴
        if (_suppressNextClick)
        {
            _suppressNextClick = false;
            return;
        }

        // 点击落在图钉/删除按钮上时不处理
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not ClipboardItemRow row) return;

        var item = row.Item;
        // 置顶项的双击在 PreviewMouseLeftButtonDown 中处理，MouseUp 不再参与
        if (_vm.IsKeepAfterPaste(item)) return;

        // 普通项仅单击；双击的第二次抬起若穿透到下一行，用冷却窗口拦截
        if (e.ClickCount != 1) return;
        if (Environment.TickCount64 < _normalClickCooldownUntil) return;

        // 搜索框曾获焦时把焦点还给目标应用；前台未就绪时跳过粘贴并保留该项待重试
        if (!RestoreTargetFocusBeforePaste())
        {
            (Application.Current as App)?.Tray?.ShowPasteBlockedTip();
            return;
        }

        _normalClickCooldownUntil = Environment.TickCount64 + 300;
        _vm.PasteNormalItem(item);
    }

    // ---------- 拖拽导出：把条目拖到目标窗口或文件夹 ----------
    private void ItemDragStart(object sender, MouseButtonEventArgs e)
    {
        _suppressNextClick = false;

        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null)
        {
            _dragItem = null;
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is ClipboardItemRow row)
        {
            // 置顶项：第二次按下（双击）直接粘贴并保留。
            // 用 PreviewMouseLeftButtonDown 的 ClickCount，避免第二次 MouseUp 被列表容器接管。
            if (e.ClickCount == 2 && _vm != null && _vm.IsKeepAfterPaste(row.Item))
            {
                _dragItem = null;
                e.Handled = true;
                if (!RestoreTargetFocusBeforePaste())
                {
                    (Application.Current as App)?.Tray?.ShowPasteBlockedTip();
                    return;
                }
                _vm.PastePinnedItem(row.Item);
                return;
            }

            _dragStart = e.GetPosition(this);
            _dragItem = row.Item;
        }
        else
        {
            _dragItem = null;
        }
    }

    private void ItemDragMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;

        Point pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        _dragItem = null;

        var data = BuildDragData(item);
        if (data == null) return;

        _suppressNextClick = true;
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private static DataObject? BuildDragData(ClipboardItem item)
    {
        var data = new DataObject();
        switch (item)
        {
            case TextClipboardItem text:
                data.SetData(DataFormats.UnicodeText, text.Text);
                data.SetData(DataFormats.Text, text.Text);
                data.SetData(DataFormats.StringFormat, text.Text);
                // 附带 CF_HTML：部分 Web/Electron 输入框优先接受 text/html
                data.SetData(DataFormats.Html, BuildHtmlClipboard(text.Text));
                break;
            case ImageClipboardItem img:
                data.SetData(DataFormats.Bitmap, img.Image);
                string? path = SaveTempPng(img);
                if (path != null) data.SetData(DataFormats.FileDrop, new[] { path });
                break;
            default:
                return null;
        }
        return data;
    }

    /// <summary>
    /// 构造 CF_HTML 剪贴板字符串（含标准头部与 UTF-8 字节偏移）。
    /// 部分 Web/Electron 输入框优先接受 text/html 拖拽数据。
    /// </summary>
    private static string BuildHtmlClipboard(string text)
    {
        string escaped = System.Net.WebUtility.HtmlEncode(text)
            .Replace("\r\n", "<br>")
            .Replace("\n", "<br>")
            .Replace("\r", "<br>");

        string html = "<html><body><!--StartFragment--><div>" + escaped
                    + "</div><!--EndFragment--></body></html>";

        const string headerFormat =
            "Version:1.0\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";

        // 偏移量为 10 位定宽，头部长度恒定为 startHtml
        string probe = string.Format(headerFormat, 0, 0, 0, 0);
        int startHtml = Encoding.UTF8.GetByteCount(probe);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount("<html><body><!--StartFragment-->");
        int endFragment = startFragment + Encoding.UTF8.GetByteCount("<div>" + escaped + "</div>");
        int endHtml = startHtml + Encoding.UTF8.GetByteCount(html);

        string header = string.Format(headerFormat, startHtml, endHtml, startFragment, endFragment);
        return header + html;
    }

    /// <summary>把图片写入临时 PNG，供拖到资源管理器等场景作为文件接收。</summary>
    private static string? SaveTempPng(ImageClipboardItem item)
    {
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MultiCopy", "drag");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, item.Id + ".png");
            if (!System.IO.File.Exists(path))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(item.Image));
                using var stream = System.IO.File.Create(path);
                encoder.Save(stream);
            }
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 标签栏：滚轮横向滚动（溢出时） ----------
    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 无横向溢出时不吞事件，让滚轮继续走默认路径
        if (TabScroll.ScrollableWidth <= 0) return;
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    // ---------- 图像空格预览 ----------
    /// <summary>开/关选中图像项的大图预览。预览窗不抢焦点，目标应用的键盘焦点不受影响。</summary>
    private void ToggleImagePreview(ImageClipboardItem item)
    {
        if (_imagePreview != null)
        {
            CloseImagePreview();
            return;
        }
        _imagePreview = new ImagePreviewWindow();
        _imagePreview.SetItem(item);
        _imagePreview.PlaceNear(this);
        _imagePreview.Closed += (_, _) => _imagePreview = null;
        _imagePreview.Show();
    }

    /// <summary>关闭图像预览窗（Esc/空格、选择切走、主窗口隐藏或关闭时调用）。</summary>
    private void CloseImagePreview()
    {
        _imagePreview?.Close();
        _imagePreview = null;
    }

    // ---------- 普通项右键菜单：动态构建"移动到分组…"子菜单 ----------
    // 在 ContextMenu.Opened 时重建子菜单，实时反映当前分组列表。
    // 右键不触发 MouseLeftButtonDown，故不会触发粘贴——天然防误触。
    private void ItemContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (sender is not ContextMenu ctxMenu) return;

        // 被右键的列表项（PlacementTarget.DataContext = ClipboardItemRow）
        if (ctxMenu.PlacementTarget is not FrameworkElement fe || fe.DataContext is not ClipboardItemRow row)
            return;

        var item = row.Item;

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

    // ---------- 窗口状态持久化 ----------

    /// <summary>应用持久化的窗口位置/尺寸/置顶状态（在 Show 之前调用）。</summary>
    public void ApplyPersistedWindowState(SettingsStorageService.SettingsDto settings)
    {
        Topmost = settings.IsTopmost;

        if (settings.WindowLeft is not double left || settings.WindowTop is not double top)
            return;

        double width = settings.WindowWidth ?? Width;
        double height = settings.WindowHeight ?? Height;

        // 虚拟屏范围内收敛：从大屏保存的几何回到小屏时，保证标题栏仍可抓取
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualWidth = SystemParameters.VirtualScreenWidth;
        double virtualHeight = SystemParameters.VirtualScreenHeight;
        double virtualRight = virtualLeft + virtualWidth;
        double virtualBottom = virtualTop + virtualHeight;

        // 虚拟屏范围内收敛：从大屏保存的几何回到小屏时，保证标题栏仍可抓取。
        // 预留边距，避免底部提示栏或右侧控件落到屏幕外。
        width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, virtualWidth - 16));
        height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, virtualHeight - 48));
        left = Math.Clamp(left, virtualLeft, Math.Max(virtualLeft, virtualRight - width - 8));
        top = Math.Clamp(top, virtualTop, Math.Max(virtualTop, virtualBottom - height - 8));

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>保存窗口几何与置顶状态（最大化/最小化时保存还原边界）。</summary>
    private void SaveWindowState()
    {
        // 从未显示过（如 --minimized 静默启动后直接退出）时不写盘，避免覆盖有效位置
        if (!IsLoaded) return;

        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        // 未变化时不写盘：隐藏/显示到托盘是高频操作
        if (_hasSavedWindowState && bounds == _lastSavedBounds && Topmost == _lastSavedTopmost)
            return;

        _lastSavedBounds = bounds;
        _lastSavedTopmost = Topmost;
        _hasSavedWindowState = true;

        SettingsStorageService.Update(s =>
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
            s.IsTopmost = Topmost;
        });
    }

    /// <summary>托盘菜单切换窗口置顶并持久化。</summary>
    public void SetTopmost(bool value)
    {
        Topmost = value;
        SettingsStorageService.Update(s => s.IsTopmost = value);
    }

    // ---------- 关闭→最小化到托盘（除非强制退出） ----------
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        var app = Application.Current as App;
        if (app != null && !app.ForceExit)
        {
            e.Cancel = true;
            CloseImagePreview();
            Hide(); // 最小化到托盘（Hide 内部会保存窗口状态）
        }
        else
        {
            CloseImagePreview();
            SaveWindowState();
        }
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
