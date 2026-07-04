using System.Text;
using System.Windows.Input;
using MultiCopy.Native;

namespace MultiCopy.Infrastructure;

/// <summary>快捷键显示文本格式化（供设置对话框、托盘菜单使用）。</summary>
public static class HotkeyFormatter
{
    /// <summary>格式化为 "Alt+Z" / "Ctrl+Shift+M" 等显示文本。</summary>
    public static string Format(HotkeyModifierKeys mods, int vk)
    {
        var sb = new StringBuilder();
        if (mods.HasFlag(HotkeyModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(HotkeyModifierKeys.Alt)) sb.Append("Alt+");
        if (mods.HasFlag(HotkeyModifierKeys.Shift)) sb.Append("Shift+");
        if (mods.HasFlag(HotkeyModifierKeys.Win)) sb.Append("Win+");
        sb.Append(KeyName(vk));
        return sb.ToString();
    }

    /// <summary>虚拟键码转键名（用 WPF KeyInterop 反查）。</summary>
    private static string KeyName(int vk)
    {
        try { return KeyInterop.KeyFromVirtualKey(vk).ToString(); }
        catch { return $"VK_{vk:X2}"; }
    }
}
