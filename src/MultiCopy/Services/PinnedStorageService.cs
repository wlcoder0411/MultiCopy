using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using MultiCopy.Models;

namespace MultiCopy.Services;

/// <summary>
/// 置顶项持久化服务。
/// 将 PinnedItems 序列化为 JSON 存储到 %APPDATA%\MultiCopy\pinned.json，
/// 图片项独立存为 %APPDATA%\MultiCopy\images\&lt;item.Id&gt;.png，索引只存文件名。
/// 应用启动时加载、置顶操作时即时保存。
///
/// 设计原理：
/// - 置顶项是"用户偏好数据"（高频复用内容），应跨会话存活；普通队列项是临时数据，不持久化。
/// - 多态 DTO：文本项存 Text，图片项存 ImageFile/Width/Height/Hash，用 [JsonPolymorphic] 区分。
/// - 图片文件用原子写（.tmp + File.Replace）；索引文件同样原子写。
/// - Load 时图片文件缺失则跳过该项（索引在下次 Save 时被自然清理）。
/// - Save 时清理孤儿图片文件（不在当前 PinnedItems 中的）。
/// - 文件损坏时：记录日志 + 清空重建，不让用户卡在损坏数据上。
/// - 存储路径用 Environment.SpecialFolder.ApplicationData（per-user，无权限问题）。
/// </summary>
public static class PinnedStorageService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MultiCopy");

    private static readonly string FilePath = Path.Combine(AppDataDir, "pinned.json");

    /// <summary>图片文件目录：%APPDATA%\MultiCopy\images\</summary>
    private static readonly string ImagesDir = Path.Combine(AppDataDir, "images");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>加载持久化的置顶项。文件不存在或损坏返回空列表（不抛异常）。</summary>
    public static List<ClipboardItem> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<ClipboardItem>();

            string json = File.ReadAllText(FilePath);
            var dtos = JsonSerializer.Deserialize<List<PinnedItemDto>>(json, JsonOptions);
            if (dtos == null) return new List<ClipboardItem>();

            var items = new List<ClipboardItem>(dtos.Count);
            var validImageFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dto in dtos)
            {
                switch (dto)
                {
                    case TextPinnedItemDto textDto:
                        if (string.IsNullOrEmpty(textDto.Text)) continue; // 跳过空文本项
                        var textItem = new TextClipboardItem(textDto.Text, textDto.SourceApp ?? string.Empty, textDto.CreatedAt);
                        textItem.IsPinned = true;
                        items.Add(textItem);
                        break;

                    case ImagePinnedItemDto imgDto:
                        if (string.IsNullOrEmpty(imgDto.ImageFile)) continue;
                        string imgPath = Path.Combine(ImagesDir, imgDto.ImageFile);
                        if (!File.Exists(imgPath))
                        {
                            // 文件缺失：跳过该项（索引在保存时会被自然清理，因为 items 不含它）
                            continue;
                        }
                        try
                        {
                            var bitmap = LoadImageFromFile(imgPath);
                            if (bitmap == null) continue;
                            // ImageClipboardItem 构造时算的哈希应与持久化的哈希一致（同图同哈希）
                            var imgItem = new ImageClipboardItem(bitmap, imgDto.SourceApp ?? string.Empty, imgDto.CreatedAt);
                            imgItem.IsPinned = true;
                            items.Add(imgItem);
                            validImageFiles.Add(imgDto.ImageFile);
                        }
                        catch { continue; }
                        break;
                }
            }

            // 启动期清理孤儿图片文件（索引中引用但文件缺失的会在下次 Save 时被清出索引；
            // 索引中未引用但磁盘上存在的孤儿图片在此清理）
            CleanupOrphanImages(validImageFiles);

            return items;
        }
        catch (Exception ex)
        {
            // 文件损坏或反序列化失败：清空重建，不让用户卡住
            System.Diagnostics.Debug.WriteLine($"[PinnedStorage] 加载失败，将清空重建: {ex.Message}");
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* 忽略删除失败 */ }
            return new List<ClipboardItem>();
        }
    }

    /// <summary>保存置顶项到磁盘。原子写入索引 + 同步管理图片文件 + 清理孤儿。</summary>
    public static void Save(IEnumerable<ClipboardItem> pinnedItems)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(ImagesDir);

            var dtos = new List<PinnedItemDto>();
            var usedImageFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in pinnedItems)
            {
                switch (item)
                {
                    case TextClipboardItem textItem:
                        dtos.Add(new TextPinnedItemDto
                        {
                            Text = textItem.Text,
                            SourceApp = textItem.SourceApp,
                            CreatedAt = textItem.CreatedAt,
                        });
                        break;

                    case ImageClipboardItem imgItem:
                        string imgFileName = $"{imgItem.Id}.png";
                        string imgPath = Path.Combine(ImagesDir, imgFileName);
                        // 写盘（仅当文件不存在时写，避免重复 IO；原子写）
                        if (!File.Exists(imgPath))
                        {
                            SaveImageToFile(imgItem.Image, imgPath);
                        }
                        usedImageFiles.Add(imgFileName);
                        dtos.Add(new ImagePinnedItemDto
                        {
                            ImageFile = imgFileName,
                            Width = imgItem.Width,
                            Height = imgItem.Height,
                            Hash = imgItem.ImageHash,
                            SourceApp = imgItem.SourceApp,
                            CreatedAt = imgItem.CreatedAt,
                        });
                        break;
                }
            }

            // 序列化索引（原子写）
            string json = JsonSerializer.Serialize(dtos, JsonOptions);
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
            else File.Move(tempPath, FilePath);

            // 清理孤儿图片文件（不在 usedImageFiles 中的）
            CleanupOrphanImages(usedImageFiles);
        }
        catch (Exception ex)
        {
            // 保存失败不阻塞主流程（置顶操作仍生效，只是本次没持久化）
            System.Diagnostics.Debug.WriteLine($"[PinnedStorage] 保存失败: {ex.Message}");
        }
    }

    /// <summary>从文件加载图片为已 Freeze 的 BitmapSource（跨线程安全）。失败返回 null。</summary>
    private static BitmapSource? LoadImageFromFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>将图片以 PNG 格式原子写入文件（.tmp + File.Replace）。</summary>
    private static void SaveImageToFile(BitmapSource image, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            enc.Save(fs);
        }
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>清理孤儿图片文件：删除 images 目录下不在 usedImageFiles 中的 *.png。</summary>
    private static void CleanupOrphanImages(HashSet<string> usedImageFiles)
    {
        try
        {
            if (!Directory.Exists(ImagesDir)) return;
            foreach (var file in Directory.EnumerateFiles(ImagesDir, "*.png"))
            {
                string name = Path.GetFileName(file);
                if (!usedImageFiles.Contains(name))
                {
                    try { File.Delete(file); } catch { /* 忽略单个文件删除失败 */ }
                }
            }
        }
        catch { /* 忽略目录遍历失败 */ }
    }

    // ---------- 多态 DTO（.NET 8 System.Text.Json 多态序列化） ----------

    /// <summary>持久化数据传输对象基类（多态根）。</summary>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(TextPinnedItemDto), "text")]
    [JsonDerivedType(typeof(ImagePinnedItemDto), "image")]
    private abstract class PinnedItemDto
    {
        public string? SourceApp { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class TextPinnedItemDto : PinnedItemDto
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class ImagePinnedItemDto : PinnedItemDto
    {
        public string ImageFile { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public string Hash { get; set; } = string.Empty;
    }
}
