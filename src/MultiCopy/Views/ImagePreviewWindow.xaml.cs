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

    /// <summary>是否处于"悬停预览"模式（由主窗口设置）。该模式下鼠标离开预览窗会通知主窗口延迟关闭。</summary>
    public bool IsHoverPreview { get; set; }

    /// <summary>悬停预览模式下鼠标离开预览窗（主窗口据此延迟关闭，允许在图片行与预览窗之间移动）。</summary>
    public event EventHandler? HoverExited;

    /// <summary>当前预览的条目（尺寸适配要用图片宽高比，SetItem 存下供 PlaceNear 使用）。</summary>
    private ImageClipboardItem? _currentItem;

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (IsHoverPreview) HoverExited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置预览内容（尺寸与位置由紧随其后的 PlaceNear 统一计算）。</summary>
    public void SetItem(ImageClipboardItem item)
    {
        _currentItem = item;
        PreviewImage.Source = item.Image;
        InfoText.Text = BuildInfo(item);
    }

    /// <summary>
    /// 贴靠主窗口放置并按图片适配尺寸。不变量：**预览窗绝不覆盖主窗口**——
    /// 覆盖主窗会让指针落进预览窗（悬停打开又立刻关闭的抖动），更会遮住删除/置顶
    /// 按钮（正是要修的缺陷）。做法：主窗两侧各有一个槽位，取更宽的一侧放置；
    /// 槽宽不足以放下按 70% 虚拟屏适配的图时，按槽宽收缩预览（Stretch=Uniform 等比）。
    /// 垂直锚定 anchorTop（悬停=指针处，贴着正在看的那一行；空格=null 落主窗顶部）。
    ///
    /// 极端兜底：两侧槽都放不下最小宽度（主窗几乎占满屏幕）时，夹回虚拟屏内
    /// 允许与主窗边缘重叠——此时任何摆放都会重叠，这是唯一不越界的选择。
    /// 多显示器下 VirtualScreen 跨屏的局限与旧实现相同，未处理。
    /// </summary>
    public void PlaceNear(Window owner, double? anchorTop = null)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        const double gap = 12;

        // 槽位：主窗右侧 / 左侧与虚拟屏边缘之间的空间，取更宽的一侧
        double rightSlotW = virtualRight - (owner.Left + owner.ActualWidth + gap);
        double leftSlotW = (owner.Left - gap) - virtualLeft;
        bool useRight = rightSlotW >= leftSlotW;
        double slotWidth = Math.Max(rightSlotW, leftSlotW);

        if (_currentItem != null) FitToImage(_currentItem, slotWidth);

        double left = useRight
            ? owner.Left + owner.ActualWidth + gap
            : owner.Left - Width - gap;
        // 槽宽缩到最小仍放不下（罕见）：夹回虚拟屏内，不越界即可
        left = Math.Clamp(left, virtualLeft + 4, Math.Max(virtualLeft + 4, virtualRight - Width - 4));

        // 垂直：锚点与预览中线对齐（贴着正在看的内容），夹在虚拟屏内
        double anchor = anchorTop ?? owner.Top;
        double top = Math.Clamp(anchor - Height / 2,
            virtualTop + 4, Math.Max(virtualTop + 4, virtualBottom - Height - 4));

        Left = left;
        Top = top;
    }

    /// <summary>
    /// 按图片像素尺寸（≈96dpi 下的 DIP）适配窗口：不放大，仅缩小。
    /// 槽宽是硬约束（保证不覆盖主窗），虚拟屏 70% 是上限。
    /// </summary>
    private void FitToImage(ImageClipboardItem item, double slotWidth)
    {
        const double chromeW = 26; // 左右 Margin 24 + 边框 2
        const double chromeH = 66; // 标题行 + 信息行 + 边框
        double maxW = Math.Max(MinWidth - chromeW, Math.Min(slotWidth - chromeW, SystemParameters.VirtualScreenWidth * 0.7));
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
