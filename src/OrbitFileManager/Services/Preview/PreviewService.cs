using OrbitFileManager.Models;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Thumbnails;
using OrbitFileManager.Utilities;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace OrbitFileManager.Services.Preview;

public sealed class PreviewService
{
    private const int MaxTextPreviewBytes = 64 * 1024;
    private const long MaxImageFileBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".ico"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".log", ".ini", ".cfg", ".toml", ".yaml", ".yml",
        ".cs", ".csproj", ".sln", ".props", ".targets", ".xaml", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".html", ".htm", ".css", ".scss", ".sql", ".ps1", ".bat", ".cmd", ".sh", ".gitignore", ".editorconfig"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v"
    };

    private readonly ShellIntegrationService _shell;

    public PreviewService(ShellIntegrationService shell)
    {
        _shell = shell;
    }

    public Task<FilePreviewData> LoadPreviewAsync(FileEntryItem? item, CancellationToken cancellationToken)
    {
        if (item is null)
            return Task.FromResult(new FilePreviewData { Kind = PreviewKind.None });

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (largeIcon, typeName) = _shell.GetEntryMetadata(item.FullPath, item.Extension, item.IsDirectory, largeIcon: true);

            try
            {
                if (item.IsDirectory)
                {
                    var dirInfo = new DirectoryInfo(item.FullPath);
                    string summary = BuildQuickDirectorySummary(dirInfo, cancellationToken);

                    return new FilePreviewData
                    {
                        Kind = PreviewKind.Directory,
                        Name = item.Name,
                        FullPath = item.FullPath,
                        TypeDescription = typeName,
                        SizeSummary = "Folder",
                        ExactBytesSummary = summary,
                        CreatedText = FormatDate(dirInfo.CreationTime),
                        ModifiedText = FormatDate(dirInfo.LastWriteTime),
                        AccessedText = FormatDate(dirInfo.LastAccessTime),
                        AttributesText = DescribeAttributes(dirInfo.Attributes),
                        LinkTarget = item.LinkTarget,
                        DirectorySummary = summary,
                        Icon = largeIcon ?? item.Icon
                    };
                }

                var fileInfo = new FileInfo(item.FullPath);
                if (!fileInfo.Exists)
                {
                    return new FilePreviewData
                    {
                        Kind = PreviewKind.Error,
                        Name = item.Name,
                        FullPath = item.FullPath,
                        StatusMessage = "File no longer exists."
                    };
                }

                long length = fileInfo.Length;
                string sizeFormatted = FileSizeFormatter.FormatBytes(length);
                string exactBytes = $"{length:N0} bytes";
                string created = FormatDate(fileInfo.CreationTime);
                string modified = FormatDate(fileInfo.LastWriteTime);
                string accessed = FormatDate(fileInfo.LastAccessTime);
                string attrs = DescribeAttributes(fileInfo.Attributes);

                // 1. Images
                if (ImageExtensions.Contains(item.Extension) && length > 0 && length <= MaxImageFileBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (bmp, width, height) = TryLoadNonLockingBitmap(fileInfo.FullName);
                    if (bmp is not null)
                    {
                        return new FilePreviewData
                        {
                            Kind = PreviewKind.Image,
                            Name = item.Name,
                            FullPath = item.FullPath,
                            TypeDescription = typeName,
                            SizeSummary = sizeFormatted,
                            ExactBytesSummary = exactBytes,
                            CreatedText = created,
                            ModifiedText = modified,
                            AccessedText = accessed,
                            AttributesText = attrs,
                            LinkTarget = item.LinkTarget,
                            Icon = largeIcon ?? item.Icon,
                            ImagePreview = bmp,
                            PixelWidth = width,
                            PixelHeight = height
                        };
                    }
                }

                // 2. PDF Preview (Shell Page 1 Visual + PDF Header/Page Metadata)
                if (string.Equals(item.Extension, ".pdf", StringComparison.OrdinalIgnoreCase) && length > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string pdfMeta = InspectPdfMetadata(fileInfo.FullName);
                    BitmapSource? pdfThumb = ThumbnailService.TryExtractShellImage(fileInfo.FullName, 720, thumbnailOnly: true);

                    return new FilePreviewData
                    {
                        Kind = pdfThumb is not null ? PreviewKind.Image : PreviewKind.Binary,
                        Name = item.Name,
                        FullPath = item.FullPath,
                        TypeDescription = typeName,
                        SizeSummary = sizeFormatted,
                        ExactBytesSummary = exactBytes,
                        CreatedText = created,
                        ModifiedText = modified,
                        AccessedText = accessed,
                        AttributesText = attrs,
                        LinkTarget = item.LinkTarget,
                        Icon = largeIcon ?? item.Icon,
                        ImagePreview = pdfThumb,
                        PixelWidth = pdfThumb?.PixelWidth ?? 0,
                        PixelHeight = pdfThumb?.PixelHeight ?? 0,
                        StatusMessage = pdfMeta
                    };
                }

                // 3. Video & Audio Metadata (+ Video Frame Thumbnail)
                if (VideoExtensions.Contains(item.Extension) || AudioExtensions.Contains(item.Extension))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool isVideo = VideoExtensions.Contains(item.Extension);
                    string mediaInfo = QueryWindowsMediaProperties(fileInfo.FullName, isVideo);
                    BitmapSource? mediaThumb = isVideo
                        ? ThumbnailService.TryExtractShellImage(fileInfo.FullName, 520, thumbnailOnly: true)
                        : null;

                    return new FilePreviewData
                    {
                        Kind = mediaThumb is not null ? PreviewKind.Image : PreviewKind.Binary,
                        Name = item.Name,
                        FullPath = item.FullPath,
                        TypeDescription = typeName,
                        SizeSummary = sizeFormatted,
                        ExactBytesSummary = exactBytes,
                        CreatedText = created,
                        ModifiedText = modified,
                        AccessedText = accessed,
                        AttributesText = attrs,
                        LinkTarget = item.LinkTarget,
                        Icon = largeIcon ?? item.Icon,
                        ImagePreview = mediaThumb,
                        PixelWidth = mediaThumb?.PixelWidth ?? 0,
                        PixelHeight = mediaThumb?.PixelHeight ?? 0,
                        StatusMessage = mediaInfo
                    };
                }

                // 4. Text / Code Files
                if (TextExtensions.Contains(item.Extension) || (length > 0 && length <= 16_384 && LooksLikeUtf8Text(fileInfo.FullName)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (text, truncated) = ReadBoundedText(fileInfo.FullName, length);
                    return new FilePreviewData
                    {
                        Kind = PreviewKind.Text,
                        Name = item.Name,
                        FullPath = item.FullPath,
                        TypeDescription = typeName,
                        SizeSummary = sizeFormatted,
                        ExactBytesSummary = exactBytes,
                        CreatedText = created,
                        ModifiedText = modified,
                        AccessedText = accessed,
                        AttributesText = attrs,
                        LinkTarget = item.LinkTarget,
                        Icon = largeIcon ?? item.Icon,
                        TextPreview = text,
                        IsTextTruncated = truncated
                    };
                }

                // 5. Binary Header & Hex Inspection Fallback
                string binarySummary = length == 0
                    ? "Empty file (0 bytes)."
                    : BuildBinaryHeaderSummary(fileInfo.FullName);

                return new FilePreviewData
                {
                    Kind = PreviewKind.Binary,
                    Name = item.Name,
                    FullPath = item.FullPath,
                    TypeDescription = typeName,
                    SizeSummary = sizeFormatted,
                    ExactBytesSummary = exactBytes,
                    CreatedText = created,
                    ModifiedText = modified,
                    AccessedText = accessed,
                    AttributesText = attrs,
                    LinkTarget = item.LinkTarget,
                    Icon = largeIcon ?? item.Icon,
                    StatusMessage = binarySummary
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new FilePreviewData
                {
                    Kind = PreviewKind.Error,
                    Name = item.Name,
                    FullPath = item.FullPath,
                    TypeDescription = typeName,
                    Icon = largeIcon ?? item.Icon,
                    StatusMessage = $"Preview unavailable: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    private static string InspectPdfMetadata(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int sampleBytes = (int)Math.Min(stream.Length, 256 * 1024);
            byte[] buffer = new byte[sampleBytes];
            int read = stream.Read(buffer, 0, sampleBytes);
            string ascii = Encoding.Latin1.GetString(buffer, 0, read);

            string version = ascii.StartsWith("%PDF-", StringComparison.Ordinal) && ascii.Length >= 8
                ? $"PDF {ascii.Substring(5, 3)}"
                : "PDF Document";

            int pageEstimate = Regex.Matches(ascii, @"/Type\s*/Page\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150)).Count;
            string pageText = pageEstimate > 0
                ? $" • ~{pageEstimate} page(s) in header sample"
                : string.Empty;

            return $"{version}{pageText}\nDouble-click to open full document in default PDF reader.";
        }
        catch
        {
            return "PDF Document";
        }
    }

    private static string QueryWindowsMediaProperties(string filePath, bool isVideo)
    {
        var lines = new List<string> { isVideo ? "Video Media Stream" : "Audio Media Stream" };

        try
        {
            Guid iid = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"); // IPropertyStore
            if (SHGetPropertyStoreFromParsingName(filePath, IntPtr.Zero, 0, ref iid, out IntPtr storePtr) == 0 && storePtr != IntPtr.Zero)
            {
                try
                {
                    var store = (IPropertyStore)Marshal.GetTypedObjectForIUnknown(storePtr, typeof(IPropertyStore));

                    // System.Media.Duration (100-ns units): {64440490-4C8B-11D1-8B70-080036B11A03}, 3
                    ulong? duration100Ns = ReadUInt64Prop(store, new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);
                    if (duration100Ns.HasValue && duration100Ns.Value > 0)
                    {
                        var ts = TimeSpan.FromTicks((long)duration100Ns.Value);
                        lines.Add($"Duration: {(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}");
                    }

                    if (isVideo)
                    {
                        // System.Video.FrameWidth (3) & FrameHeight (4): {64440491-4C8B-11D1-8B70-080036B11A03}
                        uint? fw = ReadUInt32Prop(store, new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 3);
                        uint? fh = ReadUInt32Prop(store, new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 4);
                        if (fw > 0 && fh > 0)
                            lines.Add($"Resolution: {fw} × {fh} px");
                    }

                    // System.Audio.EncodingBitrate: {64440490-4C8B-11D1-8B70-080036B11A03}, 4
                    uint? bitrate = ReadUInt32Prop(store, new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 4);
                    if (bitrate > 0)
                        lines.Add($"Audio Bitrate: {bitrate / 1000} kbps");
                }
                finally
                {
                    Marshal.Release(storePtr);
                }
            }
        }
        catch { }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildBinaryHeaderSummary(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buf = stackalloc byte[64];
            int read = stream.Read(buf);
            if (read == 0)
                return "Empty file.";

            string sig = DetectSignature(buf[..read]);
            var sb = new StringBuilder();
            sb.AppendLine($"Binary File ({sig})");
            sb.AppendLine("Header (first 32 bytes):");

            int hexCount = Math.Min(read, 32);
            for (int i = 0; i < hexCount; i += 8)
            {
                int rowLen = Math.Min(8, hexCount - i);
                for (int j = 0; j < rowLen; j++)
                    sb.Append($"{buf[i + j]:X2} ");
                for (int pad = rowLen; pad < 8; pad++)
                    sb.Append("   ");
                sb.Append(" | ");
                for (int j = 0; j < rowLen; j++)
                {
                    byte b = buf[i + j];
                    sb.Append(b is >= 32 and <= 126 ? (char)b : '.');
                }
                if (i + 8 < hexCount)
                    sb.AppendLine();
            }

            return sb.ToString();
        }
        catch
        {
            return "Binary file — double-click to launch default handler.";
        }
    }

    private static string DetectSignature(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2 && header[0] == 'M' && header[1] == 'Z') return "Win32/PE Executable";
        if (header.Length >= 4 && header[0] == 'P' && header[1] == 'K' && header[2] == 3 && header[3] == 4) return "ZIP / OpenXML Container";
        if (header.Length >= 6 && header[0] == '7' && header[1] == 'z') return "7-Zip Archive";
        if (header.Length >= 4 && header[0] == 0x76 && header[1] == 0x2F && header[2] == 0x31 && header[3] == 0x01) return "OpenEXR Image";
        if (header.Length >= 7 && Encoding.ASCII.GetString(header[..7]) == "BLENDER") return "Blender Scene";
        return "Raw Binary";
    }

    private static (BitmapSource? Bitmap, int Width, int Height) TryLoadNonLockingBitmap(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return (null, 0, 0);

            BitmapFrame frame = decoder.Frames[0];
            int origWidth = frame.PixelWidth;
            int origHeight = frame.PixelHeight;

            if (origWidth > 1200)
            {
                stream.Position = 0;
                var scaled = new BitmapImage();
                scaled.BeginInit();
                scaled.CacheOption = BitmapCacheOption.OnLoad;
                scaled.DecodePixelWidth = 1200;
                scaled.StreamSource = stream;
                scaled.EndInit();
                scaled.Freeze();
                return (scaled, origWidth, origHeight);
            }

            frame.Freeze();
            return (frame, origWidth, origHeight);
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    private static (string Text, bool Truncated) ReadBoundedText(string filePath, long totalBytes)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int toRead = (int)Math.Min(totalBytes, MaxTextPreviewBytes);
        byte[] buffer = new byte[toRead];
        int actualRead = stream.Read(buffer, 0, toRead);

        using var mem = new MemoryStream(buffer, 0, actualRead);
        using var reader = new StreamReader(mem, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        return (text, totalBytes > MaxTextPreviewBytes);
    }

    private static bool LooksLikeUtf8Text(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> sample = stackalloc byte[512];
            int read = stream.Read(sample);
            for (int i = 0; i < read; i++)
            {
                if (sample[i] == 0)
                    return false;
            }
            return read > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildQuickDirectorySummary(DirectoryInfo dirInfo, CancellationToken cancellationToken)
    {
        try
        {
            int folders = 0, files = 0;
            bool capped = false;
            var opts = new EnumerationOptions { IgnoreInaccessible = true, ReturnSpecialDirectories = false };

            foreach (FileSystemInfo child in dirInfo.EnumerateFileSystemInfos("*", opts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((child.Attributes & FileAttributes.Directory) != 0) folders++;
                else files++;

                if (folders + files >= 5000) { capped = true; break; }
            }

            string prefix = capped ? "5,000+ items (" : string.Empty;
            string suffix = capped ? "+)" : string.Empty;
            return $"{prefix}{folders:N0} folders, {files:N0} files{suffix}";
        }
        catch
        {
            return "Contents inaccessible";
        }
    }

    private static string FormatDate(DateTime dt) =>
        dt == DateTime.MinValue ? "—" : dt.ToString("yyyy-MM-dd HH:mm:ss");

    private static string DescribeAttributes(FileAttributes attributes)
    {
        var parts = new List<string>();
        if ((attributes & FileAttributes.ReadOnly) != 0) parts.Add("Read-Only");
        if ((attributes & FileAttributes.Hidden) != 0) parts.Add("Hidden");
        if ((attributes & FileAttributes.System) != 0) parts.Add("System");
        if ((attributes & FileAttributes.Archive) != 0) parts.Add("Archive");
        if ((attributes & FileAttributes.ReparsePoint) != 0) parts.Add("Symbolic Link / Junction");
        if ((attributes & FileAttributes.Compressed) != 0) parts.Add("Compressed");
        if ((attributes & FileAttributes.Encrypted) != 0) parts.Add("Encrypted");
        return parts.Count == 0 ? "Normal" : string.Join(", ", parts);
    }

    private static ulong? ReadUInt64Prop(IPropertyStore store, Guid fmtid, uint pid)
    {
        var key = new PROPERTYKEY { fmtid = fmtid, pid = pid };
        if (store.GetValue(ref key, out PROPVARIANT pv) == 0)
        {
            try
            {
                if (pv.vt == 21) // VT_UI8
                    return pv.uhVal;
            }
            finally { PropVariantClear(ref pv); }
        }
        return null;
    }

    private static uint? ReadUInt32Prop(IPropertyStore store, Guid fmtid, uint pid)
    {
        var key = new PROPERTYKEY { fmtid = fmtid, pid = pid };
        if (store.GetValue(ref key, out PROPVARIANT pv) == 0)
        {
            try
            {
                if (pv.vt == 19) // VT_UI4
                    return pv.uintVal;
            }
            finally { PropVariantClear(ref pv); }
        }
        return null;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public ulong uhVal;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        int flags,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);
}
