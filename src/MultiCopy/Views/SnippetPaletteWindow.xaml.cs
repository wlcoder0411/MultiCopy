using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MultiCopy.Models;
using MultiCopy.Services;
using MultiCopy.ViewModels;
using MultiCopy.Native;

namespace MultiCopy.Views;

/// <summary>
/// 片段速取窗：全局热键（默认 Alt+S）呼出的小型速取框。
/// 打开时记录当前前台窗口作为粘贴目标；输入即过滤，Enter/单击原样粘贴（不追加前后缀/序号），
/// Esc 或失焦关闭。关闭后焦点自动回到目标窗口，再显式断言一次。
/// </summary>
public partial class SnippetPaletteWindow : Window
{
    private const string SecretMask = "●●●●●●";

    private readonly SnippetLibrary _snippets;
    private readonly PasteExecutor _paste;
    private readonly IntPtr _targetHwnd; // 打开时的前台窗口（粘贴目标）

    /// <summary>过滤后的片段条目（供列表绑定）。</summary>
    public ObservableCollection<SnippetEntry> Entries { get; } = new();

    public SnippetPaletteWindow(SnippetLibrary snippets, PasteExecutor paste)
    {
        _snippets = snippets;
        _paste = paste;
        // 必须在显示（抢焦点）之前记录前台窗口
        _targetHwnd = Win32.GetForegroundWindow();
        InitializeComponent();
        DataContext = this;
        _snippets.Changed += OnSnippetsChanged;
        // 窗口关闭时解绑，避免速取窗实例被库事件钉在内存里
        Closed += (_, _) =>
        {
            _snippets.Changed -= OnSnippetsChanged;
        };
        SourceInitialized += (_, _) =>
        {
            RestorePosition();
        };
        Loaded += (_, _) =>
        {
            // 尺寸（SizeToContent=Height）此时才确定：按实际高度再收敛一次，
            // 避免记住的位置靠近屏幕底部时窗口被任务栏/屏幕边缘截断
            ClampCurrentPosition();
            ActivatePalette();
        };
        RefreshList();
    }

    /// <summary>
    /// 强制激活速取窗并聚焦搜索框。
    /// 主窗口带 WS_EX_NOACTIVATE 且应用通常不在前台，Windows 的前台锁会拒绝
    /// ShowActivated 的激活请求——表现为窗口弹出来了，但按键仍落到原来的应用，
    /// 输入过滤与 Esc 全部失效。这里用与主窗口相同的 AttachThreadInput 方案强制激活。
    /// </summary>
    private void ActivatePalette()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

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

