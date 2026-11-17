using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MultiCopy.Services;

/// <summary>
/// 一条图片的剪贴板预编码结果。任一路为 null 表示该格式未编码（或被跳过）。
/// </summary>
internal sealed class EncodedImage
{
    /// <summary>PNG 字节流（剪贴板格式 "PNG"）。</summary>
    public byte[]? Png { get; init; }

    /// <summary>CF_DIB 字节流（BITMAPINFOHEADER + bottom-up 像素）。</summary>
    public byte[]? Dib { get; init; }

    /// <summary>两份字节流的总字节数，供缓存做容量淘汰。</summary>
    public long TotalBytes => (Png?.LongLength ?? 0) + (Dib?.LongLength ?? 0);
}

/// <summary>
/// 图片编码工具：把已 Freeze 的 BitmapSource 编码为剪贴板所需的 PNG / CF_DIB 字节流。
///
/// 为什么独立成纯函数：编码是 O(W×H) 的重活（4096² 图约 64MB 像素 + 同量临时缓冲），
/// 而低级键盘钩子回调受系统 LowLevelHooksTimeout（默认 300ms）约束，在钩子里编码会拖慢
/// 全系统输入、甚至被系统摘钩。因此必须能在后台线程预编码。
///
/// 线程安全前提：输入位图必须已 Freeze（ImageClipboardItem.Image 满足）。
/// 刻意不使用 WriteableBitmap：它在创建线程上有亲和性，后台线程构造后跨线程读取会抛异常；
/// BitmapSource.CopyPixels 在 Freeze 后可安全跨线程调用。
/// </summary>
internal static class ImageEncoding
{
    /// <summary>BITMAPINFOHEADER 固定 40 字节。</summary>
    private const int DibHeaderSize = 40;

    /// <summary>编码为 PNG 字节流。失败返回 null（不抛异常，调用方按"编码失败"降级）。</summary>
    public static byte[]? EncodePng(BitmapSource image)
    {
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 编码为 CF_DIB 字节流（BITMAPINFOHEADER + 像素，bottom-up）。
    /// 按行倒序直接写入目标缓冲：相比旧实现（pixels + flipped + MemoryStream + ToArray
    /// 四份全图副本）只保留 pixels + 目标缓冲两份，4096² 图可省约 128MB 峰值分配。
    /// </summary>
    public static byte[]? EncodeDib(BitmapSource image)
    {
        try
        {
            // 统一转 Bgra32（32 位含 alpha），与原实现一致
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;

            // 单块托管数组上限 2GB：超大图直接放弃（宁可不缓存，也不抛到钩子里）
            long pixelBytes = (long)stride * height;
            if (pixelBytes > int.MaxValue) return null;

            byte[] pixels = new byte[(int)pixelBytes];
            converted.CopyPixels(pixels, stride, 0);

            var buffer = new byte[DibHeaderSize + pixels.Length];
            BitConverter.GetBytes(DibHeaderSize).CopyTo(buffer, 0);   // biSize
            BitConverter.GetBytes(width).CopyTo(buffer, 4);            // biWidth
            BitConverter.GetBytes(height).CopyTo(buffer, 8);           // biHeight（正数=bottom-up）
            BitConverter.GetBytes((ushort)1).CopyTo(buffer, 12);       // biPlanes
            BitConverter.GetBytes((ushort)32).CopyTo(buffer, 14);      // biBitCount
            BitConverter.GetBytes(0).CopyTo(buffer, 16);               // biCompression = BI_RGB
            BitConverter.GetBytes(pixels.Length).CopyTo(buffer, 20);   // biSizeImage
            // 24..39：biXPelsPerMeter / biYPelsPerMeter / biClrUsed / biClrImportant 保持 0

            // DIB 像素自下而上存储：源第 y 行写到目标第 (height-1-y) 行
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(pixels, y * stride,
                    buffer, DibHeaderSize + (height - 1 - y) * stride, stride);
            }

            return buffer;
        }
        catch
        {
            return null;
        }
    }
}
