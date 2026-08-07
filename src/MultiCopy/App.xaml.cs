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

    // 后端服务
    public ClipboardQueue? Queue { get; private set; }
    public ClipboardService? ClipboardSvc { get; private set; }
    public ClipboardListenerService? Listener { get; private set; }
    public KeyboardHookService? KeyboardHook { get; private set; }
    public TrayIconService? Tray { get; private set; }
    public HotkeyService? HotkeyService { get; private set; }
    public MainViewModel? ViewModel { get; private set; }

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
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        // 组装服务
        Queue = new ClipboardQueue();
        // 加载持久化的置顶项与分组（在 ViewModel 订阅 Changed 之前加载，避免触发不必要的 UI 刷新）
        Queue.LoadPinned(PinnedStorageService.Load());
        Queue.LoadGroups(GroupStorageService.Load());
        // 初始化指纹：避免首次普通队列变化触发对已加载置顶项/分组的冗余写盘
        _lastPinnedFingerprint = BuildPinnedFingerprint(Queue.PinnedItems);
        _lastGroupsFingerprint = BuildGroupsFingerprint(Queue.Groups);
        // 统一清理孤儿图片：联合 PinnedItems + Groups 的图片引用，避免互相误删
        CleanupOrphanImagesUnified();
        ClipboardSvc = new ClipboardService();
        Listener = new ClipboardListenerService(Queue, ClipboardSvc);
        KeyboardHook = new KeyboardHookService(Queue, ClipboardSvc);
        var pasteExecutor = new PasteExecutor(ClipboardSvc);
        ViewModel = new MainViewModel(Queue, pasteExecutor);

        // 置顶项/分组变化时即时持久化（防丢失，OnExit 兜底）
        Queue.Changed += OnQueueChangedForPersistence;

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

        var window = new MainWindow();
        window.SetViewModel(ViewModel);
        // HotkeyService 必须在 TrayIconService 之前创建（TrayIconService 构造需要它）
        HotkeyService = new HotkeyService(window, settings);
        Tray = new TrayIconService(window, Queue, HotkeyService);
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

    /// <summary>
    /// 队列变化时，按需保存置顶项和分组列表（指纹比对跳过冗余写盘）。
    /// 置顶项与分组各自独立指纹：只有真正变化的子集写盘，避免普通队列操作引发连锁 IO。
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
            PinnedStorageService.Save(pinned);
        }

        // 分组列表（含组内项、置顶标识等）
        var groups = Queue.Groups;
        var groupsFp = BuildGroupsFingerprint(groups);
        if (groupsFp != _lastGroupsFingerprint)
        {
            _lastGroupsFingerprint = groupsFp;
            GroupStorageService.Save(groups);
        }
    }

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
        KeyboardHook?.Start();
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
        }
        Listener?.Stop();
        KeyboardHook?.Dispose();
        Tray?.Dispose();
        HotkeyService?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
