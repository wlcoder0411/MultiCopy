using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using MultiCopy.Models;
using MultiCopy.Services;
using MultiCopy.ViewModels;
using MultiCopy.Native;

namespace MultiCopy.Views;

/// <summary>
/// 片段库管理窗：独立于主窗口的 Topmost 工具窗（可拖动/调整大小），常用语句的增删改与速取。
/// 焦点机制与主窗口一致：常态 WS_EX_NOACTIVATE（点条目不抢焦点，目标应用直接被粘贴）；
/// 点搜索框时临时移除 NOACTIVATE 并强制激活以便输入，失焦后记录新前台窗口为粘贴目标并恢复 NOACTIVATE。
/// 单击条目 = 原样粘贴（不追加前后缀/序号）并记录使用时间。
/// </summary>
public partial class SnippetManagerWindow : Window
{
    private readonly SnippetLibrary _snippets;
    private readonly PasteExecutor _paste;
    private IntPtr _targetHwnd = IntPtr.Zero; // 粘贴目标窗口（最近一次激活前的前台窗口）
    private bool _activated;                  // 当前是否处于"已激活可输入"状态

    /// <summary>过滤后的片段条目（供列表绑定）。</summary>
    public ObservableCollection<SnippetEntry> Entries { get; } = new();

    public SnippetManagerWindow(SnippetLibrary snippets, PasteExecutor paste)
    {
        _snippets = snippets;
        _paste = paste;
        InitializeComponent();
        DataContext = this;
        _snippets.Changed += OnSnippetsChanged;
        // 关闭时解绑，避免窗口实例被库事件钉在内存里
        Closed += (_, _) => _snippets.Changed -= OnSnippetsChanged;
        SourceInitialized += (_, _) => ApplyNoActivate();
        // 注意：不要在 Loaded 里自动激活！Show 的激活流程尚未完成时在这里
        // 摘/挂 NOACTIVATE + SetForegroundWindow 会与 WPF 的激活机制互相触发，
        // 形成 WM_ACTIVATE 风暴卡死 UI 线程。
        // 激活一律由用户动作触发（点搜索框 → SnippetSearchBox_GotFocus）。
        RefreshList();
    }

    // ---------- 焦点机制（照搬主窗口 NOACTIVATE + 搜索激活模式） ----------

    private void ApplyNoActivate()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        IntPtr ex = Win32.GetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE);
        int style = ex.ToInt32() | Constants.WS_EX_NOACTIVATE;
        Win32.SetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>移除 NOACTIVATE 并强制激活窗口，聚焦搜索框（仅在用户主动点击搜索框时调用）。</summary>
    private void ActivateForTyping()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // 1. 记录激活前的前台窗口为粘贴目标（必须先于 SetForegroundWindow）
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != hwnd) _targetHwnd = fg;

            // 2. 移除 WS_EX_NOACTIVATE（否则窗口无法激活、搜索框收不到键盘输入）
            IntPtr ex = Win32.GetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE);
            int style = ex.ToInt32() & ~Constants.WS_EX_NOACTIVATE;
            Win32.SetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE, new IntPtr(style));
            _activated = true;

            // 3. AttachThreadInput 跨进程强制激活（与主窗口 BringUpAndFocusSearch 一致）
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

            // 4. 聚焦搜索框
            SnippetSearchBox.Focus();
            Keyboard.Focus(SnippetSearchBox);
        }
        catch
        {
            // 激活失败不影响其他功能（与主窗口 BringUpAndFocusSearch 容错策略一致）
        }
    }

    /// <summary>失焦：记录新前台窗口为粘贴目标，恢复 NOACTIVATE（与主窗口 Deactivated 一致）。</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_activated) return;
        IntPtr newFg = Win32.GetForegroundWindow();
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (newFg != IntPtr.Zero && newFg != hwnd) _targetHwnd = newFg;
        _activated = false;
        ApplyNoActivate();
    }

    /// <summary>点击搜索框（窗口处于 NOACTIVATE 态）：重新激活以接收键盘输入。</summary>
    private void SnippetSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!_activated) ActivateForTyping();
    }

    // ---------- 列表与操作 ----------

    private void OnSnippetsChanged(object? sender, EventArgs e) => RefreshList();

    private void RefreshList()
    {
        Entries.Clear();
        foreach (var s in _snippets.Search(SnippetSearchBox.Text))
            Entries.Add(new SnippetEntry(s));
    }

    private void SnippetSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    /// <summary>单击条目：原样粘贴到目标窗口（不追加前后缀/序号）。</summary>
    private void SnippetItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SnippetEntry entry })
            PasteSnippet(entry);
    }

    private void PasteSnippet(SnippetEntry entry)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        IntPtr fg = Win32.GetForegroundWindow();

        // 焦点在管理窗自身（刚在搜索框输入过）时，先还原到记录的目标窗口；
        // 否则前台窗口就是目标（管理窗为 NOACTIVATE 态，点条目不影响前台）
        if (fg == hwnd)
        {
            if (_targetHwnd == IntPtr.Zero || !Win32.IsWindow(_targetHwnd)) return;
            Win32.SetForegroundWindow(_targetHwnd);
        }

        if (_paste.ExecuteText(entry.Model.Content))
            _snippets.MarkUsed(entry.Model.Id);
    }

    private void AddSnippet_Click(object sender, RoutedEventArgs e)
    {
        var result = SnippetEditDialog.Show(this, "新增片段");
        if (result == null) return;
        _snippets.Add(result.Name, result.Content, result.IsSecret);
    }

    /// <summary>编辑片段（片段库内右键菜单）。[RelayCommand] 生成 EditSnippetCommand 供菜单绑定。</summary>
    [RelayCommand]
    private void EditSnippet(SnippetEntry entry)
    {
        var m = entry.Model;
        var result = SnippetEditDialog.Show(this, "编辑片段", m.Name, m.Content, m.IsSecret, m.Id);
        if (result == null) return;
        _snippets.Update(result.Id, result.Name, result.Content, result.IsSecret);
        RefreshList();
    }

    /// <summary>删除片段（片段库内右键菜单，带不可恢复确认）。</summary>
    [RelayCommand]
    private void DeleteSnippet(SnippetEntry entry)
    {
        if (!ConfirmDialog.Show(this, "删除片段",
                entry.IsSecret ? "确定要删除该隐藏片段吗？此操作不可撤销。"
                               : $"确定要删除片段「{entry.Title}」吗？此操作不可撤销。", "删除"))
            return;
        _snippets.Remove(entry.Model.Id);
    }

    /// <summary>Esc 关闭窗口。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
