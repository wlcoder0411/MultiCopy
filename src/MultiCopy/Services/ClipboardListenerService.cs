using System.Diagnostics;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MultiCopy.Models;
using MultiCopy.Native;
using MultiCopy.State;

namespace MultiCopy.Services;

/// <summary>
/// 监听系统剪贴板变化（AddClipboardFormatListener + WM_CLIPBOARDUPDATE），
/// 把用户新复制的纯文本入队。忽略自身主动写入（防回环）与短时重复写入。
/// </summary>
public sealed class ClipboardListenerService
{
    private readonly ClipboardQueue _queue;
    private readonly ClipboardService _clipboard;
    private readonly AppState _state = AppState.Instance;
    private IntPtr _hwnd;

    // 重复写入抑制：同一文本在 500ms 内只入队一次（某些应用一次 Ctrl+C 触发多次 WM_CLIPBOARDUPDATE）
    private string? _lastEnqueuedText;
    private long _lastEnqueuedTicks;

    // 图片去重：尺寸 + 哈希双校验，窗口 1500ms（截图工具可能多次触发）
    private int _lastEnqueuedImgW;
    private int _lastEnqueuedImgH;
    private string? _lastEnqueuedImgHash;
    private long _lastEnqueuedImgTicks;

    // 复用 StringBuilder 抓取前台窗口标题，避免每次剪贴板变化都分配
    private readonly StringBuilder _titleBuffer = new(256);

    private const int DedupWindowMs = 500;
    private const int ImageDedupWindowMs = 1500;
    private const int MaxItemLength = 100_000; // 单条上限，防超大文本拖慢
    private const int MaxImageDimension = 4096; // 单边像素上限，防超大图片拖慢

    public ClipboardListenerService(ClipboardQueue queue, ClipboardService clipboard)
    {
        _queue = queue;
        _clipboard = clipboard;
    }

    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Win32.AddClipboardFormatListener(hwnd);
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    public void Stop()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.RemoveClipboardFormatListener(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Constants.WM_CLIPBOARDUPDATE)
        {
            // 防回环：我们自己的写入，忽略并清位
            if (_state.IsWritingClipboard)
            {
                _state.IsWritingClipboard = false;
                handled = true;
                return IntPtr.Zero;
            }

            // 监控关闭：不采集剪贴板，直接放行（不影响系统和其他监听者）
            if (!_state.IsMonitoring)
            {
                handled = true;
                return IntPtr.Zero;
            }

            string? text = _clipboard.GetClipboardText();
            if (!string.IsNullOrEmpty(text))
            {
                if (text.Length > MaxItemLength)
                    text = text.Substring(0, MaxItemLength);

                bool isDup = _lastEnqueuedText == text
                             && _lastEnqueuedTicks > 0
                             && (Environment.TickCount64 - _lastEnqueuedTicks) < DedupWindowMs;

                if (!isDup)
                {
                    _lastEnqueuedText = text;
                    _lastEnqueuedTicks = Environment.TickCount64;
                    string sourceApp = GetForegroundAppTitle();
                    _queue.Enqueue(new TextClipboardItem(text, sourceApp), _state.ActiveGroupId);
                }
            }
            else
            {
                // 图片采集分支：文本为空时尝试读取剪贴板图片
                var img = _clipboard.GetClipboardImage();
                if (img != null)
                {
                    // 超大图片拒绝（防拖慢队列与 UI）
                    if (img.Value.width > MaxImageDimension || img.Value.height > MaxImageDimension)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }

                    // 容量检查：超出上限不再采集，气泡提示用户清理
                    if (_queue.NormalCount >= AppState.MaxQueueItems)
                    {
                        _state.NotifyQueueFull();
                        handled = true;
                        return IntPtr.Zero;
                    }

                    // 先构造项（构造时一次性计算哈希），再判断去重，避免重复算哈希
                    string sourceApp = GetForegroundAppTitle();
                    if (string.IsNullOrEmpty(sourceApp))
                        sourceApp = $"截图 {DateTime.Now:yyyy-MM-dd HH:mm}";
                    var item = new ImageClipboardItem(img.Value.image, sourceApp);

                    // 去重：尺寸 + 哈希双校验，窗口 1500ms
                    if (IsDuplicateImage(item))
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }

                    _queue.Enqueue(item, _state.ActiveGroupId);
                    UpdateLastEnqueuedImage(item);
                }
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 图片去重：尺寸 + 哈希双校验，1500ms 窗口内判定为重复。
    /// 调用前需已构造 ImageClipboardItem（哈希在构造时计算）。
    /// </summary>
    private bool IsDuplicateImage(ImageClipboardItem item)
    {
        // 快速尺寸比对
        if (_lastEnqueuedImgW != item.Width || _lastEnqueuedImgH != item.Height) return false;
        // 时间窗口
        if (_lastEnqueuedImgTicks == 0
            || (Environment.TickCount64 - _lastEnqueuedImgTicks) >= ImageDedupWindowMs) return false;
        // 完整哈希校验
        if (_lastEnqueuedImgHash != item.ImageHash) return false;
        return true;
    }

    /// <summary>更新最近入队图片指纹（尺寸 + 哈希 + 时间戳）。</summary>
    private void UpdateLastEnqueuedImage(ImageClipboardItem item)
    {
        _lastEnqueuedImgW = item.Width;
        _lastEnqueuedImgH = item.Height;
        _lastEnqueuedImgHash = item.ImageHash;
        _lastEnqueuedImgTicks = Environment.TickCount64;
    }

    private string GetForegroundAppTitle()
    {
        try
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return string.Empty;
            int len = Win32.GetWindowTextLength(fg);
            if (len <= 0) return string.Empty;
            if (_titleBuffer.Capacity < len + 1) _titleBuffer.Capacity = len + 1;
            _titleBuffer.Clear();
            Win32.GetWindowText(fg, _titleBuffer, _titleBuffer.Capacity);
            return _titleBuffer.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
