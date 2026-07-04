using System.IO;
using System.Text.Json;
using MultiCopy.Native;

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
            return dto;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsStorage] 加载失败，返回默认: {ex.Message}");
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            return Default;
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
    }
}
