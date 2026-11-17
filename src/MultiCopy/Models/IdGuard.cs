using System.IO;

namespace MultiCopy.Models;

/// <summary>
/// 持久化输入的安全校验（Id 与图片文件名）。
///
/// 为什么需要：<see cref="ClipboardItem.Id"/> 会被拼进图片文件名
/// （<c>images\{Id}.png</c>）。虽然 json 在本机，但它是可被手工编辑的明文文件，
/// 一旦写入 <c>..\..\x</c> 或含路径分隔符的值，保存时就会在 images 目录之外落盘。
/// 只接受字母、数字、<c>-</c>、<c>_</c>，长度 1–64（现有 Id 为 32 位十六进制 Guid）。
/// </summary>
internal static class IdGuard
{
    private const int MaxLength = 64;

    public static bool IsSafe(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxLength) return false;
        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') return false;
        }
        return true;
    }

    /// <summary>
    /// 校验持久化记录里的图片文件名：必须是 images 目录内的纯文件名
    /// （拒绝路径分隔符、非法字符与 <c>..</c> 这类相对路径段）。
    /// </summary>
    public static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
    }
}
