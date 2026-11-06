using OrbitFileManager.Services.Thumbnails;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class ThumbnailReplacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"OrbitThumbnailTests_{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Thumbnail_RefreshesAfterSameSizeFileIsReplaced(bool preserveTimestamp)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "frame.bmp");
        var service = new ThumbnailService();
        WriteBitmap(path, 255, 0);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1));
        var original = service.GetOrCreateThumbnail(path);
        Assert.NotNull(original);
        long size = new FileInfo(path).Length;

        WriteBitmap(path, 0, 255);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, preserveTimestamp ? 1 : 2));
        if (preserveTimestamp)
            service.ClearCache(); // transfer completion invalidates even identical metadata
        Assert.Equal(size, new FileInfo(path).Length);
        var replaced = service.GetOrCreateThumbnail(path);
        Assert.NotNull(replaced);
        Assert.NotSame(original, replaced);
        Assert.NotEqual(GetPixels(original), GetPixels(replaced));
        Assert.Same(replaced, service.GetOrCreateThumbnail(path));
    }

    private static byte[] GetPixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void WriteBitmap(string path, byte red, byte blue)
    {
        byte[] pixels = [blue, 0, red, 255];
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
