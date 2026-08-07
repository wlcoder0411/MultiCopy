using System.Windows;
using System.Windows.Controls;

namespace MultiCopy.Views;

/// <summary>
/// 编辑前缀/后缀内容的对话框。
/// 支持多行输入，并提供常用标点预设按钮。
/// </summary>
public partial class EditPasteSuffixDialog : Window
{
    private string _result;

    private EditPasteSuffixDialog(string title, string initialText)
    {
        _result = initialText;
        InitializeComponent();
        Title = title;
        TitleTextBlock.Text = $"{title}（支持换行）：";
        ContentTextBox.Text = initialText;
        Loaded += (_, _) => ContentTextBox.Focus();
    }

    /// <summary>
    /// 显示编辑对话框。返回用户确认后的文本；取消或关闭窗口返回 null。
    /// </summary>
    public static string? Show(Window? owner, string title, string initialText)
    {
        var dlg = new EditPasteSuffixDialog(title, initialText);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    /// <summary>点击预设按钮：在当前光标位置插入对应字符。</summary>
    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string s) return;

        string text = ContentTextBox.Text ?? string.Empty;
        int caret = ContentTextBox.CaretIndex;
        if (caret < 0) caret = 0;
        if (caret > text.Length) caret = text.Length;

        ContentTextBox.Text = text.Insert(caret, s);
        ContentTextBox.CaretIndex = caret + s.Length;
        ContentTextBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _result = ContentTextBox.Text ?? string.Empty;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>一键清空编辑区内容，便于重新输入。清空后聚焦输入框，方便立即录入。</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ContentTextBox.Text = string.Empty;
        ContentTextBox.Focus();
    }
}
