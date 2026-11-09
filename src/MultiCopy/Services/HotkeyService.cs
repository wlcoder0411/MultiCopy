using System.Windows.Interop;
using MultiCopy.Native;

namespace MultiCopy.Services;

/// <summary>
/// 全局快捷键服务（RegisterHotKey + WM_HOTKEY）。
/// 收到热键后调出主窗口并聚焦搜索框（复用 MainWindow.BringUpAndFocusSearch）。
/// 与 ClipboardListenerService 共享同一 HwndSource 钩子（各自检查不同 msg，互不干扰）。
/// 热键注册与 AppState.IsMonitoring 无关：监控关闭时热键仍可调出窗口。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x9000;        // 主窗口热键 id
    private const int SnippetHotkeyId = 0x9001; // 片段速取热键 id

    private readonly MainWindow _window;
    private SettingsStorageService.SettingsDto _settings;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _registered;
    private bool _snippetRegistered;

    /// <summary>热键设置变化（设置对话框应用后触发），供 TrayIconService 刷新菜单文本。</summary>
    public event EventHandler<SettingsStorageService.SettingsDto>? SettingsChanged;

    /// <summary>片段速取热键触发（App 订阅 → 打开片段速取窗）。</summary>
    public event EventHandler? SnippetHotkeyTriggered;

    /// <summary>当前生效设置（供设置对话框初始化显示）。</summary>
    public SettingsStorageService.SettingsDto CurrentSettings => _settings;

    public HotkeyService(MainWindow window, SettingsStorageService.SettingsDto settings)
    {
        _window = window;
        _settings = settings;
    }

    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        // 始终挂钩（廉价）；即便初始禁用也挂，便于后续 UpdateSettings 直接注册
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        if (_settings.HotkeyEnabled)
            TryRegister(_settings.Modifiers, _settings.Key);
        if (_settings.SnippetHotkeyEnabled)
            TryRegisterSnippet(_settings.SnippetModifiers, _settings.SnippetKey);
    }

    public void Stop()
    {
        Unregister();
        UnregisterSnippet();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    /// <summary>
    /// 更新热键设置（设置对话框"应用/确定"调用）。
    /// 先反注册旧的，再按新设置注册。返回 (成功, 错误信息)。
    /// 成功后持久化 + 触发 SettingsChanged。
    /// </summary>
    public (bool ok, string? error) UpdateSettings(bool enabled, uint modifiers, int key)
    {
        Unregister();

        // 增量更新：在磁盘上已有的设置基础上只改热键相关字段，其余字段（粘贴前缀/后缀、
        // 序号配置等）原样保留后再落盘。
        // 此前这里 new 了一个只含热键三字段的新 SettingsDto 直接 Save，会以默认值覆盖
        // settings.json，把用户配置的前后缀与序号冲刷掉（尤其注册失败早退时 DoApply 的
        // 再合并不会执行，擦除会永久留在磁盘上）。
        var previous = _settings;
        var current = SettingsStorageService.Load();
        current.HotkeyEnabled = enabled;
        current.Modifiers = modifiers;
        current.Key = key;

        if (enabled && !TryRegister(modifiers, key))
        {
            // 注册失败：回滚而非写盘。Unregister 已把原热键摘掉，这里把它注册回来，
            // 且不改动磁盘设置。否则用户点“取消”后原快捷键会永久失效
            // （Cancel 的还原分支因 _applied* 未变而不会触发），磁盘也会被写成禁用态。
            if (previous.HotkeyEnabled)
                TryRegister(previous.Modifiers, previous.Key);
            return (false, "快捷键注册失败，可能已被其他程序占用，请换一组组合试试。");
        }

        _settings = current;
        SettingsStorageService.Save(current);
        SettingsChanged?.Invoke(this, current);
        return (true, null);
    }

    /// <summary>
    /// 更新片段速取热键（设置对话框"确定"调用）。增量落盘 + 失败回滚的语义与 UpdateSettings 相同。
    /// </summary>
    public (bool ok, string? error) UpdateSnippetHotkey(bool enabled, uint modifiers, int key)
    {
        var previous = _settings;
        var current = SettingsStorageService.Load();
        current.SnippetHotkeyEnabled = enabled;
        current.SnippetModifiers = modifiers;
        current.SnippetKey = key;

        UnregisterSnippet();
        if (enabled && !TryRegisterSnippet(modifiers, key))
        {
            // 注册失败：回滚注册而非写盘（语义同 UpdateSettings）
            if (previous.SnippetHotkeyEnabled)
                TryRegisterSnippet(previous.SnippetModifiers, previous.SnippetKey);
            return (false, "快捷键注册失败，可能已被其他程序占用，请换一组组合试试。");
        }

        _settings = current;
        SettingsStorageService.Save(current);
        SettingsChanged?.Invoke(this, current);
        return (true, null);
    }

    private bool TryRegister(uint modifiers, int key)
    {
        if (_hwnd == IntPtr.Zero) return false;
        if (_registered) Unregister();
        bool ok = Win32.RegisterHotKey(_hwnd, HotkeyId, modifiers, key);
        if (ok)
        {
            _registered = true;
        }
        return ok;
    }

    private void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    private bool TryRegisterSnippet(uint modifiers, int key)
    {
        if (_hwnd == IntPtr.Zero) return false;
        bool ok = Win32.RegisterHotKey(_hwnd, SnippetHotkeyId, modifiers, key);
        _snippetRegistered = ok;
        return ok;
    }

    private void UnregisterSnippet()
    {
        if (!_snippetRegistered) return;
        Win32.UnregisterHotKey(_hwnd, SnippetHotkeyId);
        _snippetRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Constants.WM_HOTKEY)
        {
            if (wParam == (IntPtr)HotkeyId)
            {
                OnHotkeyTriggered();
                handled = true;
            }
            else if (wParam == (IntPtr)SnippetHotkeyId)
            {
                SnippetHotkeyTriggered?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>热键触发：智能 toggle 调出/隐藏主窗口。</summary>
    private void OnHotkeyTriggered()
    {
        // HwndSource 钩子在 UI 线程执行（与 ClipboardListenerService 一致），无需 Dispatcher
        // toggle 三态策略：
        //   隐藏 → 显示 + 聚焦搜索框
        //   显示且是前台窗口 → 隐藏（toggle 关）
        //   显示但非前台（用户切到别的应用）→ 调到前台 + 聚焦搜索框（不隐藏）
        if (_window.IsVisible && Win32.GetForegroundWindow() == _hwnd)
        {
            _window.Hide();
        }
        else
        {
            _window.BringUpAndFocusSearch();
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
