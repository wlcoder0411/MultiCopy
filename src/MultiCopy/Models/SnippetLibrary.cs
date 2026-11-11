using MultiCopy.Services;

namespace MultiCopy.Models;

/// <summary>
/// 片段库：长期复用的常用语句集合（与队列/置顶完全独立的第三个数据集）。
/// 不参与剪贴板采集与去重，仅在用户主动操作（新增/编辑/删除/使用）时变更。
/// 变更即时持久化到 snippets.json 并触发 Changed；MarkUsed 只更新时间戳并落盘，
/// 不触发 Changed（不打乱当前列表）。
/// 所有成员仅在 UI 线程访问（与 ClipboardQueue 同一约定），无需锁。
/// </summary>
public sealed class SnippetLibrary
{
    private readonly List<Snippet> _snippets;

    /// <summary>片段集合变化（新增/编辑/删除），供面板与速取窗刷新。</summary>
    public event EventHandler? Changed;

    public SnippetLibrary(List<Snippet> initial) => _snippets = initial;

    /// <summary>当前全部片段（插入顺序）。</summary>
    public IReadOnlyList<Snippet> Snippets => _snippets;

    public int Count => _snippets.Count;

    public Snippet Add(string name, string content, bool isSecret)
    {
        var snippet = new Snippet { Name = name, Content = content, IsSecret = isSecret };
        _snippets.Add(snippet);
        SaveAndNotify();
        return snippet;
    }

    public void Update(string id, string name, string content, bool isSecret)
    {
        var snippet = _snippets.FirstOrDefault(x => x.Id == id);
        if (snippet == null) return;
        snippet.Name = name;
        snippet.Content = content;
        snippet.IsSecret = isSecret;
        SaveAndNotify();
    }

    public void Remove(string id)
    {
        if (_snippets.RemoveAll(x => x.Id == id) == 0) return;
        SaveAndNotify();
    }

    /// <summary>记录一次粘贴使用（更新 LastUsedAt 并落盘；不触发 Changed，下次过滤/打开时按最近使用排序）。</summary>
    public void MarkUsed(string id)
    {
        var snippet = _snippets.FirstOrDefault(x => x.Id == id);
        if (snippet == null) return;
        snippet.LastUsedAt = DateTime.Now;
        SnippetStorageService.Save(_snippets);
    }

    /// <summary>
    /// 按关键字过滤（匹配名称与内容，不区分大小写；空关键字返回全部）。
    /// 结果按「最近活动」排序：最近粘贴过的优先，从未使用过的按创建时间倒序，
    /// 让常用片段在速取窗里自然靠前，减少翻找；LINQ 排序稳定，同分保持插入顺序。
    /// </summary>
    public IEnumerable<Snippet> Search(string? keyword)
    {
        IEnumerable<Snippet> source = _snippets;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            source = source.Where(s =>
                s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                s.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        return source.OrderByDescending(s => s.LastUsedAt ?? s.CreatedAt);
    }

    private void SaveAndNotify()
    {
        SnippetStorageService.Save(_snippets);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
