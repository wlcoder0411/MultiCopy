using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using MultiCopy.Models;
using MultiCopy.Services;
using MultiCopy.ViewModels;
using MultiCopy.State;

namespace MultiCopy;

/// <summary>
/// 应用入口：单实例互斥锁、服务编排、主窗口创建与后端服务生命周期管理。
/// </summary>
public partial class App : Application
{
    private static readonly string MutexName = "Global\\MultiCopy_SingleInstance";
    private Mutex? _singleInstanceMutex;

    /// <summary>是否真正持有单实例互斥体。仅在 createdNew=true 时为 true。</summary>
    private bool _mutexOwned;

    // 后端服务
    public ClipboardQueue? Queue { get; private set; }
    public ClipboardService? ClipboardSvc { get; private set; }
    public ClipboardListenerService? Listener { get; private set; }
    public KeyboardHookService? KeyboardHook { get; private set; }
    public TrayIconService? Tray { get; private set; }
    public HotkeyService? HotkeyService { get; private set; }
    public MainViewModel? ViewModel { get; private set; }
    public PasteExecutor? PasteExecutor { get; private set; }
    public SnippetLibrary? Snippets { get; private set; }

    /// <summary>图片预编码缓存：让 Ctrl+V 钩子路径只做 memcpy，不在回调里编码大图。</summary>
    internal ImageEncodeCache? ImageCache { get; private set; }

    public bool ForceExit { get; set; }

    /// <summary>上次保存时置顶项的指纹，用于跳过未变化的冗余写盘。</summary>
    private string _lastPinnedFingerprint = string.Empty;

