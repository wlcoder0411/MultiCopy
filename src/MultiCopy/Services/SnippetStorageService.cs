using System.IO;
using System.Text.Json;
using MultiCopy.Models;

namespace MultiCopy.Services;

/// <summary>
/// 片段库持久化服务：List&lt;Snippet&gt; 存储到 %APPDATA%\MultiCopy\snippets.json。
/// 照搬 SettingsStorageService 的容错与原子写入策略：
/// 文件不存在→空列表；损坏→删除重建返回空列表；保存用 .tmp + File.Replace 原子替换。
/// 注意：按用户决定，片段文件明文存储（与 pinned.json 一致），README 提示不要存敏感内容。
/// </summary>
public static class SnippetStorageService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MultiCopy");

    private static readonly string FilePath = Path.Combine(AppDataDir, "snippets.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>加载片段库。文件不存在或损坏返回空列表（不抛异常）。</summary>
    public static List<Snippet> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Snippet>();
            string json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize<List<Snippet>>(json, JsonOptions);
            if (list == null) return new List<Snippet>();
            // 兜底归一化：字段缺失/非法的条目剔除，其余保留
            list.RemoveAll(s => s == null || string.IsNullOrEmpty(s.Content));
            return list;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnippetStorage] 加载失败，将清空重建: {ex.Message}");
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* 忽略删除失败 */ }
            return new List<Snippet>();
        }
    }

    /// <summary>保存片段库到磁盘（原子写入）。失败不阻塞主流程（内存态仍生效）。</summary>
    public static void Save(IReadOnlyList<Snippet> snippets)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(snippets, JsonOptions);
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
            else File.Move(tempPath, FilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SnippetStorage] 保存失败: {ex.Message}");
        }
    }
}
