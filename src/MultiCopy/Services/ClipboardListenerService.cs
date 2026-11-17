using System.Diagnostics;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MultiCopy.Models;
using MultiCopy.Native;
using MultiCopy.State;

namespace MultiCopy.Services;

/// <summary>一次采集的结果状态。</summary>
internal enum CaptureStatus
{
    /// <summary>已作为新条目入队。</summary>
    Captured,
    /// <summary>目标集合（或置顶）中已存在相同内容，Item 为已有条目。</summary>
    AlreadyInQueue,
    /// <summary>被回音抑制 / 800ms 短窗口去重拦下（仅监听路径会出现）。</summary>
    Suppressed,
    /// <summary>队列已满（管线内已触发「队列已满」气泡）。</summary>
    QueueFull,
    /// <summary>图片超过单边像素上限。</summary>
    TooLarge,
    /// <summary>剪贴板没有可采的内容。</summary>
    Empty,
}

/// <summary>采集结果：状态 + 新入队或命中的已有条目（供 UI 定位反馈）。</summary>
internal readonly record struct CaptureOutcome(CaptureStatus Status, ClipboardItem? Item = null);

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
                CaptureText(text, GetForegroundAppTitle(), isAdoption: false);
            }
            else
            {
                // 图片采集分支：文本为空时尝试读取剪贴板图片
                var img = _clipboard.GetClipboardImage();
                if (img != null)
                {
                    string sourceApp = GetForegroundAppTitle();
                    if (string.IsNullOrEmpty(sourceApp))
                        sourceApp = $"截图 {DateTime.Now:yyyy-MM-dd HH:mm}";
                    CaptureImage(img.Value.image, img.Value.width, img.Value.height, sourceApp, isAdoption: false);
                }
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- 共享采集管线（监听路径与「收编」路径的唯一入口） ----------
    //
    // 两条路径必须语义单一来源：容量上限、目标集合去重、去重指纹、入队目标全在这里。
    // 去重指纹（_lastEnqueued*）两路共享更新——否则收编的内容会被监听器当成新内容
    // 重复采集一遍。差异仅在判定条件：回音抑制与 800ms 窗口只作用于监听路径。

    /// <summary>
    /// 采集一段文本。回音抑制与 800ms 去重窗口仅作用于监听路径——
    /// 收编是用户显式动作，刚出队的内容要能一键找回，抑制它反而违背意图。
    /// </summary>
    private CaptureOutcome CaptureText(string text, string sourceApp, bool isAdoption)
    {
        // 与回音记录共用同一截断标准
        if (text.Length > AppState.MaxItemLength)
            text = text.Substring(0, AppState.MaxItemLength);

        // 回音抑制：某些目标应用（典型：资源管理器新建文件夹的重命名编辑框）会在粘贴后
        // 把刚粘贴的内容写回剪贴板，触发新的 WM_CLIPBOARDUPDATE。此时自身写入的防回环
        // 标志已被消费，若不加抑制，刚出队的内容会被当作「新复制」重新入队到队尾。
        // 判定：文本与最近一次队列粘贴写入的内容相同，且在回音窗口内。
        if (!isAdoption && _state.IsPasteEcho(text))
            return new CaptureOutcome(CaptureStatus.Suppressed);

        // 目标集合中已存在相同文本（Word 等应用在文档变更后可能重复广播同一剪贴板内容）：
        // 监听路径据此不重复入队；收编路径定位已有条目并顺带查置顶（收编幂等——
        // 内容已存在时就地带用户去看它，而不是再排一份重复项）。
        var existing = TryFindQueuedText(text, includePinned: isAdoption);
        if (existing != null)
            return new CaptureOutcome(CaptureStatus.AlreadyInQueue, existing);

        // 重复抑制：短窗口内同一文本（应用一次复制触发多次 WM_CLIPBOARDUPDATE）。
        // 收编路径不需要：已在集合的情形上一条已覆盖，刚出队的内容本就应允许找回。
        if (!isAdoption
            && _lastEnqueuedText == text
            && _lastEnqueuedTicks > 0
            && (Environment.TickCount64 - _lastEnqueuedTicks) < DedupWindowMs)
            return new CaptureOutcome(CaptureStatus.Suppressed);

        // 先更新去重指纹，再判断容量：队列已满时也会提前返回，
        // 若不先记录本次文本，同一文本被应用重复触发 WM_CLIPBOARDUPDATE 时
        // 会因 isDup 恒假而反复弹出「队列已满」气泡。
        _lastEnqueuedText = text;
        _lastEnqueuedTicks = Environment.TickCount64;

        // 容量检查（与图片分支保持一致）：普通项达到上限后不再采集，气泡提示用户清理。
        if (_queue.NormalCount >= _state.MaxQueueItems)
        {
            _state.NotifyQueueFull();
            return new CaptureOutcome(CaptureStatus.QueueFull);
        }

        var item = new TextClipboardItem(text, sourceApp);
        _queue.Enqueue(item, _state.ActiveGroupId);
        return new CaptureOutcome(CaptureStatus.Captured, item);
    }

    /// <summary>
    /// 采集一张图片。监听路径按「尺寸+哈希、1500ms 窗口」去重（截图工具可能对同一张图
    /// 多次触发）；收编路径按目标集合+置顶的哈希全量幂等。指纹两路共享更新，
    /// 理由同 <see cref="CaptureText"/>。
    /// </summary>
    private CaptureOutcome CaptureImage(BitmapSource image, int width, int height, string sourceApp, bool isAdoption)
    {
        // 超大图片拒绝（防拖慢队列与 UI）
        if (width > MaxImageDimension || height > MaxImageDimension)
            return new CaptureOutcome(CaptureStatus.TooLarge);

        // 先构造项（构造时一次性计算哈希），再判断去重，避免重复算哈希
        var item = new ImageClipboardItem(image, sourceApp);

        if (isAdoption)
        {
            var existing = TryFindQueuedImage(item, includePinned: true);
            if (existing != null)
                return new CaptureOutcome(CaptureStatus.AlreadyInQueue, existing);
        }
        else if (IsDuplicateImage(item))
        {
            return new CaptureOutcome(CaptureStatus.Suppressed);
        }

        // 先更新去重指纹，再判断容量（与文本路径同理：满员时同一图片被应用
        // 重复触发 WM_CLIPBOARDUPDATE 不反复弹「队列已满」气泡）。
        UpdateLastEnqueuedImage(item);

        if (_queue.NormalCount >= _state.MaxQueueItems)
        {
            _state.NotifyQueueFull();
            return new CaptureOutcome(CaptureStatus.QueueFull);
        }

        _queue.Enqueue(item, _state.ActiveGroupId);
        return new CaptureOutcome(CaptureStatus.Captured, item);
    }

    // ---------- 收编（MC 未运行 / 监控关闭期间的剪贴板遗留内容） ----------

    /// <summary>
    /// 收编剪贴板当前内容到队列。MC 未运行或监控关闭期间复制的内容不会产生
    /// WM_CLIPBOARDUPDATE（事件驱动的天然盲区），用户通过工具栏 / ⋯ 菜单 / 托盘菜单 /
    /// 主窗前台 Ctrl+V 显式触发，把「剪贴板当前状态」走一遍与监听完全相同的采集管线。
    /// 只读不写剪贴板，不涉及防回环标志与粘贴回音记录。
    /// </summary>
    internal CaptureOutcome AdoptCurrentClipboard()
    {
        // 与监听路径一致的格式优先级：先文本，后图片
        string? text = _clipboard.GetClipboardText();
        if (!string.IsNullOrEmpty(text))
            return CaptureText(text, GetClipboardOwnerTitle(), isAdoption: true);

        var img = _clipboard.GetClipboardImage();
        if (img != null)
            return CaptureImage(img.Value.image, img.Value.width, img.Value.height,
                GetClipboardOwnerTitle(), isAdoption: true);

        return new CaptureOutcome(CaptureStatus.Empty);
    }

    /// <summary>
    /// 收编来源归属：剪贴板属主窗口还在时取其进程名（如 chrome / WeChat）；
    /// 属主已退出（复制它的应用已关闭）或查询失败时诚实标「剪贴板遗留」。
    /// 不取当前前台窗口标题——收编时刻的前台是 MultiCopy 自己，标上去是错的。
    /// </summary>
    private string GetClipboardOwnerTitle()
    {
        try
        {
            IntPtr owner = Win32.GetClipboardOwner();
            if (owner != IntPtr.Zero)
            {
                Win32.GetWindowThreadProcessId(owner, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
        }
        catch
        {
            // 属主进程已退出 / 权限不足：落到下面的兜底文案
        }
        return "剪贴板遗留";
    }

    /// <summary>
    /// 目标集合（活动分组或未分组）中查找相同文本；includePinned 时连同置顶一起找。
    /// 比较时统一换行并去掉末尾空白，避免 Word 重新广播时行尾差异导致漏判。
    /// </summary>
    private TextClipboardItem? TryFindQueuedText(string text, bool includePinned)
    {
        string normalized = NormalizeForDedup(text);
        foreach (var item in EnumerateScanTargets(includePinned))
        {
            if (item is TextClipboardItem t && NormalizeForDedup(t.Text) == normalized)
                return t;
        }
        return null;
    }

    /// <summary>按内容哈希查找相同图片（收编幂等用）；includePinned 时连同置顶一起找。</summary>
    private ImageClipboardItem? TryFindQueuedImage(ImageClipboardItem item, bool includePinned)
    {
        foreach (var it in EnumerateScanTargets(includePinned))
        {
            if (it is ImageClipboardItem img && img.ImageHash == item.ImageHash)
                return img;
        }
        return null;
    }

    /// <summary>采集去重的扫描范围：目标集合（活动分组或未分组），可选叠加置顶。</summary>
    private IEnumerable<ClipboardItem> EnumerateScanTargets(bool includePinned)
    {
        if (includePinned)
        {
            foreach (var it in _queue.PinnedItems)
                yield return it;
        }
        var target = _queue.FindGroup(_state.ActiveGroupId)?.Items ?? _queue.UngroupedItems;
        foreach (var it in target)
            yield return it;
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
