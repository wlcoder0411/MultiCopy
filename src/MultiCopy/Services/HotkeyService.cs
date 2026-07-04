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
    private const int HotkeyId = 0x9000; // RegisterHotKey 的热键 id（单热键用固定值即可）

    private readonly MainWindow _window;
    private SettingsStorageService.SettingsDto _settings;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _registered;

    /// <summary>热键设置变化（设置对话框应用后触发），供 TrayIconService 刷新菜单文本。</summary>
    public event EventHandler<SettingsStorageService.SettingsDto>? SettingsChanged;

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
    }

    public void Stop()
    {
        Unregister();
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

        _settings = new SettingsStorageService.SettingsDto
        {
            HotkeyEnabled = enabled,
            Modifiers = modifiers,
            Key = key,
        };

        if (enabled)
        {
            if (!TryRegister(modifiers, key))
            {
                // 注册失败：保持 HotkeyEnabled=false 状态（已 Unregister），
                // 保存为禁用态避免每次启动反复失败。调用方应提示用户换一组。
                _settings.HotkeyEnabled = false;
                SettingsStorageService.Save(_settings);
                SettingsChanged?.Invoke(this, _settings);
                return (false, "快捷键注册失败，可能已被其他程序占用，请换一组组合试试。");
            }
        }

        SettingsStorageService.Save(_settings);
        SettingsChanged?.Invoke(this, _settings);
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Constants.WM_HOTKEY && wParam == (IntPtr)HotkeyId)
        {
            OnHotkeyTriggered();
            handled = true;
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
