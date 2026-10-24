using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private MenuItem? _miTopmost;    // 窗口置顶菜单项
    private DispatcherTimer? _autoStartRecheckTimer; // 自启项写入后的延迟复核（部分守护程序会异步清除）

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
        // 延迟复核用的定时器：自启项写入成功后稍后再回读一次，捕获被守护程序异步清除的情况
        _autoStartRecheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoStartRecheckTimer.Tick += OnAutoStartRecheckTick;
        _tray.ContextMenu = BuildMenu();

        _state.ModeChanged += OnModeChanged;
        _state.MonitoringChanged += OnMonitoringChanged;
        _state.QueueFullNotification += OnQueueFull;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        // 托盘菜单不在主窗口逻辑树内，显式应用 Application 级别的隐式样式，避免主题丢失
        if (Application.Current != null)
        {
            menu.Style = (Style)Application.Current.FindResource(typeof(ContextMenu));
        }

        var miShow = new MenuItem { Header = "显示/隐藏窗口(_S)" };
        miShow.Click += (_, _) => ToggleWindowVisibility();
        menu.Items.Add(miShow);

        // 窗口置顶开关（状态持久化由 MainWindow.SetTopmost 负责）
        _miTopmost = new MenuItem { Header = "窗口置顶(_P)", IsCheckable = true, IsChecked = _window.Topmost };
        _miTopmost.Click += (_, _) => _window.SetTopmost(_miTopmost.IsChecked);
        menu.Items.Add(_miTopmost);

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
        _miAutoStart.Click += (_, _) => ToggleAutoStart();
        menu.Items.Add(_miAutoStart);

        menu.Items.Add(new Separator());

        // 设置（简洁入口，具体配置在对话框中查看）
        _miSettings = new MenuItem { Header = "设置(_T)" };
        _miSettings.Click += (_, _) => OpenSettings();
        menu.Items.Add(_miSettings);

        // 使用说明：解释全局快捷键与窗口内快捷键的生效前提
        var miHelp = new MenuItem { Header = "使用说明(_H)" };
        miHelp.Click += (_, _) => HelpDialog.Open(_window.IsVisible ? _window : null);
        menu.Items.Add(miHelp);

        menu.Items.Add(new Separator());

        var miExit = new MenuItem { Header = "退出(_X)" };
        miExit.Click += (_, _) =>
        {
            if (Application.Current is App app)
            {
                app.ForceExit = true;
                app.Shutdown();
            }
        };
        menu.Items.Add(miExit);

        return menu;
    }

    private void OpenSettings()
    {
        // 主窗口隐藏时对话框用 CenterScreen 定位
        SettingsDialog.Show(_window.IsVisible ? _window : null, _hotkey);
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

    /// <summary>上次"粘贴目标未就绪"气泡的时间（Tick64），用于 5 秒节流防止连点刷屏。</summary>
    private long _lastPasteBlockedTipTicks;

    /// <summary>
    /// 点选粘贴被阻止（前台仍是 MultiCopy 自身，Ctrl+V 无法送达目标）时的气泡提示。
    /// 5 秒节流：连续点击不重复弹。
    /// </summary>
    public void ShowPasteBlockedTip()
    {
        long now = Environment.TickCount64;
        if (now - _lastPasteBlockedTipTicks < 5000) return;
        _lastPasteBlockedTipTicks = now;
        _tray?.ShowBalloonTip("MultiCopy", "粘贴目标未就绪：请先点击目标窗口定位光标，再点选内容", BalloonIcon.Warning);
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
        {
            _window.Hide();
        }
        else
        {
            // 复用快捷键的显示逻辑：移除 WS_EX_NOACTIVATE 并聚焦搜索框，
            // 否则从托盘调出的窗口无法接收键盘输入（方向键、搜索都会失效）。
            _window.BringUpAndFocusSearch();
        }
    }

    // ---------- 开机自启 ----------

    /// <summary>
    /// 切换开机自启：写入后回读校验，以实际结果为准同步菜单勾选态，失败时给出明确提示。
    /// 不做校验的话，注册表写入被系统/安全软件静默拦截时会静默失败——用户以为已开启，
    /// 实际每次开机都不会启动（本地实测：名为 MultiCopy 的自启项会被第三方守护程序清除）。
    /// </summary>
    private void ToggleAutoStart()
    {
        if (_miAutoStart == null) return;

        bool want = _miAutoStart.IsChecked;
        bool applied = SetAutoStart(want);

        // 以实际结果同步 UI，避免"勾选了却没生效"的假象
        _autoStart = applied ? want : !want;
        _miAutoStart.IsChecked = _autoStart;

        if (!applied)
        {
            ShowBalloon("开机自启设置失败",
                "自启项写入注册表后被系统或其他软件清除。请在「联想电脑管家 / 支付宝安全组件」等工具中允许 MultiCopy 开机自启。",
                BalloonIcon.Warning);
        }
        else if (want)
        {
            // 写入成功；部分守护程序会在稍后异步清除，延迟复核一次
            _autoStartRecheckTimer?.Stop();
            _autoStartRecheckTimer?.Start();
        }
    }

    /// <summary>延迟复核：确认刚写入的自启项是否仍在（被守护程序异步清除时提示用户）。</summary>
    private void OnAutoStartRecheckTick(object? sender, EventArgs e)
    {
        _autoStartRecheckTimer?.Stop();
        // 期间用户已把自启关掉时，项不存在是正常的，不误报
        if (!_autoStart) return;
        if (IsAutoStartEntryPresent()) return;

        _autoStart = false;
        if (_miAutoStart != null) _miAutoStart.IsChecked = false;
        ShowBalloon("开机自启已被清除",
            "自启项写入后又被系统或其他软件清除（如联想电脑管家 / 支付宝安全组件）。请在对应工具中允许 MultiCopy 开机自启。",
            BalloonIcon.Warning);
    }

    /// <summary>显示气泡提示（标题 + 正文 + 图标）。</summary>
    private void ShowBalloon(string title, string text, BalloonIcon icon)
        => _tray?.ShowBalloonTip(title, text, icon);

    /// <summary>读取当前是否已存在开机自启项；并把旧格式值（缺 --minimized）补全为新格式。</summary>
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

    /// <summary>
    /// 写入/删除开机自启注册表项，并在操作后回读校验。
    /// 返回是否达到期望状态（true=写入后确实存在 / 删除后确实不存在）。
    /// </summary>
    private static bool SetAutoStart(bool enable)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
            {
                if (key == null) return false;
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
            // 回读校验：注册表写入可能被安全软件静默拦截/回滚，不校验就会静默失败
            return IsAutoStartEntryPresent() == enable;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>回读注册表，判断开机自启项当前是否存在。</summary>
    private static bool IsAutoStartEntryPresent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) is string;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _state.ModeChanged -= OnModeChanged;
        _state.MonitoringChanged -= OnMonitoringChanged;
        _state.QueueFullNotification -= OnQueueFull;
        if (_autoStartRecheckTimer != null)
        {
            _autoStartRecheckTimer.Stop();
            _autoStartRecheckTimer.Tick -= OnAutoStartRecheckTick;
            _autoStartRecheckTimer = null;
        }
        _tray?.Dispose();
    }
}
