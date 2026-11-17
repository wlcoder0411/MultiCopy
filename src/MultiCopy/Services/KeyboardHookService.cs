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
    private readonly ImageEncodeCache _imageCache;
    private readonly AppState _state = AppState.Instance;
    private IntPtr _hook = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc; // 必须保持委托引用，防止 GC 回收导致钩子失效

    internal KeyboardHookService(ClipboardQueue queue, ClipboardService clipboard, ImageEncodeCache imageCache)
    {
        _queue = queue;
        _clipboard = clipboard;
        _imageCache = imageCache;
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
                            // 追加前后缀内容和序号（通过 AppState.BuildPasteText 统一拼接，图片项不追加）
                            string textToPaste = _state.BuildPasteText(textItem.Text);
                            // 缩窄锁等待预算：本回调运行在低级键盘钩子里，整体耗时受
                            // LowLevelHooksTimeout（默认 300ms）约束，超时会导致系统输入卡顿
                            // 甚至钩子被摘除。150ms 远超正常争用耗时，又给最坏情况留出余量。
                            writeOk = _clipboard.SetClipboardTextSync(textToPaste, 150);
                            // 记录粘贴文本：目标应用可能把刚粘贴的内容写回剪贴板
                            // （资源管理器重命名框），监听器据此识别回音不采集
                            if (writeOk) _state.RecordPastedText(textToPaste);
                            break;
                        case ImageClipboardItem imgItem:
                            // 图片路径绝不能在钩子回调里做 O(W×H) 编码：LowLevelHooksTimeout 默认
                            // 300ms，超时会拖慢全系统输入并可能被系统静默摘钩（此后 Ctrl+V 失效）。
                            // 正常情况队首图片已在入队/出队时完成后台预编码，此处只剩 memcpy 级写入。
                            writeOk = WriteImageForHook(imgItem);
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

    /// <summary>
    /// 在钩子回调的时间预算内把图片写入剪贴板。
    ///
    /// 分层策略（按代价从低到高）：
    /// 1. 缓存命中（正常路径）：PNG 与 CF_DIB 都已算好，写入只剩两次 GlobalAlloc + memcpy。
    /// 2. 缓存未命中（刚采集就立刻 Ctrl+V，窗口约 1 秒）：只同步生成 CF_DIB——纯内存拷贝
    ///    （实测 4096² 约 40ms），PNG 压缩才是耗时大头（约 160ms）；同时排队后台补全缓存。
    ///    DIB 已被 Office / 画图 / 资源管理器 / 浏览器等绝大多数目标支持，降级影响可控。
    /// 3. 编码失败：返回 false，由调用方清空剪贴板并**不出队**（条目保留可重试）。
    /// </summary>
    private bool WriteImageForHook(ImageClipboardItem item)
    {
        if (_imageCache.TryGet(item, out var encoded))
            return _clipboard.SetClipboardImageEncodedSync(encoded, 150);

        _imageCache.Prefetch(item); // 后台补全，下次粘贴走缓存
        var dib = ImageEncoding.EncodeDib(item.Image);
        if (dib == null) return false;
        return _clipboard.SetClipboardImageEncodedSync(new EncodedImage { Dib = dib }, 150);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
