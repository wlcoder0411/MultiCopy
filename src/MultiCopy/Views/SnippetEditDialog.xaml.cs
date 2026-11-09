using System.Windows;
using System.Windows.Input;
using MultiCopy.Models;

namespace MultiCopy.Views;

/// <summary>
/// 片段编辑对话框（新增/编辑共用）：备注名（可选）+ 多行内容 + 隐藏内容开关。
/// 内容 Ctrl+Enter 也可确认。返回 Snippet（取消返回 null）。
/// </summary>
public partial class SnippetEditDialog : Window
{
    private SnippetEditDialog(string title, string name, string content, bool isSecret)
    {
        InitializeComponent();
        Title = title;
        TitleTextBlock.Text = $"{title}（粘贴时原样输出，不追加前后缀/序号）：";
        NameBox.Text = name;
        ContentTextBox.Text = content;
        SecretCheckBox.IsChecked = isSecret;
        Loaded += (_, _) => { if (string.IsNullOrEmpty(content)) ContentTextBox.Focus(); };
    }

    /// <summary>
    /// 显示编辑对话框。编辑现有片段时传入其字段；新增时全空。
    /// 返回包含新字段值的 Snippet（Id 复用传入值；取消或关闭返回 null）。
    /// </summary>
    public static Snippet? Show(Window? owner, string title, string? name = null,
        string? content = null, bool isSecret = false, string? id = null)
    {
        var dlg = new SnippetEditDialog(title, name ?? string.Empty, content ?? string.Empty, isSecret);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (dlg.ShowDialog() != true) return null;
        return new Snippet
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Name = dlg.NameBox.Text.Trim(),
            Content = dlg.ContentTextBox.Text ?? string.Empty,
            IsSecret = dlg.SecretCheckBox.IsChecked == true,
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => TryConfirm();

    /// <summary>内容框内 Ctrl+Enter 快速确认。</summary>
    private void ContentTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            TryConfirm();
        }
    }

    private void TryConfirm()
    {
        if (string.IsNullOrWhiteSpace(ContentTextBox.Text))
        {
            MessageBox.Show(this, "片段内容不能为空。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
