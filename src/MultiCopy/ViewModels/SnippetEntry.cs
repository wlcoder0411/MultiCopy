using MultiCopy.Models;

namespace MultiCopy.ViewModels;

/// <summary>
/// 片段列表条目包装：把 Snippet 映射为单行显示文本。
/// Title = 名称（有名称时，隐藏内容加 🔒 前缀）或内容预览；
/// Subtitle = 有名称时的内容预览（隐藏内容打码）。
/// </summary>
public sealed class SnippetEntry
{
    private const string SecretMask = "●●●●●●";

    public Snippet Model { get; }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>是否隐藏内容（列表不显示原文、不给全文 tooltip）。</summary>
    public bool IsSecret { get; }

    public SnippetEntry(Snippet model)
    {
        Model = model;
        IsSecret = model.IsSecret;
        string preview = model.IsSecret ? SecretMask : Preview(model.Content);
        Title = string.IsNullOrWhiteSpace(model.Name)
            ? preview
            : (model.IsSecret ? "🔒 " + model.Name.Trim() : model.Name.Trim());
        Subtitle = string.IsNullOrWhiteSpace(model.Name) ? string.Empty : preview;
    }

    /// <summary>内容预览：首行、截断 60 字符、多行折叠为 ↵ 结尾。</summary>
    private static string Preview(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        string line = content.Split('\n')[0].TrimEnd('\r');
        if (line.Length > 60) line = line[..60] + "…";
        if (content.Contains('\n')) line += " ↵";
        return line;
    }
}
