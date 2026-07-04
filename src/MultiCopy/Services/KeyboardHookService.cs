using System.Runtime.InteropServices;
using MultiCopy.Models;
using MultiCopy.Native;
using MultiCopy.State;

namespace MultiCopy.Services;

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）。
/// 队列粘贴模式开启时，拦截 Ctrl+V：把队首写入剪贴板后放行原按键，
/// 目标应用读到的就是队首文本。Shift 按下=逃生口（不拦截，普通粘贴）。
/// 出队范围由 AppState.ActiveGroupId 决定：选中分组=仅该分组；未选中=全局 FIFO。
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    private readonly ClipboardQueue _queue;
    private readonly ClipboardService _clipboard;
    private readonly AppState _state = AppState.Instance;
    private IntPtr _hook = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc; // 必须保持委托引用，防止 GC 回收导致钩子失效

    public KeyboardHookService(ClipboardQueue queue, ClipboardService clipboard)
    {
        _queue = queue;
        _clipboard = clipboard;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback;
        IntPtr hMod = Win32.GetModuleHandle(null);
        _hook = Win32.SetWindowsHookEx(Constants.WH_KEYBOARD_LL, _proc, hMod, 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && (wParam == (IntPtr)Constants.WM_KEYDOWN || wParam == (IntPtr)Constants.WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);
            bool ctrl = (Win32.GetKeyState(Constants.VK_CONTROL) & 0x8000) != 0;
            bool shift = (Win32.GetKeyState(Constants.VK_SHIFT) & 0x8000) != 0;

            // 自己用 SendInput 模拟的 Ctrl+V，放行不拦截（防重入）
            if (_state.IsSimulatingPaste)
            {
                return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (vk == Constants.VK_V && ctrl && _state.ModeOn && _state.IsMonitoring && !shift)
            {
                // 范围由活动分组决定：选中分组=仅该分组出队；未选中=全局 FIFO。
                // Peek 返回 null（活动分组空 或 全队列空）时不写剪贴板，放行原按键
                // → 目标应用粘贴当前剪贴板内容（降级为普通粘贴，不消耗其他分组）。
                var head = _queue.Peek(_state.ActiveGroupId);
                if (head != null)
                {
                    bool writeOk;
                    switch (head)
                    {
                        case TextClipboardItem textItem:
                            writeOk = _clipboard.SetClipboardTextSync(textItem.Text);
                            break;
                        case ImageClipboardItem imgItem:
                            writeOk = _clipboard.SetClipboardImageSync(imgItem.Image);
                            // 图片写失败：清空剪贴板（避免粘出上一条文本）
                            if (!writeOk) _clipboard.ClearClipboard();
                            break;
                        default:
                            writeOk = false;
                            break;
                    }
                    if (writeOk)
                    {
                        // 写剪贴板成功（IsWritingClipboard 已在内部置位）→ 乐观出队
                        _queue.Dequeue(_state.ActiveGroupId);
                    }
                }
                // 无论成功失败都放行原按键：
                //  成功 → 目标应用粘贴队首；失败 → 目标应用粘贴当前剪贴板（文本降级）或空（图片清空）
                return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
