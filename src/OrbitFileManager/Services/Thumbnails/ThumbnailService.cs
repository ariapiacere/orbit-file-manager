using OrbitFileManager.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OrbitFileManager.Services.Thumbnails;

public sealed class ThumbnailService
{
    private const int MaxCacheEntries = 512;
    private readonly ConcurrentDictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrencyGate = new(4, 4);
    private int _cacheGeneration;

    private static readonly HashSet<string> SupportedThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".ico",
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".pdf"
    };

    public async Task PopulateThumbnailsForTabAsync(IReadOnlyList<FileEntryItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (item.IsDirectory || item.Thumbnail is not null || !SupportedThumbnailExtensions.Contains(item.Extension))
                continue;

            await _concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                BitmapSource? bmp = await Task.Run(() => GetOrCreateThumbnail(item.FullPath, 128), cancellationToken);
                if (bmp is not null && !cancellationToken.IsCancellationRequested)
                {
                    if (Application.Current?.Dispatcher is { } disp)
                        await disp.InvokeAsync(() => item.Thumbnail = bmp);
                    else
                        item.Thumbnail = bmp;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _concurrencyGate.Release();
            }
        }
    }

    public BitmapSource? GetOrCreateThumbnail(string fullPath, int pixelSize = 128)
    {
        int generation = Volatile.Read(ref _cacheGeneration);
        string key;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
                return null;
            key = $"{generation}|{pixelSize}|{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        if (_cache.Count > MaxCacheEntries)
            _cache.Clear();

        BitmapSource? generated = TryExtractShellImage(fullPath, pixelSize) ?? TryDecodeWicThumbnail(fullPath, pixelSize);
        _cache[key] = generated;
        return generated;
    }

    public void ClearCache()
    {
        // In-flight loads from before a replacement must not repopulate the current cache.
        Interlocked.Increment(ref _cacheGeneration);
        _cache.Clear();
    }

    public static BitmapSource? TryExtractShellImage(string fullPath, int size, bool thumbnailOnly = true)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        Guid iid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b"); // IShellItemImageFactory
        IntPtr factoryPtr = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IShellItemImageFactory? factory = null;

        try
        {
            int hr = SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref iid, out factoryPtr);
            if (hr != 0 || factoryPtr == IntPtr.Zero)
                return null;

            factory = (IShellItemImageFactory)Marshal.GetTypedObjectForIUnknown(factoryPtr, typeof(IShellItemImageFactory));
            SIIGBF flags = SIIGBF.SIIGBF_BIGGERSIZEOK;
            if (thumbnailOnly)
                flags |= SIIGBF.SIIGBF_THUMBNAILONLY;

            int imgHr = factory.GetImage(new SIZE(size, size), flags, out hBitmap);
            if (imgHr != 0 || hBitmap == IntPtr.Zero)
                return null;

            BitmapSource bmp = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (factory is not null)
                Marshal.ReleaseComObject(factory);
            if (factoryPtr != IntPtr.Zero)
                Marshal.Release(factoryPtr);
        }
    }

    private static BitmapSource? TryDecodeWicThumbnail(string fullPath, int decodeWidth)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists || fi.Length == 0 || fi.Length > 40L * 1024 * 1024)
                return null;

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = decodeWidth;
            img.StreamSource = stream;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SIZE(int cx, int cy)
    {
        public readonly int cx = cx;
        public readonly int cy = cy;
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
