using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
            PositionNearCursor();
        };
        Loaded += (_, _) =>
        {
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

    /// <summary>在鼠标光标附近显示（夹取到工作区内）。</summary>
    private void PositionNearCursor()
    {
        var wa = SystemParameters.WorkArea;
        // SizeToContent=Height 下高度未量出，用估算值夹取；宽 440 为定值
        const double estimatedHeight = 320;
        if (Win32.GetCursorPos(out var pt))
        {
            Left = Math.Min(Math.Max(pt.X + 8, wa.Left), Math.Max(wa.Left, wa.Right - Width - 4));
            Top = Math.Min(Math.Max(pt.Y + 8, wa.Top), Math.Max(wa.Top, wa.Bottom - estimatedHeight - 4));
        }
        else
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + 80;
        }
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
