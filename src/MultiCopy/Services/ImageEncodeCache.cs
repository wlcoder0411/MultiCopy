using MultiCopy.Models;

namespace MultiCopy.Services;

/// <summary>
/// 图片预编码缓存：把「写剪贴板所需的 PNG / CF_DIB 字节流」提前在后台线程算好。
///
/// 动机（第一性）：<see cref="ImageEncoding"/> 的编码是 O(W×H) 的纯 CPU 工作，
/// 而 KeyboardHookService 的 Ctrl+V 拦截运行在 WH_KEYBOARD_LL 回调里，受系统
/// LowLevelHooksTimeout（默认 300ms）约束——超时会让全系统输入卡顿，反复超时后
/// 系统会静默摘掉钩子（此后队列粘贴失效且无提示）。图片内容对同一条目是**不可变**的
/// （ImageClipboardItem.Image 已 Freeze），因此编码结果可以算一次、反复复用。
///
/// 键用图片内容哈希（ImageHash）而不是条目 Id：同图必得同字节，天然免疫
/// 「两条记录共用 Id」或 json 被手工改坏时粘出另一张图的风险，顺带让同图去重。
///
/// 容量策略：只需服务队首，超过 3 条或 96MB 即淘汰最旧的，但**永远保留最新一条**——
/// 单张 4096² 图的 PNG+DIB 可达约 130MB，若连它一起淘汰，队首永远命中不了缓存，
/// 每次 Ctrl+V 都要重新编码，等于没修。
///
/// 线程：任意线程可调用；编码在 ThreadPool 上执行，结果只在任务成功完成后发布。
/// </summary>
internal sealed class ImageEncodeCache
{
    /// <summary>缓存条目上限。Ctrl+V 只会消耗队首，缓存少量即可覆盖连续粘贴。</summary>
    private const int MaxEntries = 3;

    /// <summary>缓存总字节上限（典型 1080p 截图约 9MB/条，4096² 单条约 130MB）。</summary>
    private const long MaxTotalBytes = 96L * 1024 * 1024;

    private sealed class Entry
    {
        public required Task<EncodedImage?> Task { get; init; }

        /// <summary>编码成功后的结果；null 表示仍在进行或已失败。</summary>
        public EncodedImage? Value { get; set; }

        public long Bytes { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>插入顺序（最旧在前），用于淘汰。与 _entries 的增删保持同步。</summary>
    private readonly List<string> _order = new();

    private readonly object _gate = new();

    /// <summary>为指定条目启动（或复用）后台编码。传入 null 或已缓存则立即返回。</summary>
    public void Prefetch(ImageClipboardItem? item)
    {
        if (item == null) return;
        _ = GetOrStart(item);
    }

    /// <summary>
    /// 取已完成的编码结果。未命中、仍在编码或已失败均返回 false
    /// （调用方需回退到同步路径或让本次粘贴降级）。
    /// </summary>
    public bool TryGet(ImageClipboardItem? item, out EncodedImage image)
    {
        image = null!;
        if (item == null) return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(KeyOf(item), out var entry)) return false;

            if (entry.Value != null)
            {
                Touch(KeyOf(item));
                image = entry.Value;
                return true;
            }

            // 任务已完成但发布延续尚未执行（时间窗口极小）：直接取任务结果并顺手发布，
            // 避免"编码其实已好、却因发布延迟被判未命中"而降级成只写 DIB。
            if (entry.Task.Status == TaskStatus.RanToCompletion && entry.Task.Result != null)
            {
                entry.Value = entry.Task.Result;
                entry.Bytes = entry.Task.Result.TotalBytes;
                Touch(KeyOf(item));
                image = entry.Value;
                return true;
            }
            return false;
        }
    }

    /// <summary>启动后台编码并登记到缓存；同一条目重复调用返回同一个任务。</summary>
    public Task<EncodedImage?> GetOrStart(ImageClipboardItem item)
    {
        string key = KeyOf(item);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
                return existing.Task;
        }

        // 在锁外启动编码：Task.Run 内部做像素拷贝与 PNG 压缩，绝不占用调用线程
        var task = Task.Run(() =>
        {
            var png = ImageEncoding.EncodePng(item.Image);
            var dib = ImageEncoding.EncodeDib(item.Image);
            if (png == null && dib == null) return (EncodedImage?)null;
            return new EncodedImage { Png = png, Dib = dib };
        });

        lock (_gate)
        {
            // 竞态兜底：并发调用可能已为同一内容登记任务，保留先到者
            if (_entries.TryGetValue(key, out var raced))
                return raced.Task;

            _entries[key] = new Entry { Task = task };
            _order.Add(key);
            Trim();
        }

        _ = task.ContinueWith(finished => Publish(key, task, finished), TaskScheduler.Default);
        return task;
    }

    /// <summary>任务完成：成功则发布结果，失败则连同登记一起丢弃。</summary>
    private void Publish(string key, Task<EncodedImage?> expected, Task<EncodedImage?> finished)
    {
        if (finished.Status != TaskStatus.RanToCompletion || finished.Result == null)
        {
            Discard(key, expected);
            return;
        }

        lock (_gate)
        {
            // 身份校验：同 key 可能已被新任务取代（例如上次失败后重试），只认自己的任务
            if (!_entries.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Task, expected))
                return;
            entry.Value = finished.Result;
            entry.Bytes = finished.Result.TotalBytes;
            Trim();
        }
    }

    /// <summary>丢弃失败条目（同样要做任务身份校验，避免误删同 key 的新登记）。</summary>
    private void Discard(string key, Task<EncodedImage?> expected)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Task, expected))
                return;
            _entries.Remove(key);
            _order.Remove(key);
        }
    }

    /// <summary>
    /// 按「条目数 + 总字节」淘汰最旧条目（调用方需持有 _gate）。
    /// 关键约束：至少保留一条——单张大图自身可能就超总预算，若把它也淘汰，
    /// 队首将永远无法命中缓存，钩子每次都要重新编码（正是本次修复要消除的问题）。
    /// 在飞条目的 Bytes 为 0，不计入总量（其峰值内存由条目数上限约束）。
    /// </summary>
    private void Trim()
    {
        long total = 0;
        foreach (var entry in _entries.Values) total += entry.Bytes;

        while (_entries.Count > 1 && (_entries.Count > MaxEntries || total > MaxTotalBytes))
        {
            string oldest = _order[0];
            _order.RemoveAt(0);
            if (!_entries.TryGetValue(oldest, out var entry)) continue;
            total -= entry.Bytes;
            _entries.Remove(oldest);
        }
    }

    /// <summary>
    /// 把命中项移到队尾（最近使用）。淘汰按"最久未用"进行，避免刚被取用的队首
    /// 因为一次新插入而被淘汰——那会让下一次 Ctrl+V 退回同步编码。
    /// </summary>
    private void Touch(string key)
    {
        int index = _order.IndexOf(key);
        if (index < 0 || index == _order.Count - 1) return;
        _order.RemoveAt(index);
        _order.Add(key);
    }

    /// <summary>缓存键：优先用内容哈希（同图同字节），哈希缺失时退回条目 Id。</summary>
    private static string KeyOf(ImageClipboardItem item)
        => string.IsNullOrEmpty(item.ImageHash) ? item.Id : item.ImageHash;
}
