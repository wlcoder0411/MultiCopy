using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MultiCopy.Models;
using MultiCopy.Native;

namespace MultiCopy.Views;

/// <summary>
/// 图像大图预览窗（PasteNow 式空格预览）。
/// ShowActivated=False + WS_EX_NOACTIVATE：弹出与交互均不抢目标应用焦点，
/// 键盘焦点留在 MultiCopy 主窗口（空格/Esc 由主窗口统一处理），点选粘贴链路不受影响。
/// </summary>
public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyNoActivate();
    }

    /// <summary>设置预览内容并按图片尺寸适配窗口。</summary>
    public void SetItem(ImageClipboardItem item)
    {
        PreviewImage.Source = item.Image;
        InfoText.Text = BuildInfo(item);
        FitToImage(item);
    }

    /// <summary>贴靠主窗口右侧放置；右侧放不下改左侧，再不行居中，并收敛到虚拟屏内。</summary>
    public void PlaceNear(Window owner)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        double left = owner.Left + owner.Width + 12;
        if (left + Width > virtualRight)
            left = owner.Left - Width - 12;
        if (left < virtualLeft)
            left = owner.Left + (owner.Width - Width) / 2;

        double top = Math.Clamp(owner.Top, virtualTop,
            Math.Max(virtualTop, virtualBottom - Height - 8));

        Left = left;
        Top = top;
    }

    /// <summary>按图片像素尺寸（≈96dpi 下的 DIP）适配窗口：不放大，仅缩小到虚拟屏约束内。</summary>
    private void FitToImage(ImageClipboardItem item)
    {
        const double chromeW = 26; // 左右 Margin 24 + 边框 2
        const double chromeH = 66; // 标题行 + 信息行 + 边框
        double maxW = SystemParameters.VirtualScreenWidth * 0.7;
        double maxH = SystemParameters.VirtualScreenHeight * 0.7;

        double scale = Math.Min(1.0,
            Math.Min(maxW / Math.Max(1, item.Width), maxH / Math.Max(1, item.Height)));
        Width = Math.Max(MinWidth, Math.Round(item.Width * scale) + chromeW);
        Height = Math.Max(MinHeight, Math.Round(item.Height * scale) + chromeH);
    }

    private static string BuildInfo(ImageClipboardItem item)
    {
        string source = string.IsNullOrEmpty(item.SourceApp) ? "未知来源" : item.SourceApp;
        return $"{source} · 复制于 {item.CreatedAt:yyyy/M/d HH:mm} · {item.Width} × {item.Height} 像素";
    }

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 无边框窗口的拖动；DragMove 不要求窗口处于激活状态
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>与主窗口一致：不参与前台激活，键盘焦点留在原窗口。</summary>
    private void ApplyNoActivate()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            IntPtr ex = Win32.GetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE);
            int style = ex.ToInt32() | Constants.WS_EX_NOACTIVATE;
            Win32.SetWindowLongCompat(hwnd, Constants.GWL_EXSTYLE, new IntPtr(style));
        }
        catch
        {
            // 失败则退化为普通窗口（预览仍可用，仅弹出瞬间可能抢焦点）
        }
    }
}
