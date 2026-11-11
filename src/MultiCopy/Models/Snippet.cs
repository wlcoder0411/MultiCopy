namespace MultiCopy.Models;

/// <summary>
/// 片段库条目：长期复用的常用语句（与队列/置顶项完全独立）。
/// IsSecret=true 时 UI 以 ●●●● 打码显示，搜索仍按真实内容匹配，粘贴用真实内容。
/// </summary>
public sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>备注名（可空；为空时列表直接显示内容预览）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>片段内容（多行，粘贴时原样输出，不追加前后缀/序号）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>隐藏内容（如密码）：预览打码显示。</summary>
    public bool IsSecret { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最近一次粘贴使用时间（用于片段排序：最近用过的优先）。</summary>
    public DateTime? LastUsedAt { get; set; }
}
