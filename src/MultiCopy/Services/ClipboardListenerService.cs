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

    // 重复写入抑制：同一文本在 800ms 内只入队一次（某些应用一次 Ctrl+C 触发多次 WM_CLIPBOARDUPDATE）
    private string? _lastEnqueuedText;
    private long _lastEnqueuedTicks;

    // 图片去重：尺寸 + 哈希双校验，窗口 1500ms（截图工具可能多次触发）
    private int _lastEnqueuedImgW;
    private int _lastEnqueuedImgH;
    private string? _lastEnqueuedImgHash;
    private long _lastEnqueuedImgTicks;

    // 复用 StringBuilder 抓取前台窗口标题，避免每次剪贴板变化都分配
    private readonly StringBuilder _titleBuffer = new(256);

    private const int DedupWindowMs = 800;
    private const int ImageDedupWindowMs = 1500;
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
                if (text.Length > AppState.MaxItemLength)
                    text = text.Substring(0, AppState.MaxItemLength);

                // 粘贴回音抑制：某些目标应用（典型：资源管理器新建文件夹的重命名
                // 编辑框）会在粘贴后把刚粘贴的内容写回剪贴板，触发新的
                // WM_CLIPBOARDUPDATE。此时自身写入的防回环标志已被消费，若不加抑制，
                // 刚出队的内容会被当作"新复制"重新入队到队尾。
                // 判定：文本与最近一次队列粘贴写入的内容相同，且在回音窗口内。
                bool isEcho = _state.IsPasteEcho(text);

                // 重复抑制：
                // 1) 短窗口内同一文本（应用一次复制触发多次 WM_CLIPBOARDUPDATE）
                // 2) 目标集合中已存在相同文本（Word 等应用在文档变更后可能重复广播同一剪贴板内容）
                bool isDup = !isEcho
                             && ((_lastEnqueuedText == text
                                  && _lastEnqueuedTicks > 0
                                  && (Environment.TickCount64 - _lastEnqueuedTicks) < DedupWindowMs)
                                 || IsTextAlreadyQueued(text));

                if (!isEcho && !isDup)
                {
                    // 先更新去重指纹，再判断容量：队列已满时也会提前返回，
                    // 若不先记录本次文本，同一文本被应用重复触发 WM_CLIPBOARDUPDATE 时
                    // 会因 isDup 恒假而反复弹出“队列已满”气泡。
                    _lastEnqueuedText = text;
                    _lastEnqueuedTicks = Environment.TickCount64;

                    // 容量检查（与图片分支保持一致）：普通项达到上限后不再采集，气泡提示用户清理。
                    // 必须显式放在文本分支内——此前文本分支缺失该检查，导致文本队列无界增长，
                    // 与 AppState.MaxQueueItems/README 宣称的“上限 30 条”不符。
                    if (_queue.NormalCount >= AppState.MaxQueueItems)
                    {
                        _state.NotifyQueueFull();
                        handled = true;
                        return IntPtr.Zero;
                    }

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
    /// 目标集合（活动分组或未分组）中是否已存在相同文本。
    /// 比较时统一换行并去掉末尾空白，避免 Word 重新广播时行尾差异导致漏判。
    /// </summary>
    private bool IsTextAlreadyQueued(string text)
    {
        string normalized = NormalizeForDedup(text);
        var target = _queue.FindGroup(_state.ActiveGroupId)?.Items ?? _queue.UngroupedItems;
        foreach (var item in target)
        {
            if (item is TextClipboardItem t && NormalizeForDedup(t.Text) == normalized)
                return true;
        }
        return false;
    }

    private static string NormalizeForDedup(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

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
