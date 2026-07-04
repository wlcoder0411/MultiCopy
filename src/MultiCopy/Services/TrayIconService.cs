using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using MultiCopy.Infrastructure;
using MultiCopy.Models;
using MultiCopy.Native;
using MultiCopy.State;
using MultiCopy.Views;

namespace MultiCopy.Services;

/// <summary>
/// 系统托盘图标与右键菜单。图标颜色随队列粘贴模式变化（开=绿/关=灰）。
/// 含开机自启（HKCU Run 项，用户级无需管理员）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly AppState _state = AppState.Instance;
    private readonly ClipboardQueue _queue;
    private readonly HotkeyService _hotkey;
    private TaskbarIcon? _tray;
    private bool _autoStart;
    private MenuItem? _miMode;       // 字段引用，避免硬编码索引
    private MenuItem? _miMonitor;    // 监控菜单项
    private MenuItem? _miAutoStart;  // 开机自启菜单项
    private MenuItem? _miSettings;   // 设置菜单项

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "MultiCopy";

    public TrayIconService(MainWindow window, ClipboardQueue queue, HotkeyService hotkey)
    {
        _window = window;
        _queue = queue;
        _hotkey = hotkey;
    }

    public void Start()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "MultiCopy — 多段复制队列",
            IconSource = IconFactory.CreateTrayIcon(_state.ModeOn),
        };
        _tray.TrayMouseDoubleClick += (_, _) => ToggleWindowVisibility();
        // 先读注册表再建菜单：BuildMenu 中 miAutoStart.IsChecked 依赖 _autoStart 字段
        _autoStart = ReadAutoStart();
        _tray.ContextMenu = BuildMenu();

        _state.ModeChanged += OnModeChanged;
        _state.MonitoringChanged += OnMonitoringChanged;
        _state.QueueFullNotification += OnQueueFull;
        _hotkey.SettingsChanged += OnHotkeySettingsChanged;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var miShow = new MenuItem { Header = "显示/隐藏窗口(_S)" };
        miShow.Click += (_, _) => ToggleWindowVisibility();
        menu.Items.Add(miShow);

        menu.Items.Add(new Separator());

        _miMode = new MenuItem { Header = "切换队列粘贴模式(_M)", IsCheckable = true, IsChecked = _state.ModeOn };
        _miMode.Click += (_, _) => _state.ModeOn = _miMode.IsChecked;
        menu.Items.Add(_miMode);

        _miMonitor = new MenuItem { Header = "监控剪贴板(_N)", IsCheckable = true, IsChecked = _state.IsMonitoring };
        _miMonitor.Click += (_, _) => _state.IsMonitoring = _miMonitor.IsChecked;
        menu.Items.Add(_miMonitor);

        var miClearNormal = new MenuItem { Header = "清空普通队列(_C)" };
        miClearNormal.Click += (_, _) => _queue.ClearNormal();
        menu.Items.Add(miClearNormal);

        menu.Items.Add(new Separator());

        _miAutoStart = new MenuItem { Header = "开机自启(_A)", IsCheckable = true, IsChecked = _autoStart };
        _miAutoStart.Click += (_, _) =>
        {
            _autoStart = _miAutoStart.IsChecked;
            SetAutoStart(_autoStart);
        };
        menu.Items.Add(_miAutoStart);

        menu.Items.Add(new Separator());

        // 设置（显示当前快捷键）
        _miSettings = new MenuItem { Header = FormatSettingsMenuText() };
        _miSettings.Click += (_, _) => OpenSettings();
        menu.Items.Add(_miSettings);

        menu.Items.Add(new Separator());

        var miExit = new MenuItem { Header = "退出(_X)" };
        miExit.Click += (_, _) =>
        {
            if (Application.Current is App app) app.ForceExit = true;
            Application.Current.Shutdown();
        };
        menu.Items.Add(miExit);

        return menu;
    }

    /// <summary>格式化设置菜单文本（含当前快捷键）。</summary>
    private string FormatSettingsMenuText()
    {
        var s = _hotkey.CurrentSettings;
        if (!s.HotkeyEnabled) return "设置(_T)";
        return $"设置(_T)  {HotkeyFormatter.Format((HotkeyModifierKeys)s.Modifiers, s.Key)}";
    }

    private void OpenSettings()
    {
        // 主窗口隐藏时对话框用 CenterScreen 定位
        SettingsDialog.Show(_window.IsVisible ? _window : null, _hotkey);
        // 对话框关闭后刷新菜单文本（快捷键可能已改）
        if (_miSettings != null) _miSettings.Header = FormatSettingsMenuText();
    }

    private void OnHotkeySettingsChanged(object? sender, SettingsStorageService.SettingsDto s)
    {
        if (_miSettings != null) _miSettings.Header = FormatSettingsMenuText();
    }

    private void OnModeChanged(object? sender, bool modeOn)
    {
        if (_tray == null) return;
        _tray.IconSource = IconFactory.CreateTrayIcon(modeOn);
        UpdateTrayToolTip();
        if (_miMode != null) _miMode.IsChecked = modeOn;
    }

    private void OnMonitoringChanged(object? sender, bool monitoring)
    {
        if (_tray == null) return;
        UpdateTrayToolTip();
        if (_miMonitor != null) _miMonitor.IsChecked = monitoring;
    }

    /// <summary>队列已满：气泡提示用户清理。</summary>
    private void OnQueueFull(object? sender, EventArgs e)
    {
        _tray?.ShowBalloonTip("MultiCopy", "队列已满，请先清理后再复制", BalloonIcon.Info);
    }

    /// <summary>根据模式和监控状态更新托盘 Tooltip。</summary>
    private void UpdateTrayToolTip()
    {
        if (_tray == null) return;
        if (!_state.IsMonitoring)
        {
            _tray.ToolTipText = "MultiCopy — 监控已关闭（静默待机）";
        }
        else
        {
            _tray.ToolTipText = _state.ModeOn
                ? "MultiCopy — 队列粘贴模式：开"
                : "MultiCopy — 队列粘贴模式：关";
        }
    }

    private void ToggleWindowVisibility()
    {
        if (_window.IsVisible)
            _window.Hide();
        else
        {
            _window.Show();
            _window.Activate();
        }
    }

    // ---------- 开机自启 ----------
    private static bool ReadAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(AppName) is not string value) return false;
            // 自我修复：旧版注册表值缺少 --minimized 参数（开机自启会弹主窗口），
            // 检测到此情况自动补全为新格式，免去用户手动取消再重新勾选的操作。
            if (!value.Contains("--minimized", StringComparison.OrdinalIgnoreCase))
            {
                SetAutoStart(true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return;
            if (enable)
            {
                string exePath = Environment.ProcessPath
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, AppName + ".exe");
                key.SetValue(AppName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // 权限或注册表异常时静默
        }
    }

    public void Dispose()
    {
        _state.ModeChanged -= OnModeChanged;
        _state.MonitoringChanged -= OnMonitoringChanged;
        _state.QueueFullNotification -= OnQueueFull;
        _hotkey.SettingsChanged -= OnHotkeySettingsChanged;
        _tray?.Dispose();
    }
}
