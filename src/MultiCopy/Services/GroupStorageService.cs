using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using MultiCopy.Models;

namespace MultiCopy.Services;

/// <summary>
/// 分组列表持久化服务。
/// 将 Groups 序列化为 JSON 存储到 %APPDATA%\MultiCopy\groups.json，
/// 组内图片项独立存为 %APPDATA%\MultiCopy\images\&lt;item.Id&gt;.png（与 PinnedStorageService 共享 images 目录）。
/// 应用启动时加载、分组变化时即时保存。
///
/// 设计原理（与 PinnedStorageService 对齐）：
/// - 分组是用户偏好数据（含置顶标识、组内项），应跨会话存活。
/// - 多态 ItemDto：文本项存 Text，图片项存 ImageFile/Width/Height/Hash，用 [JsonPolymorphic] 区分。
/// - GroupDto 承载分组元数据（Id/Name/IsPinned/IsExpanded/CreatedAt/PinnedAt）+ Items 列表。
/// - 图片文件用原子写（.tmp + File.Replace）；索引文件同样原子写。
/// - Load 时图片文件缺失则跳过该项（索引在下次 Save 时被自然清理）。
/// - 不在 Save 时清理孤儿图片（与 PinnedStorageService 协调：由 App.OnStartup 统一清理，避免互相误删）。
/// - 文件损坏时：清空重建，不让用户卡在损坏数据上。
/// </summary>
public static class GroupStorageService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MultiCopy");

    private static readonly string FilePath = Path.Combine(AppDataDir, "groups.json");

    /// <summary>图片文件目录（与 PinnedStorageService 共享）。</summary>
    private static readonly string ImagesDir = Path.Combine(AppDataDir, "images");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>加载持久化的分组列表。文件不存在或损坏返回空列表（不抛异常）。</summary>
    public static List<ClipboardGroup> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<ClipboardGroup>();

            string json = File.ReadAllText(FilePath);
            var dtos = JsonSerializer.Deserialize<List<GroupDto>>(json, JsonOptions);
            if (dtos == null) return new List<ClipboardGroup>();

            var groups = new List<ClipboardGroup>(dtos.Count);
            foreach (var gDto in dtos)
            {
                if (string.IsNullOrEmpty(gDto.Name)) continue;

                // 用 DTO 的 Id / CreatedAt 恢复分组身份（保持 Id 跨会话稳定，便于活动分组引用）
                var g = new ClipboardGroup(gDto.Name);
                g.RestoreIdentity(gDto.Id, gDto.CreatedAt);
                g.IsExpanded = gDto.IsExpanded;
                g.IsPinned = gDto.IsPinned;
                g.PinnedAt = gDto.PinnedAt;

                foreach (var itemDto in gDto.Items)
                {
                    switch (itemDto)
                    {
                        case TextItemDto textDto:
                            if (string.IsNullOrEmpty(textDto.Text)) continue;
                            var textItem = new TextClipboardItem(textDto.Text, textDto.SourceApp ?? string.Empty, textDto.CreatedAt);
                            textItem.RestoreId(textDto.Id);
                            g.Items.Add(textItem);
                            break;

                        case ImageItemDto imgDto:
                            // 文件名来自不可信 json：必须是 images 目录内的纯文件名，否则跳过
                            if (!IdGuard.IsSafeFileName(imgDto.ImageFile)) continue;
                            string imgPath = Path.Combine(ImagesDir, imgDto.ImageFile);
                            if (!File.Exists(imgPath)) continue;
                            try
                            {
                                var bitmap = LoadImageFromFile(imgPath);
                                if (bitmap == null) continue;
                                var imgItem = new ImageClipboardItem(bitmap, imgDto.SourceApp ?? string.Empty, imgDto.CreatedAt);
                                // 与 PinnedStorageService 同约定：Id 是 PNG 文件名外键，必须还原；
                                // 旧版 DTO 无 id 字段（或 id 非法）时从文件名反推（文件名即上次保存时的 Id）
                                imgItem.RestoreId(IdGuard.IsSafe(imgDto.Id)
                                    ? imgDto.Id!
                                    : Path.GetFileNameWithoutExtension(imgDto.ImageFile));
                                g.Items.Add(imgItem);
                            }
                            catch { continue; }
                            break;
                    }
                }

                groups.Add(g);
            }

            return groups;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GroupStorage] 加载失败，将清空重建: {ex.Message}");
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* 忽略删除失败 */ }
            return new List<ClipboardGroup>();
        }
    }

    /// <summary>保存分组列表到磁盘。原子写入索引 + 同步管理图片文件（不清理孤儿）。</summary>
    public static void Save(IEnumerable<ClipboardGroup> groups)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(ImagesDir);

            var dtos = new List<GroupDto>();

            foreach (var g in groups)
            {
                var gDto = new GroupDto
                {
                    Id = g.Id,
                    Name = g.Name,
                    IsPinned = g.IsPinned,
                    IsExpanded = g.IsExpanded,
                    CreatedAt = g.CreatedAt,
                    PinnedAt = g.PinnedAt,
                    Items = new List<ItemDto>()
                };

                foreach (var item in g.Items)
                {
                    switch (item)
                    {
                        case TextClipboardItem textItem:
                            gDto.Items.Add(new TextItemDto
                            {
                                Id = textItem.Id,
                                Text = textItem.Text,
                                SourceApp = textItem.SourceApp,
                                CreatedAt = textItem.CreatedAt,
                            });
                            break;

                        case ImageClipboardItem imgItem:
                            string imgFileName = $"{imgItem.Id}.png";
                            string imgPath = Path.Combine(ImagesDir, imgFileName);
                            if (!File.Exists(imgPath))
                            {
                                SaveImageToFile(imgItem.Image, imgPath);
                            }
                            gDto.Items.Add(new ImageItemDto
                            {
                                Id = imgItem.Id,
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

                dtos.Add(gDto);
            }

            string json = JsonSerializer.Serialize(dtos, JsonOptions);
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
            else File.Move(tempPath, FilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GroupStorage] 保存失败: {ex.Message}");
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

    // ---------- DTO ----------

    private sealed class GroupDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsPinned { get; set; }
        public bool IsExpanded { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime? PinnedAt { get; set; }
        public List<ItemDto> Items { get; set; } = new();
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(TextItemDto), "text")]
    [JsonDerivedType(typeof(ImageItemDto), "image")]
    private abstract class ItemDto
    {
        /// <summary>条目 Id（图片文件名的外键），必须落盘并在加载时还原。</summary>
        public string? Id { get; set; }
        public string? SourceApp { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class TextItemDto : ItemDto
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class ImageItemDto : ItemDto
    {
        public string ImageFile { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public string Hash { get; set; } = string.Empty;
    }
}
