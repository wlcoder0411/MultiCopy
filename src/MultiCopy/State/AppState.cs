using MultiCopy.Infrastructure;

namespace MultiCopy.State;

/// <summary>前缀序号格式。</summary>
public enum PrefixSequenceFormat
{
    /// <summary>纯数字：1, 2, 3</summary>
    Plain,
    /// <summary>补零两位：01, 02, 03</summary>
    ZeroPadded,
    /// <summary>带括号：(1), (2), (3)</summary>
    Parenthesized,
    /// <summary>带点：1., 2., 3.</summary>
    Dotted,
    /// <summary>带顿号：1、 2、 3、</summary>
    Dunhao,
}

/// <summary>前缀序号与前缀内容的位置关系。</summary>
public enum PrefixSequencePosition
{
    /// <summary>序号在前缀内容之后：前缀+序号+正文</summary>
    AfterPrefix,
    /// <summary>序号在前缀内容之前：序号+前缀+正文</summary>
    BeforePrefix,
}

/// <summary>
/// 全局可变状态（单例）。跨服务共享的标志位与模式开关。
/// 所有字段均由 UI 线程访问（钩子回调/剪贴板消息/UI 事件同线程），volatile 足够。
/// </summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    /// <summary>普通队列容量上限（含未分组+所有分组）。超出后不再采集，并提示用户清理。</summary>
    public const int MaxQueueItems = 30;

    /// <summary>
    /// 粘贴回音抑制窗口（毫秒）。队列粘贴后，目标应用可能把刚粘贴的内容
    /// 写回剪贴板（典型：资源管理器新建文件夹的重命名编辑框），窗口内相同文本不采集。
    /// 5 秒覆盖"粘贴→重命名提交"的连续操作，又不至于误伤后续真正的同文本复制。
    /// </summary>
    public const int PasteEchoWindowMs = 5000;

    /// <summary>单条采集文本上限（防超大文本拖慢）。监听截断与回音记录共用同一标准。</summary>
    public const int MaxItemLength = 100_000;

    private volatile bool _modeOn;
    private volatile bool _isMonitoring = true; // 默认开启监控
    private string? _activeGroupId;
    private string _pastePrefix = string.Empty; // 粘贴前缀内容（空=无前缀）
    private string _pasteSuffix = string.Empty; // 粘贴后缀内容（空=无后缀）
    private bool _isPastePrefixEnabled;         // true=启用前缀
    private bool _isPasteSuffixEnabled;         // true=启用后缀
    private int _prefixSequenceStart = 1;       // 序号起始值
    private int _prefixSequenceCurrent = 1;     // 当前序号（每次粘贴后递增）
    private bool _isPrefixSequenceEnabled;      // true=启用序号自增
    private PrefixSequenceFormat _prefixSequenceFormat = PrefixSequenceFormat.Plain;
    private PrefixSequencePosition _prefixSequencePosition = PrefixSequencePosition.AfterPrefix;

    /// <summary>队列粘贴模式开关：开启时 Ctrl+V 自动出队；关闭时 Ctrl+V 普通粘贴。</summary>
    public bool ModeOn
    {
        get => _modeOn;
        set
        {
            if (_modeOn == value) return;
            _modeOn = value;
            ModeChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// 剪贴板监控开关：关闭时不采集剪贴板、不拦截 Ctrl+V（完全静默待机）。
    /// 由 ViewModel 设置，ClipboardListenerService 和 KeyboardHookService 读取。
    /// </summary>
    public bool IsMonitoring
    {
        get => _isMonitoring;
        set
        {
            if (_isMonitoring == value) return;
            _isMonitoring = value;
            MonitoringChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// 当前活动分组 Id（null=未分组/默认）。新复制内容归入此分组。
    /// 由 ViewModel 设置，ClipboardListenerService 读取。
    /// </summary>
    public string? ActiveGroupId
    {
        get => _activeGroupId;
        set
        {
            if (_activeGroupId == value) return;
            _activeGroupId = value;
            ActiveGroupChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// 粘贴前缀内容（空字符串=无前缀）。由设置对话框设置，PasteExecutor 和 KeyboardHookService 读取。
    /// 文本粘贴时仅当 IsPastePrefixEnabled 为 true 才追加到开头；图片粘贴不追加。
    /// </summary>
    public string PastePrefix
    {
        get => _pastePrefix;
        set
        {
            var v = value ?? string.Empty;
            if (_pastePrefix == v) return;
            _pastePrefix = v;
            PasteSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 粘贴后缀内容（空字符串=无后缀）。由设置对话框设置，PasteExecutor 和 KeyboardHookService 读取。
    /// 文本粘贴时仅当 IsPasteSuffixEnabled 为 true 才追加到末尾；图片粘贴不追加。
    /// </summary>
    public string PasteSuffix
    {
        get => _pasteSuffix;
        set
        {
            var v = value ?? string.Empty;
            if (_pasteSuffix == v) return;
            _pasteSuffix = v;
            PasteSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>是否启用粘贴前缀。由设置对话框设置，PasteExecutor 和 KeyboardHookService 读取。</summary>
    public bool IsPastePrefixEnabled
    {
        get => _isPastePrefixEnabled;
        set
        {
            if (_isPastePrefixEnabled == value) return;
            _isPastePrefixEnabled = value;
            PasteSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>是否启用粘贴后缀。由设置对话框设置，PasteExecutor 和 KeyboardHookService 读取。</summary>
    public bool IsPasteSuffixEnabled
    {
        get => _isPasteSuffixEnabled;
        set
        {
            if (_isPasteSuffixEnabled == value) return;
            _isPasteSuffixEnabled = value;
            PasteSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>序号起始值（默认 1）。由设置对话框设置，重置序号时回到此值。</summary>
    public int PrefixSequenceStart
    {
        get => _prefixSequenceStart;
        set
        {
            if (_prefixSequenceStart == value) return;
            _prefixSequenceStart = value;
            PasteSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>当前序号值。每次粘贴后递增，不触发 PasteSettingsChanged（运行时变化不刷新 UI）。</summary>
    public int PrefixSequenceCurrent
    {
        get => _prefixSequenceCurrent;
        set => _prefixSequenceCurrent = value;
    }

    /// <summary>是否启用前缀序号自增。由设置对话框设置，PasteExecutor 和 KeyboardHookService 读取。</summary>
    public bool IsPrefixSequenceEnabled
    {
        get => _isPrefixSequenceEnabled;
        set
        {
            if (_isPrefixSequenceEnabled == value) return;
            _isPrefixSequenceEnabled = value;
            PasteSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>序号格式。由设置对话框设置，PasteExecutor 和 KeyboardHookService 读取。</summary>
    public PrefixSequenceFormat PrefixSequenceFormat
    {
        get => _prefixSequenceFormat;
        set
        {
            if (_prefixSequenceFormat == value) return;
            _prefixSequenceFormat = value;
            PasteSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>序号与前缀内容的位置关系。由设置对话框设置，PasteExecutor 和 KeyboardHookService 读取。</summary>
    public PrefixSequencePosition PrefixSequencePosition
    {
        get => _prefixSequencePosition;
        set
        {
            if (_prefixSequencePosition == value) return;
            _prefixSequencePosition = value;
            PasteSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>递增当前序号（每次粘贴后调用）。</summary>
    public void IncrementPrefixSequence() => _prefixSequenceCurrent++;

    /// <summary>重置当前序号到起始值。</summary>
    public void ResetPrefixSequence() => _prefixSequenceCurrent = _prefixSequenceStart;

    /// <summary>
    /// 构建粘贴文本：按启用状态拼接前缀+序号+正文+后缀。
    /// 序号仅在前缀启用且序号自增启用时生效；每次调用后递增序号。
    /// 供 PasteExecutor 和 KeyboardHookService 共用，避免重复逻辑。
    /// </summary>
    public string BuildPasteText(string text)
    {
        string prefix = _isPastePrefixEnabled ? _pastePrefix : string.Empty;
        string suffix = _isPasteSuffixEnabled ? _pasteSuffix : string.Empty;

        if (!_isPastePrefixEnabled || !_isPrefixSequenceEnabled)
            return prefix + text + suffix;

        string seq = SequenceFormatter.Format(_prefixSequenceCurrent, _prefixSequenceFormat);
        string fullPrefix = _prefixSequencePosition == PrefixSequencePosition.BeforePrefix
            ? seq + prefix
            : prefix + seq;
        IncrementPrefixSequence();
        return fullPrefix + text + suffix;
    }

    /// <summary>
    /// 防自身复制回环：我们主动写剪贴板前置位，WM_CLIPBOARDUPDATE 处理后清位。
    /// </summary>
    public volatile bool IsWritingClipboard;

    /// <summary>
    /// 防钩子重入：PasteExecutor 用 SendInput 模拟 Ctrl+V 时置位，钩子检测到则放行不拦截。
    /// </summary>
    public volatile bool IsSimulatingPaste;

    /// <summary>
    /// 最近若干次队列粘贴写入剪贴板的文本（含前后缀拼接结果，按 MaxItemLength 截断）。
    /// 由 KeyboardHookService（Ctrl+V 路径）和 PasteExecutor（点选路径）在写剪贴板成功后记录，
    /// ClipboardListenerService 用于识别"粘贴回音"（目标应用把刚粘贴的内容写回剪贴板）。
    /// 用小集合而非单槽位：交错粘贴（重命名框悬置未提交 + 中途另一次队列粘贴）时，
    /// 单槽位会被覆盖导致早先的回音漏抑制。仅 UI 线程读写，无需锁。
    /// </summary>
    private readonly List<(string Text, long Ticks)> _recentPastes = new(8);

    /// <summary>记录一次队列粘贴写入剪贴板的文本（供回音抑制判定）。</summary>
    public void RecordPastedText(string text)
    {
        // 与监听侧截断标准一致，保证回音比对逐字节匹配
        if (text.Length > MaxItemLength) text = text.Substring(0, MaxItemLength);
        long now = Environment.TickCount64;
        // 清理过期记录
        _recentPastes.RemoveAll(p => now - p.Ticks >= PasteEchoWindowMs);
        _recentPastes.Add((text, now));
        // 容量上限：只保留最近 8 条，防无界增长
        if (_recentPastes.Count > 8) _recentPastes.RemoveRange(0, _recentPastes.Count - 8);
    }

    /// <summary>判定剪贴板文本是否为粘贴回音（与最近一次队列粘贴内容相同且在窗口内）。</summary>
    public bool IsPasteEcho(string text)
    {
        long now = Environment.TickCount64;
        foreach (var p in _recentPastes)
        {
            if (p.Text == text && now - p.Ticks < PasteEchoWindowMs) return true;
        }
        return false;
    }

    /// <summary>模式变化通知（oldValue→newValue）。</summary>
    public event EventHandler<bool>? ModeChanged;

    /// <summary>监控开关变化通知（newValue）。</summary>
    public event EventHandler<bool>? MonitoringChanged;

    /// <summary>活动分组变化通知（newGroupId，null=切回未分组）。</summary>
    public event EventHandler<string?>? ActiveGroupChanged;

    /// <summary>粘贴前后缀设置变化通知（任意相关字段变化时触发）。</summary>
    public event EventHandler? PasteSettingsChanged;

    /// <summary>队列已满通知（ClipboardListenerService 触发，TrayIconService 订阅显示气泡）。</summary>
    public event EventHandler? QueueFullNotification;

    /// <summary>触发队列已满通知。</summary>
    public void NotifyQueueFull() => QueueFullNotification?.Invoke(this, EventArgs.Empty);
}
