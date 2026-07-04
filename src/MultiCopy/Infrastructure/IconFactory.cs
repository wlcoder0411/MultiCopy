using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MultiCopy.Infrastructure;

/// <summary>
/// 加载内嵌 .ico 资源作为图标。
/// 用 pack URI（pack://application:,,,/...）加载——Hardcodet.TaskbarIcon 的 ToIcon
/// 内部调用 Application.GetResourceStream，仅识别 pack 资源 URI。
/// .ico 文件在构建期由 Assets/gen-icons.js 生成并作为 &lt;Resource&gt; 内嵌。
/// 图标加载后 Freeze 为不可变，通过 Lazy 缓存为静态实例复用（避免每次模式切换重新分配 BitmapImage）。
/// </summary>
internal static class IconFactory
{
    private const string AppIconUri = "pack://application:,,,/Assets/app.ico";
    private const string TrayOnUri = "pack://application:,,,/Assets/tray-on.ico";
    private const string TrayOffUri = "pack://application:,,,/Assets/tray-off.ico";

    private static readonly Lazy<ImageSource> _appIcon = new(LoadImage(AppIconUri));
    private static readonly Lazy<ImageSource> _trayOn = new(LoadImage(TrayOnUri));
    private static readonly Lazy<ImageSource> _trayOff = new(LoadImage(TrayOffUri));

    /// <summary>窗口图标（青蓝）。</summary>
    public static ImageSource CreateAppIcon() => _appIcon.Value;

    /// <summary>托盘图标：模式开=绿，关=灰。</summary>
    public static ImageSource CreateTrayIcon(bool modeOn) => modeOn ? _trayOn.Value : _trayOff.Value;

    private static ImageSource LoadImage(string packUri)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(packUri, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
