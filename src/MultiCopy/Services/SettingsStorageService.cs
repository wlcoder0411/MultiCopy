using System.IO;
using System.Text.Json;
using MultiCopy.Native;
using MultiCopy.State;

namespace MultiCopy.Services;

/// <summary>
/// 用户设置持久化服务。存储到 %APPDATA%\MultiCopy\settings.json。
/// 照搬 PinnedStorageService 的容错与原子写入策略：
/// 文件不存在→默认值；损坏→清空重建返回默认值；保存用 .tmp + File.Replace 原子替换。
/// </summary>
public static class SettingsStorageService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MultiCopy");

    private static readonly string FilePath = Path.Combine(AppDataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>默认设置：启用 + Alt+Z。</summary>
    public static SettingsDto Default => new SettingsDto
    {
        HotkeyEnabled = true,
        Modifiers = (uint)HotkeyModifierKeys.Alt, // 0x0001
        Key = 0x5A, // VK_Z
    };

    public static SettingsDto Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Default;
            string json = File.ReadAllText(FilePath);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json, JsonOptions);
            if (dto == null) return Default;
            // 合法性校验：主键不能为 0
            if (dto.Key <= 0) dto.Key = 0x5A;
            // 兜底归一化：新字段缺失时反序列化为 null
            dto.PastePrefix ??= string.Empty;
            dto.PasteSuffix ??= string.Empty;
            // 序号相关字段兜底（缺失时用默认值）
            if (dto.PrefixSequenceStart < 1) dto.PrefixSequenceStart = 1;
            if (dto.PrefixSequenceCurrent < 1) dto.PrefixSequenceCurrent = dto.PrefixSequenceStart;
            // 兼容旧版单选配置：检测到 pasteSuffixIsPostfix 字段时迁移
            MigrateLegacySettings(json, dto);
            return dto;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsStorage] 加载失败，返回默认: {ex.Message}");
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            return Default;
        }
    }

    /// <summary>
    /// 从原始 JSON 检测旧版单选字段（pasteSuffix + pasteSuffixIsPostfix），
    /// 若存在且内容非空，则迁移到新的前后缀独立模型。
    /// </summary>
    private static void MigrateLegacySettings(string json, SettingsDto dto)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("pasteSuffixIsPostfix", out var posElement) ||
                !root.TryGetProperty("pasteSuffix", out var suffixElement))
            {
                return;
            }
            string legacySuffix = suffixElement.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(legacySuffix)) return;
            bool isPostfix = posElement.GetBoolean();
            if (isPostfix)
            {
                dto.PasteSuffix = legacySuffix;
                dto.IsPasteSuffixEnabled = true;
            }
            else
            {
                dto.PastePrefix = legacySuffix;
                dto.IsPastePrefixEnabled = true;
            }
        }
        catch
        {
            // 迁移失败时保持已反序列化的新字段值，不阻断正常使用
        }
    }

    public static void Save(SettingsDto settings)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
            else File.Move(tempPath, FilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsStorage] 保存失败: {ex.Message}");
        }
    }

    /// <summary>持久化数据传输对象。</summary>
    public sealed class SettingsDto
    {
        public bool HotkeyEnabled { get; set; } = true;
        public uint Modifiers { get; set; } = (uint)HotkeyModifierKeys.Alt;
        public int Key { get; set; } = 0x5A;
        /// <summary>粘贴前缀内容（空字符串=无前缀）。默认无前缀。</summary>
        public string PastePrefix { get; set; } = string.Empty;
        /// <summary>粘贴后缀内容（空字符串=无后缀）。默认无后缀。</summary>
        public string PasteSuffix { get; set; } = string.Empty;
        /// <summary>是否启用粘贴前缀。</summary>
        public bool IsPastePrefixEnabled { get; set; }
        /// <summary>是否启用粘贴后缀。</summary>
        public bool IsPasteSuffixEnabled { get; set; }
        /// <summary>前缀序号起始值（默认 1）。</summary>
        public int PrefixSequenceStart { get; set; } = 1;
        /// <summary>前缀序号当前值（每次粘贴后递增，持久化防重启丢失）。</summary>
        public int PrefixSequenceCurrent { get; set; } = 1;
        /// <summary>是否启用前缀序号自增。</summary>
        public bool IsPrefixSequenceEnabled { get; set; }
        /// <summary>序号格式。</summary>
        public PrefixSequenceFormat PrefixSequenceFormat { get; set; } = PrefixSequenceFormat.Plain;
        /// <summary>序号与前缀内容的位置关系。</summary>
        public PrefixSequencePosition PrefixSequencePosition { get; set; } = PrefixSequencePosition.AfterPrefix;
    }
}
