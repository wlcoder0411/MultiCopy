namespace MultiCopy.Native;

/// <summary>
/// 全局热键修饰键（位标志）。数值直接对齐 Win32 MOD_* 常量，
/// 可直接 (uint) 转换后传给 RegisterHotKey，无需映射。
/// </summary>
[Flags]
public enum HotkeyModifierKeys : uint
{
    None    = 0x0000,
    Alt     = 0x0001,   // MOD_ALT
    Control = 0x0002,   // MOD_CONTROL
    Shift   = 0x0004,   // MOD_SHIFT
    Win     = 0x0008,   // MOD_WIN
}
