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
        // 加载持久化的置顶项（在 ViewModel 订阅 Changed 之前加载，避免触发不必要的 UI 刷新）
        Queue.LoadPinned(PinnedStorageService.Load());
        // 初始化指纹：避免首次普通队列变化触发对已加载置顶项的冗余写盘
        _lastPinnedFingerprint = BuildPinnedFingerprint(Queue.PinnedItems);
        ClipboardSvc = new ClipboardService();
        Listener = new ClipboardListenerService(Queue, ClipboardSvc);
        KeyboardHook = new KeyboardHookService(Queue, ClipboardSvc);
        var pasteExecutor = new PasteExecutor(ClipboardSvc);
        ViewModel = new MainViewModel(Queue, pasteExecutor);

        // 置顶项变化时即时持久化（防丢失，OnExit 兜底）
        Queue.Changed += OnQueueChangedForPersistence;

        // 加载用户设置（全局快捷键等）
        var settings = SettingsStorageService.Load();

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

    /// <summary>队列变化时，仅当置顶项实际变化才保存（指纹比对跳过冗余写盘）。</summary>
    private void OnQueueChangedForPersistence(object? sender, EventArgs e)
    {
        if (Queue == null) return;
        var pinned = Queue.PinnedItems;
        var fp = BuildPinnedFingerprint(pinned);
        if (fp == _lastPinnedFingerprint) return; // 置顶项未变，跳过写盘
        _lastPinnedFingerprint = fp;
        PinnedStorageService.Save(pinned);
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
        // 兜底保存置顶项（虽然 Changed 事件已即时保存，但 OnExit 再保一次防漏）
        if (Queue != null)
        {
            PinnedStorageService.Save(Queue.PinnedItems);
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