    /// <summary>上次保存时分组列表的指纹，用于跳过未变化的冗余写盘。</summary>
    private string _lastGroupsFingerprint = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：若已运行则退出
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例运行。createdNew=false 时当前进程并不持有该命名互斥体
            // （initiallyOwned 仅在新建时生效），因此绝不能在此或 OnExit 中释放它。
            Shutdown(0);
            return;
        }
        _mutexOwned = true;

        base.OnStartup(e);

        // 组装服务
        Queue = new ClipboardQueue();
        // 加载持久化的置顶项与分组（在 ViewModel 订阅 Changed 之前加载，避免触发不必要的 UI 刷新）
        Queue.LoadPinned(PinnedStorageService.Load());
        Queue.LoadGroups(GroupStorageService.Load());
        // 初始化指纹：避免首次普通队列变化触发对已加载置顶项/分组的冗余写盘
        _lastPinnedFingerprint = BuildPinnedFingerprint(Queue.PinnedItems);
        _lastGroupsFingerprint = BuildGroupsFingerprint(Queue.Groups);
        // 统一清理孤儿图片：联合 PinnedItems + Groups 中的图片引用，避免互相误删
        CleanupOrphanImagesUnified();
        ClipboardSvc = new ClipboardService();
        // 图片预编码缓存：图片编码是 O(W×H) 的重活，必须移出低级键盘钩子回调
        ImageCache = new ImageEncodeCache();
        Listener = new ClipboardListenerService(Queue, ClipboardSvc);
        KeyboardHook = new KeyboardHookService(Queue, ClipboardSvc, ImageCache);
        PasteExecutor = new PasteExecutor(ClipboardSvc, ImageCache);
        Snippets = new SnippetLibrary(SnippetStorageService.Load());
        ViewModel = new MainViewModel(Queue, PasteExecutor, Snippets);


        // 置顶项/分组变化后排队写盘（合并连续变化；OnExit 兜底）
        Queue.Changed += OnQueueChangedForPersistence;
        // 队首变化时预热图片编码缓存（Ctrl+V 只会消耗队首）
        Queue.Changed += OnQueueChangedForImagePrefetch;
        AppState.Instance.ModeChanged += OnModeChangedForImagePrefetch;
        // 切换活动分组只改出队范围、不触发 Queue.Changed，但队首会变，同样需要预热
        AppState.Instance.ActiveGroupChanged += OnActiveGroupChangedForImagePrefetch;

        // 加载用户设置（全局快捷键等）
        var settings = SettingsStorageService.Load();
        AppState.Instance.PastePrefix = settings.PastePrefix;
        AppState.Instance.PasteSuffix = settings.PasteSuffix;
        AppState.Instance.IsPastePrefixEnabled = settings.IsPastePrefixEnabled;
        AppState.Instance.IsPasteSuffixEnabled = settings.IsPasteSuffixEnabled;
        AppState.Instance.PrefixSequenceStart = settings.PrefixSequenceStart;
        AppState.Instance.PrefixSequenceCurrent = settings.PrefixSequenceCurrent;
        AppState.Instance.IsPrefixSequenceEnabled = settings.IsPrefixSequenceEnabled;
        AppState.Instance.PrefixSequenceFormat = settings.PrefixSequenceFormat;
        AppState.Instance.PrefixSequencePosition = settings.PrefixSequencePosition;
        AppState.Instance.MaxQueueItems = settings.MaxQueueItems;

        // 首次使用引导：仅在用户未关闭过时显示
        ViewModel.ShowOnboarding = !settings.HasSeenOnboarding;

        // 应用持久化主题：必须在创建窗口前，确保 DynamicResource 首次解析即为正确主题
        if (settings.Theme == AppTheme.Light)
            ThemeService.Set(AppTheme.Light);

        var window = new MainWindow();
        window.SetViewModel(ViewModel);
        // 应用持久化的窗口位置/尺寸/置顶状态（Show 之前）
        window.ApplyPersistedWindowState(settings);
        // HotkeyService 必须在 TrayIconService 之前创建（TrayIconService 构造需要它）
        HotkeyService = new HotkeyService(window, settings);
        // 片段速取热键：呼出速取窗（独立于主窗口显隐）。
        // WM_HOTKEY 在消息钩子上下文里到达，此处若同步创建/关闭窗口可能与焦点切换、
        // IME(TSF) 分离竞争造成死锁，整体延迟到 Background 优先级处理。
        HotkeyService.SnippetHotkeyTriggered += (_, _) =>
            window.Dispatcher.BeginInvoke(
                () => window.OpenSnippetPalette(),
                System.Windows.Threading.DispatcherPriority.Background);
        Tray = new TrayIconService(window, Queue, HotkeyService);
        // 主窗前台 Ctrl+V = 收编剪贴板（键盘手势入口），与 ⋯ / 托盘 / 工具栏入口汇入同一编排
        KeyboardHook.AdoptRequested += (_, _) => window.AdoptClipboardFromUser();
        // 进入后台采集后给一次说明气泡：窗口消失容易被误判为程序退出
        window.BackgroundCaptureEntered += (_, _) => Tray?.ShowBackgroundCaptureTip();
        // 被其他窗口挡住时也要说明原因，否则菜单点击看起来像"没反应"
        window.BackgroundCaptureDeclined += (_, _) => Tray?.ShowBackgroundCaptureBlockedTip();
        // 开机自启带 --minimized 参数：仅驻留托盘，不显示主窗口
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (minimized)
        {
            AppState.Instance.IsMonitoring = false; // 静默驻留：关闭监控，避免干扰日常复制粘贴
            new WindowInteropHelper(window).EnsureHandle(); // 创建 HWND（触发 SourceInitialized → StartBackendServices）但不显示窗口
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>是否有一次延迟写盘在排队（合并连续变化，避免钩子回调内重复排队）。</summary>
    private bool _persistScheduled;

    /// <summary>置顶项 / 分组列表是否有未落盘的变更。</summary>
    private bool _pinnedDirty;
    private bool _groupsDirty;

    /// <summary>
    /// 队列变化时，按需保存置顶项和分组列表（指纹比对跳过冗余写盘）。
    /// 置顶项与分组各自独立指纹：只有真正变化的子集写盘，避免普通队列操作引发连锁 IO。
    /// 指纹计算（纯内存）在此同步完成；**实际写盘延迟到当前消息处理之后**——
    /// 因为 Queue.Changed 会在 WH_KEYBOARD_LL 回调内同步触发（Ctrl+V 出队），
    /// 而 groups.json 是全量序列化 + 原子替换，条目多时可达数十毫秒，留在回调里会挤占
    /// LowLevelHooksTimeout 预算。延迟执行时读取的是当时最新状态，连续变化自动合并为一次写入。
    /// </summary>
    private void OnQueueChangedForPersistence(object? sender, EventArgs e)
    {
        if (Queue == null) return;

        // 置顶项
        var pinned = Queue.PinnedItems;
        var pinnedFp = BuildPinnedFingerprint(pinned);
        if (pinnedFp != _lastPinnedFingerprint)
        {
            _lastPinnedFingerprint = pinnedFp;
            _pinnedDirty = true;
        }

        // 分组列表（含组内项、置顶标识等）
        var groups = Queue.Groups;
        var groupsFp = BuildGroupsFingerprint(groups);
        if (groupsFp != _lastGroupsFingerprint)
        {
            _lastGroupsFingerprint = groupsFp;
            _groupsDirty = true;
        }

        if (_pinnedDirty || _groupsDirty) SchedulePersist();
    }

    /// <summary>把写盘排到当前消息处理之后（Background 优先级），多次变化只排队一次。</summary>
    private void SchedulePersist()
    {
        if (_persistScheduled) return;
        if (Dispatcher.HasShutdownStarted)
        {
            PersistPendingNow(); // 正在退出，直接写，避免丢改动
            return;
        }
        _persistScheduled = true;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _persistScheduled = false;
                PersistPendingNow();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            // 关闭竞态下 BeginInvoke 可能被拒：复位标志并改为同步写，
            // 否则脏标记会永久滞留（指纹已前移，后续变化不再被判脏）。
            _persistScheduled = false;
            PersistPendingNow();
            System.Diagnostics.Debug.WriteLine($"[Persist] 排程失败，改为同步写盘: {ex.Message}");
        }
    }

    /// <summary>仅写回标记为脏的子集（与指纹机制保持"只写变化部分"的语义）。</summary>
    private void PersistPendingNow()
    {
        if (Queue == null) return;
        if (_pinnedDirty)
        {
            _pinnedDirty = false;
            PinnedStorageService.Save(Queue.PinnedItems);
        }
        if (_groupsDirty)
        {
            _groupsDirty = false;
            GroupStorageService.Save(Queue.Groups);
        }
    }

    /// <summary>
    /// 为队首图片预热编码缓存。Ctrl+V 只会消耗队首，因此只需保证队首就绪；
    /// 缓存层自己做条目数与字节上限淘汰，这里可以无脑调用。
    /// 仅在队列粘贴模式开启时预热：其余模式不经过钩子写图片，预编码纯属浪费 CPU/内存。
    /// </summary>
    private void PrefetchHeadImage()
    {
        if (Queue == null || ImageCache == null) return;
        if (!AppState.Instance.ModeOn) return;
        if (Queue.Peek(AppState.Instance.ActiveGroupId) is ImageClipboardItem img)
            ImageCache.Prefetch(img);
    }

    private void OnQueueChangedForImagePrefetch(object? sender, EventArgs e) => PrefetchHeadImage();

    /// <summary>模式刚打开时队首可能已是图片：立即补一次预热。</summary>
    private void OnModeChangedForImagePrefetch(object? sender, bool modeOn)
    {
        if (modeOn) PrefetchHeadImage();
    }

    /// <summary>活动分组变化后出队范围变了，队首可能换成另一张图片。</summary>
    private void OnActiveGroupChangedForImagePrefetch(object? sender, string? groupId)
        => PrefetchHeadImage();

    /// <summary>
    /// 构建置顶项轻量指纹：Count + 各项按类型计算的签名 + CreatedAt.Ticks。
    /// 文本项用 Text.Length，图片项用 ImageHash。
    /// 用于检测置顶集合是否真正变化，避免普通队列操作触发冗余磁盘写入。
    /// </summary>
    private static string BuildPinnedFingerprint(IEnumerable<ClipboardItem> items)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var it in items)
        {
            switch (it)
            {
                case TextClipboardItem text:
                    sb.Append("text:").Append(text.Text.Length).Append('|').Append(it.CreatedAt.Ticks).Append(';');
                    break;
                case ImageClipboardItem img:
                    sb.Append("image:").Append(img.ImageHash).Append('|').Append(it.CreatedAt.Ticks).Append(';');
                    break;
            }
            n++;
        }
        return n + ":" + sb.ToString();
    }

    /// <summary>
    /// 构建分组列表轻量指纹：分组数 + 各分组元数据签名（Id/IsPinned/IsExpanded/PinnedAt/CreatedAt）
    /// + 组内项签名（同 BuildPinnedFingerprint 的项签名规则）。
    /// 用于检测分组列表是否真正变化，避免置顶项或普通队列操作触发冗余磁盘写入。
    /// </summary>
    private static string BuildGroupsFingerprint(IEnumerable<ClipboardGroup> groups)
    {
        var sb = new StringBuilder();
        int gn = 0;
        foreach (var g in groups)
        {
            sb.Append("[g]")
              .Append(g.Id).Append('|')
              .Append(g.Name).Append('|')
              .Append(g.IsPinned).Append('|')
              .Append(g.IsExpanded).Append('|')
              .Append(g.PinnedAt?.Ticks.ToString() ?? "null").Append('|')
              .Append(g.CreatedAt.Ticks).Append('|')
              .Append("items=");
            int in_ = 0;
            foreach (var it in g.Items)
            {
                switch (it)
                {
                    case TextClipboardItem text:
                        sb.Append("text:").Append(text.Text.Length).Append('|').Append(it.CreatedAt.Ticks).Append(';');
                        break;
                    case ImageClipboardItem img:
                        sb.Append("image:").Append(img.ImageHash).Append('|').Append(it.CreatedAt.Ticks).Append(';');
                        break;
                }
                in_++;
            }
            sb.Append(in_).Append(";");
            gn++;
        }
        return gn + ":" + sb.ToString();
    }

    /// <summary>
    /// 统一清理孤儿图片：联合 PinnedItems + Groups 中的图片引用，删除 images 目录下未被引用的 *.png。
    /// 必须联合收集：PinnedStorageService 和 GroupStorageService 共享 images 目录，
    /// 若任一方单独清理会误删对方正在引用的图片文件。
    /// </summary>
    private void CleanupOrphanImagesUnified()
    {
        if (Queue == null) return;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in Queue.PinnedItems)
        {
            if (it is ImageClipboardItem img)
                used.Add($"{img.Id}.png");
        }
        foreach (var g in Queue.Groups)
        {
            foreach (var it in g.Items)
            {
                if (it is ImageClipboardItem img)
                    used.Add($"{img.Id}.png");
            }
        }

        PinnedStorageService.CleanupOrphanImages(used);
    }

    /// <summary>主窗口 SourceInitialized 后调用：启动剪贴板监听、键盘钩子、托盘、全局快捷键。</summary>
    public void StartBackendServices(IntPtr hwnd)
    {
        Listener?.Start(hwnd);
        KeyboardHook?.Start(hwnd); // 主窗句柄：前台 Ctrl+V 的收编手势判定用
        Tray?.Start();
        HotkeyService?.Start(hwnd);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底保存置顶项与分组（虽然 Changed 事件已即时保存，但 OnExit 再保一次防漏）
        if (Queue != null)
        {
            PinnedStorageService.Save(Queue.PinnedItems);
            GroupStorageService.Save(Queue.Groups);
            Queue.Changed -= OnQueueChangedForPersistence;
            Queue.Changed -= OnQueueChangedForImagePrefetch;
        }
        AppState.Instance.ModeChanged -= OnModeChangedForImagePrefetch;
        AppState.Instance.ActiveGroupChanged -= OnActiveGroupChangedForImagePrefetch;
        Listener?.Stop();
        KeyboardHook?.Dispose();
        Tray?.Dispose();
        HotkeyService?.Dispose();
        // 仅在真正持有互斥体时释放：非持有者调用 ReleaseMutex 会抛 ApplicationException。
        // createdNew=false 的二次启动路径会走到 OnExit，此前无条件释放会导致未处理异常。
        if (_mutexOwned)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* 非持有者释放：忽略 */ }
            _mutexOwned = false;
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