            Activate();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }
        catch
        {
            // 激活失败仍可鼠标操作，不影响其它功能
        }
    }

    private void OnSnippetsChanged(object? sender, EventArgs e) => RefreshList();

    /// <summary>SizeToContent=Height 下首次定位时高度尚未量出，用估算值做边界收敛。</summary>
    private const double EstimatedHeight = 320;

    /// <summary>
    /// 恢复上次拖动后的位置；从未拖动过时退回「跟随鼠标光标弹出」。
    /// </summary>
    private void RestorePosition()
    {
        var settings = SettingsStorageService.Load();
        if (settings.SnippetPaletteLeft is double left && settings.SnippetPaletteTop is double top)
        {
            ApplyPosition(left, top);
            return;
        }
        PositionNearCursor();
    }

    /// <summary>在鼠标光标附近显示。</summary>
    private void PositionNearCursor()
    {
        if (Win32.GetCursorPos(out var pt))
        {
            Point dip = ToDip(pt);
            ApplyPosition(dip.X + 8, dip.Y + 8);
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            ApplyPosition(wa.Left + (wa.Width - Width) / 2, wa.Top + 80);
        }
    }

    /// <summary>
    /// 把窗口放到指定 DIP 坐标，并收敛到虚拟屏范围内（多显示器也适用）。
    /// 尺寸未知时按估算高度收敛，Loaded 后会再按实际高度收敛一次。
    /// </summary>
    private void ApplyPosition(double left, double top)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : EstimatedHeight;

        // 不做额外内缩：保存的位置在屏幕内时原样恢复，只有越界才收敛
        Left = Math.Min(Math.Max(left, virtualLeft), Math.Max(virtualLeft, virtualRight - width));
        Top = Math.Min(Math.Max(top, virtualTop), Math.Max(virtualTop, virtualBottom - height));
    }

    /// <summary>按当前实际尺寸重新收敛位置（Loaded 时调用）。</summary>
    private void ClampCurrentPosition() => ApplyPosition(Left, Top);

    /// <summary>
    /// 物理像素坐标转 WPF 设备无关单位（DIP）。
    /// 进程为 PerMonitorV2，GetCursorPos 返回物理像素，直接当 Left/Top 会在
    /// 高缩放屏幕上产生偏移，必须经当前窗口的 DPI 变换。
    /// </summary>
    private Point ToDip(POINT pt)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
            return source.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y));
        return new Point(pt.X, pt.Y);
    }

    // ---------- 拖动窗口 ----------

    /// <summary>
    /// 按住窗口空白处拖动（搜索框、列表项、滚动条等交互控件除外）。
    /// 拖动结束后把坐标写入 settings.json，下次呼出固定在该位置。
    /// </summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        double beforeLeft = Left;
        double beforeTop = Top;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 窗口尚未完成初始化时 DragMove 会抛异常，忽略即可
            return;
        }

        // 仅真正移动过才记录：单击空白处不应把「跟随光标」锁定成固定位置
        if (Left != beforeLeft || Top != beforeTop)
            SavePosition();
    }

    /// <summary>沿可视/逻辑树向上判断是否落在交互控件内（这些区域不响应窗口拖动）。</summary>
    private static bool IsInteractiveElement(DependencyObject? source)
    {
        var d = source;
        while (d != null)
        {
            if (d is TextBoxBase || d is ListBoxItem || d is ScrollBar || d is ButtonBase)
                return true;

            d = d switch
            {
                Visual => VisualTreeHelper.GetParent(d),
                FrameworkContentElement fce => fce.Parent,
                _ => null
            };
        }
        return false;
    }

    /// <summary>持久化当前窗口位置（仅在用户拖动后调用）。</summary>
    private void SavePosition()
    {
        double left = Left;
        double top = Top;
        SettingsStorageService.Update(s =>
        {
            s.SnippetPaletteLeft = left;
            s.SnippetPaletteTop = top;
        });
    }

    private void RefreshList()
    {
        Entries.Clear();
        foreach (var s in _snippets.Search(SearchBox.Text))
            Entries.Add(new SnippetEntry(s));
        EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Entries.Count > 0)
        {
            SnipList.SelectedIndex = 0; // 预选第一条，Enter 直接粘贴
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    /// <summary>单击条目：粘贴并关闭。</summary>
    private void SnippetItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SnippetEntry entry })
            PasteAndClose(entry);
    }

    /// <summary>键盘：↑↓ 选择、Enter 粘贴、Esc 关闭（关闭延迟，避开 IME 分离流程）。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.Background);
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                if (SnipList.SelectedItem is SnippetEntry entry)
                    PasteAndClose(entry);
                else if (Entries.Count == 0)
                    Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.Background);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Entries.Count == 0) return;
        int index = SnipList.SelectedIndex + delta;
        index = Math.Clamp(index, 0, Entries.Count - 1);
        SnipList.SelectedIndex = index;
        SnipList.ScrollIntoView(SnipList.SelectedItem);
    }

    /// <summary>
    /// 关闭后把焦点交还目标窗口，再原样粘贴。
    /// 焦点还原与粘贴延迟到 Background 优先级执行：Close 会触发焦点切换与 IME(TSF) 分离，
    /// 同帧内再 SetForegroundWindow/注入按键可能与输入法线程互相等待造成死锁。
    /// </summary>
    private void PasteAndClose(SnippetEntry entry)
    {
        string content = entry.Model.Content;
        string id = entry.Model.Id;
        Close();
        Dispatcher.BeginInvoke(() =>
        {
            if (_targetHwnd != IntPtr.Zero && Win32.IsWindow(_targetHwnd))
                Win32.SetForegroundWindow(_targetHwnd);
            if (_paste.ExecuteText(content))
                _snippets.MarkUsed(id);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 点击窗体外部（失焦）自动关闭。关闭必须延迟：失焦瞬间正处于焦点切换/IME 分离流程，
    /// 同步 Close 会与之竞争造成死锁（Alt+S 二次呼出卡死的根因）。
    /// </summary>
    private void Window_Deactivated(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.Background);
    }
}
