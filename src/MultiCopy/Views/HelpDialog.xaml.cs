using System.Windows;

namespace MultiCopy.Views;

/// <summary>
/// 快捷键与使用说明弹窗。集中解释全局快捷键与「仅窗口内生效」的操作，
/// 避免首次使用者不知道数字键、Ctrl+Z、方向键的使用前提。
/// </summary>
public partial class HelpDialog : Window
{
    public HelpDialog()
    {
        InitializeComponent();
    }

    /// <summary>打开说明窗口（父窗口不可见时居中显示）。</summary>
    public static void Open(Window? owner)
    {
        var dlg = new HelpDialog();
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dlg.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
