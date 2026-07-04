namespace MultiCopy.Native;

/// <summary>
/// Win32 常量集中定义。来源：Winuser.h / Win32 API 文档。
/// </summary>
internal static class Constants
{
    // 低级键盘钩子
    public const int WH_KEYBOARD_LL = 13;

    // 窗口消息
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_HOTKEY = 0x0312;

    // 虚拟键码
    public const int VK_V = 0x56;
    public const int VK_Z = 0x5A;
    public const int VK_CONTROL = 0x11;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_SHIFT = 0x10;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;

    // 全局热键修饰键（RegisterHotKey fsModifiers，与 HotkeyModifierKeys 枚举值一致）
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    // 剪贴板格式
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_DIB = 8;
    public const uint CF_DIBV5 = 17;

    // 扩展窗口样式
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    // 全局内存分配标志
    public const uint GMEM_MOVEABLE = 0x0002;

    // SendInput
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// PNG 剪贴板格式的运行时 ID（由 RegisterClipboardFormat("PNG") 返回）。
    /// 首次访问时注册并缓存，避免每次采集重复注册。
    /// </summary>
    internal static uint PngFormatId
    {
        get
        {
            if (_pngFormatId == 0)
                _pngFormatId = Win32.RegisterClipboardFormat("PNG");
            return _pngFormatId;
        }
    }
    private static uint _pngFormatId;
}
