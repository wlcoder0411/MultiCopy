using System.Windows;

namespace MultiCopy.Views;

/// <summary>
/// 支持作者弹窗：放置收款码与致谢文案。
/// 纯展示弹窗，无数据交互，通过静态方法 Show 打开。
/// </summary>
public partial class SupportDialog : Window
{
    public SupportDialog()
    {
        InitializeComponent();
    }

    /// <summary>弹出支持作者弹窗（模态）。</summary>
    public static void Show(Window? owner)
    {
        var dlg = new SupportDialog();
        if (owner != null) dlg.Owner = owner;
        dlg.ShowDialog();
    }
}
