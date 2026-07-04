using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiCopy.Native;
using MultiCopy.State;

namespace MultiCopy.Services;

/// <summary>
/// 纯 Win32 剪贴板读写封装。
/// 写操作会前置 <see cref="AppState.IsWritingClipboard"/> 以防自身回环。
/// 所有方法须在 UI 线程调用（与监听器/钩子同线程，避免剪贴板锁竞争）。
/// </summary>
public sealed class ClipboardService
{
    private readonly AppState _state = AppState.Instance;

    /// <summary>
    /// 同步写入纯文本到剪贴板，含重试与超时（默认 200ms，远低于低级钩子 300ms 限制）。
    /// 成功返回 true 且系统接管 hGlobal（勿 Free）；超时返回 false。
    /// </summary>
    public bool SetClipboardTextSync(string text, int timeoutMs = 200)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!Win32.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(5);
                continue;
            }

            try
            {
                // 防回环：EmptyClipboard 成功后必触发 WM_CLIPBOARDUPDATE，置位让
                // ClipboardListenerService.WndProc 收到时丢弃该事件（由其清位）。
                // 必须在 EmptyClipboard 成功后才置位——失败时不产生 WM_CLIPBOARDUPDATE，
                // 标志将永远无法被清位，下一次真实剪贴板事件会被误判为自身回环而丢弃。
                if (!Win32.EmptyClipboard())
                    return false;
                _state.IsWritingClipboard = true;

                int byteCount = (text.Length + 1) * 2; // Unicode + 终止符
                IntPtr hGlobal = Win32.GlobalAlloc(Constants.GMEM_MOVEABLE, (IntPtr)byteCount);
                if (hGlobal == IntPtr.Zero)
                    return false;

                IntPtr ptr = Win32.GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero)
                {
                    Win32.GlobalFree(hGlobal);
                    return false;
                }

                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                // 写入 UTF-16 终止符
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
                Win32.GlobalUnlock(hGlobal);

                IntPtr result = Win32.SetClipboardData(Constants.CF_UNICODETEXT, hGlobal);
                if (result == IntPtr.Zero)
                {
                    // 写入失败，自己释放内存
                    Win32.GlobalFree(hGlobal);
                    return false;
                }
                // 成功：系统接管 hGlobal，不要 Free
                return true;
            }
            finally
            {
                Win32.CloseClipboard();
            }
        }
        return false;
    }

    /// <summary>
    /// 读取剪贴板纯文本（CF_UNICODETEXT）。无可读文本返回 null。
    /// 不修改剪贴板，不触发防回环标志。
    /// </summary>
    public string? GetClipboardText()
    {
        if (!Win32.IsClipboardFormatAvailable(Constants.CF_UNICODETEXT))
            return null;

        if (!Win32.OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            IntPtr hData = Win32.GetClipboardData(Constants.CF_UNICODETEXT);
            if (hData == IntPtr.Zero) return null;

            IntPtr ptr = Win32.GlobalLock(hData);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Win32.GlobalUnlock(hData);
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    /// <summary>
    /// 清空剪贴板（不写入任何数据）。不触发防回环标志（不是写入操作）。
    /// 用于图片写入失败时清除上一条内容，避免目标应用粘出过期文本。
    /// </summary>
    public void ClearClipboard()
    {
        if (!Win32.OpenClipboard(IntPtr.Zero)) return;
        try { Win32.EmptyClipboard(); }
        finally { Win32.CloseClipboard(); }
    }

    // ---------- 图片读写 ----------

    /// <summary>
    /// 读取剪贴板图片。按 CF_DIBV5 &gt; PNG &gt; CF_DIB 优先级。
    /// 返回 (BitmapSource, width, height) 或 null。不修改剪贴板，不触发防回环标志。
    /// 返回的 BitmapSource 已 Freeze（跨线程安全）。
    /// </summary>
    public (BitmapSource image, int width, int height)? GetClipboardImage()
    {
        // 优先级：DIBV5 > PNG > DIB
        uint[] formats = { Constants.CF_DIBV5, Constants.PngFormatId, Constants.CF_DIB };
        foreach (uint fmt in formats)
        {
            if (!Win32.IsClipboardFormatAvailable(fmt)) continue;
            var result = TryReadImage(fmt);
            if (result != null) return result;
        }
        return null;
    }

    private (BitmapSource image, int width, int height)? TryReadImage(uint format)
    {
        if (!Win32.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr hData = Win32.GetClipboardData(format);
            if (hData == IntPtr.Zero) return null;
            IntPtr ptr = Win32.GlobalLock(hData);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                // GlobalSize 接收 HGLOBAL 句柄（hData），非锁定指针
                IntPtr sizePtr = Win32.GlobalSize(hData);
                if (sizePtr == IntPtr.Zero) return null;
                int size = (int)sizePtr;
                byte[] bytes = new byte[size];
                Marshal.Copy(ptr, bytes, 0, size);

                if (format == Constants.PngFormatId)
                {
                    // PNG 字节流：直接解码
                    var image = DecodePng(bytes);
                    if (image != null) return (image, image.PixelWidth, image.PixelHeight);
                }
                else
                {
                    // CF_DIB / CF_DIBV5：DIB 字节流（BITMAPINFOHEADER + 像素，可能含调色板）
                    var image = DecodeDib(bytes);
                    if (image != null) return (image, image.PixelWidth, image.PixelHeight);
                }
            }
            finally
            {
                Win32.GlobalUnlock(hData);
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
        return null;
    }

    /// <summary>从 PNG 字节流解码为已 Freeze 的 BitmapSource。</summary>
    private static BitmapSource? DecodePng(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 DIB 字节流（CF_DIB / CF_DIBV5）。前置 BITMAPFILEHEADER 拼成完整 BMP 后用 BitmapDecoder 解码。
    /// DIB = BITMAPINFOHEADER(+变长) + 可选调色板 + 像素。biSize 在 offset 0 决定头部长度，
    /// biBitCount 在 offset 14 决定调色板大小。返回已 Freeze 的 BitmapSource。
    /// </summary>
    private static BitmapSource? DecodeDib(byte[] dibBytes)
    {
        try
        {
            if (dibBytes.Length < 40) return null;
            int biSize = BitConverter.ToInt32(dibBytes, 0);
            if (biSize <= 0 || biSize > dibBytes.Length) return null;
            ushort bitCount = BitConverter.ToUInt16(dibBytes, 14);

            // 构造 BITMAPFILEHEADER(14) + DIB 字节，用 BitmapDecoder 解码
            // BITMAPFILEHEADER: 'BM'(2) + bfSize(4) + 保留(4,全零) + bfOffBits(4)
            // 调色板仅 biBitCount<=8 时存在，每项 4 字节
            int paletteSize = bitCount <= 8 ? (1 << bitCount) * 4 : 0;
            int offset = 14 + biSize + paletteSize;
            int fileSize = 14 + dibBytes.Length;

            var fileBytes = new byte[14 + dibBytes.Length];
            fileBytes[0] = (byte)'B';
            fileBytes[1] = (byte)'M';
            BitConverter.GetBytes(fileSize).CopyTo(fileBytes, 2);
            // offset 6-9 保留位保持 0（数组已初始化）
            BitConverter.GetBytes(offset).CopyTo(fileBytes, 10);
            Array.Copy(dibBytes, 0, fileBytes, 14, dibBytes.Length);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(fileBytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 同步写入图片到剪贴板，含重试与超时（默认 250ms）。
    /// 同时写 PNG 与 CF_DIB 两种格式以兼容不同粘贴目标。
    /// 成功返回 true 且系统接管 hGlobal（勿 Free）；超时返回 false。
    /// 在 EmptyClipboard 成功后才置位 IsWritingClipboard（沿用已修复约定）。
    /// </summary>
    public bool SetClipboardImageSync(BitmapSource image, int timeoutMs = 250)
    {
        // 预编码 PNG 和 DIB 字节流（剪贴板外，无锁）
        byte[]? pngBytes = EncodePng(image);
        byte[]? dibBytes = EncodeDib(image);
        if (pngBytes == null || dibBytes == null) return false;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!Win32.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(5);
                continue;
            }
            try
            {
                // 防回环：EmptyClipboard 成功后才置位（失败不产生 WM_CLIPBOARDUPDATE，置位将无法清位）
                if (!Win32.EmptyClipboard())
                    return false;
                _state.IsWritingClipboard = true;

                // 写 PNG：失败则自己释放内存
                IntPtr hPng = WriteDataToClipboard(pngBytes);
                if (hPng != IntPtr.Zero)
                {
                    if (Win32.SetClipboardData(Constants.PngFormatId, hPng) == IntPtr.Zero)
                        Win32.GlobalFree(hPng);
                    // 成功：系统接管 hPng，不要 Free
                }

                // 写 CF_DIB：失败则自己释放内存
                IntPtr hDib = WriteDataToClipboard(dibBytes);
                if (hDib != IntPtr.Zero)
                {
                    if (Win32.SetClipboardData(Constants.CF_DIB, hDib) == IntPtr.Zero)
                        Win32.GlobalFree(hDib);
                    // 成功：系统接管 hDib，不要 Free
                }

                return true;
            }
            finally
            {
                Win32.CloseClipboard();
            }
        }
        return false;
    }

    /// <summary>
    /// 分配全局内存并拷贝数据。返回 hGlobal（调用方负责在 SetClipboardData 失败时 GlobalFree）。
    /// </summary>
    private static IntPtr WriteDataToClipboard(byte[] data)
    {
        IntPtr hGlobal = Win32.GlobalAlloc(Constants.GMEM_MOVEABLE, (IntPtr)data.Length);
        if (hGlobal == IntPtr.Zero) return IntPtr.Zero;
        IntPtr ptr = Win32.GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            Win32.GlobalFree(hGlobal);
            return IntPtr.Zero;
        }
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            Win32.GlobalUnlock(hGlobal);
        }
        return hGlobal; // 系统接管，调用方成功后不要 Free
    }

    /// <summary>编码 BitmapSource 为 PNG 字节流。</summary>
    private static byte[]? EncodePng(BitmapSource image)
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
    /// 编码 BitmapSource 为 CF_DIB 字节流（BITMAPINFOHEADER + 像素，bottom-up）。
    /// 转 Bgra32 提取像素并按行翻转（DIB 像素自下而上存储）。
    /// </summary>
    private static byte[]? EncodeDib(BitmapSource image)
    {
        try
        {
            // 转 Bgra32（含 alpha，32 位）
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            // DIB 像素是 bottom-up，需要翻转行
            byte[] flipped = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
            }

            // BITMAPINFOHEADER (40 字节) + 像素
            var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(40);              // biSize
            bw.Write(width);           // biWidth
            bw.Write(height);          // biHeight（正数=bottom-up）
            bw.Write((ushort)1);       // biPlanes
            bw.Write((ushort)32);      // biBitCount
            bw.Write(0);               // biCompression = BI_RGB
            bw.Write(pixels.Length);   // biSizeImage
            bw.Write(0);               // biXPelsPerMeter
            bw.Write(0);               // biYPelsPerMeter
            bw.Write(0);               // biClrUsed
            bw.Write(0);               // biClrImportant
            bw.Write(flipped, 0, flipped.Length);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
