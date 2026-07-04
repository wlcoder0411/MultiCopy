namespace MultiCopy.State;

/// <summary>
/// 全局可变状态（单例）。跨服务共享的标志位与模式开关。
/// 所有字段均由 UI 线程访问（钩子回调/剪贴板消息/UI 事件同线程），volatile 足够。
/// </summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    /// <summary>普通队列容量上限（含未分组+所有分组）。超出后不再采集，并提示用户清理。</summary>
    public const int MaxQueueItems = 30;

    private volatile bool _modeOn;
    private volatile bool _isMonitoring = true; // 默认开启监控
    private string? _activeGroupId;

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
    /// 防自身复制回环：我们主动写剪贴板前置位，WM_CLIPBOARDUPDATE 处理后清位。
    /// </summary>
    public volatile bool IsWritingClipboard;

    /// <summary>
    /// 防钩子重入：PasteExecutor 用 SendInput 模拟 Ctrl+V 时置位，钩子检测到则放行不拦截。
    /// </summary>
    public volatile bool IsSimulatingPaste;

    /// <summary>模式变化通知（oldValue→newValue）。</summary>
    public event EventHandler<bool>? ModeChanged;

    /// <summary>监控开关变化通知（newValue）。</summary>
    public event EventHandler<bool>? MonitoringChanged;

    /// <summary>活动分组变化通知（newGroupId，null=切回未分组）。</summary>
    public event EventHandler<string?>? ActiveGroupChanged;

    /// <summary>队列已满通知（ClipboardListenerService 触发，TrayIconService 订阅显示气泡）。</summary>
    public event EventHandler? QueueFullNotification;

    /// <summary>触发队列已满通知。</summary>
    public void NotifyQueueFull() => QueueFullNotification?.Invoke(this, EventArgs.Empty);
}
