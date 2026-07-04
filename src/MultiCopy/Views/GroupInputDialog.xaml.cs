using System.Windows;
using System.Windows.Input;

namespace MultiCopy.Views;

/// <summary>
/// 轻量文本输入对话框（用于分组命名/重命名）。
/// 普通 Window（不带 WS_EX_NOACTIVATE），ShowDialog 自然获焦，不影响主窗口特性。
/// 用法：var name = GroupInputDialog.Prompt(owner, "新建分组", "分组名称", "");
/// </summary>
public partial class GroupInputDialog : Window
{
    /// <summary>用户确认的结果；取消则为 null。</summary>
    public string? ResultText { get; private set; }

    public string Prompt { get; }

    private GroupInputDialog(string title, string prompt, string defaultValue)
    {
        Title = title;
        Prompt = prompt;
        DataContext = this;
        InitializeComponent();
        InputBox.Text = defaultValue;
        InputBox.SelectAll();
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
        };
    }

    /// <summary>弹出输入框，返回用户输入；取消返回 null。</summary>
    public static string? PromptForInput(Window? owner, string title, string prompt, string defaultValue = "")
    {
        var dlg = new GroupInputDialog(title, prompt, defaultValue);
        if (owner != null) dlg.Owner = owner;
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Confirm();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
    }

    private void Confirm()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ResultText = text;
        DialogResult = true;
    }

    private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        OkButton.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
    }
}
