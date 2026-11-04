using System.Windows;

namespace MultiCopy.Views;

/// <summary>
/// 通用确认对话框：跟随明暗主题的二次确认弹窗。
/// 用于不可恢复的危险操作（如清空普通队列——撤销栈会一并清空，无法 Ctrl+Z 找回）。
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    /// <summary>弹出确认对话框。返回 true=用户点击确认按钮。</summary>
    public static bool Show(Window? owner, string title, string message, string confirmText = "确定")
    {
        var dlg = new ConfirmDialog(title, message, confirmText);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        else
        {
            // owner 隐藏时（如从托盘触发）CenterOwner 会定位异常，退化为 CenterScreen
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dlg.ShowDialog() == true;
    }

    /// <summary>确认按钮：关闭对话框并返回 true（Enter 由 IsDefault 触发同一处理器）。</summary>
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
