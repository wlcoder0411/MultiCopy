using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiCopy.Models;

/// <summary>
/// 队列中的一项复制内容（抽象基类）。公共字段 Id / SourceApp / CreatedAt / Preview / IsPinned。
/// TextClipboardItem 承载文本，ImageClipboardItem 承载图片（后续 Phase 实现）。
/// IsPinned 可变（驱动 UI 图钉状态），其余创建后不变。
/// </summary>
public abstract partial class ClipboardItem : ObservableObject
{
    /// <summary>唯一标识，用于 UI 跟踪与置顶 pending 标记。</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>来源应用窗口标题（复制/截图时抓取）。</summary>
    public string SourceApp { get; }

    /// <summary>入队时间。</summary>
    public DateTime CreatedAt { get; }

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>单行预览。子类在构造函数体中赋值。</summary>
    public string Preview { get; protected set; } = string.Empty;

    protected ClipboardItem(string sourceApp, DateTime? createdAt)
    {
        SourceApp = sourceApp ?? string.Empty;
        CreatedAt = createdAt ?? DateTime.Now;
    }
}

/// <summary>
/// 文本剪贴板项。Text 创建后不变。
/// </summary>
public sealed partial class TextClipboardItem : ClipboardItem
{
    /// <summary>完整文本内容。</summary>
    public string Text { get; }

    public TextClipboardItem(string text, string sourceApp, DateTime? createdAt = null)
        : base(sourceApp, createdAt)
    {
        Text = text ?? string.Empty;
        Preview = BuildPreview(Text);
    }

    private static string BuildPreview(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        if (string.IsNullOrEmpty(oneLine)) return "(空)";
        return oneLine.Length <= 80 ? oneLine : oneLine.Substring(0, 80) + "…";
    }
}

/// <summary>
/// 图片剪贴板项。承载截图/复制的图片数据。
/// Image 为深拷贝并 Freeze 的 BitmapSource（跨线程安全）。
/// Thumbnail 为 96×96 缩略图（入队时一次性生成并 Freeze，供 UI 绑定，禁止实时解码）。
/// ImageHash 为像素哈希，用于去重和持久化指纹。
/// </summary>
public sealed partial class ImageClipboardItem : ClipboardItem
{
    /// <summary>原始图片（已 Freeze，跨线程安全）。</summary>
    public BitmapSource Image { get; }

    /// <summary>96×96 缩略图（已 Freeze，供 UI 绑定）。</summary>
    public BitmapSource Thumbnail { get; }

    /// <summary>图片宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>图片高度（像素）。</summary>
    public int Height { get; }

    /// <summary>像素哈希，用于去重和持久化指纹。</summary>
    public string ImageHash { get; }

    public ImageClipboardItem(BitmapSource image, string sourceApp, DateTime? createdAt = null)
        : base(sourceApp, createdAt)
    {
        Image = FreezeCopy(image);
        Width = Image.PixelWidth;
        Height = Image.PixelHeight;
        Thumbnail = BuildThumbnail(Image);
        ImageHash = ComputeHash(Image);
        Preview = $"截图 {Width}x{Height}";
    }

    /// <summary>深拷贝 BitmapSource 并 Freeze（跨线程安全）。</summary>
    private static BitmapSource FreezeCopy(BitmapSource source)
    {
        if (source.IsFrozen) return source;
        var copy = new WriteableBitmap(source);
        copy.Freeze();
        return copy;
    }

    /// <summary>生成 96×96 缩略图（保持宽高比，居中裁剪后缩放）。</summary>
    private static BitmapSource BuildThumbnail(BitmapSource image)
    {
        const int thumbSize = 96;
        int srcW = image.PixelWidth;
        int srcH = image.PixelHeight;

        // 居中正方形裁剪
        int cropSize = Math.Min(srcW, srcH);
        int cropX = (srcW - cropSize) / 2;
        int cropY = (srcH - cropSize) / 2;

        var cropped = new CroppedBitmap(image, new Int32Rect(cropX, cropY, cropSize, cropSize));
        var scaled = new TransformedBitmap(cropped,
            new ScaleTransform((double)thumbSize / cropSize, (double)thumbSize / cropSize));
        var writable = new WriteableBitmap(scaled);
        writable.Freeze();
        return writable;
    }

    /// <summary>计算像素哈希（SHA256 over pixel bytes）。</summary>
    private static string ComputeHash(BitmapSource image)
    {
        // 转为 Bgra32 提取像素字节
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var writable = new WriteableBitmap(converted);
        int stride = writable.PixelWidth * 4;
        byte[] pixels = new byte[stride * writable.PixelHeight];
        writable.CopyPixels(pixels, stride, 0);

        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(pixels));
    }
}
