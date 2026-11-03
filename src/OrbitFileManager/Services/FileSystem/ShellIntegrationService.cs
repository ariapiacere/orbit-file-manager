using OrbitFileManager.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbitFileManager.Services.FileSystem;

public sealed class ShellIntegrationService
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_TYPENAME = 0x000000400;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

    private readonly ConcurrentDictionary<string, (ImageSource? Icon, string TypeName)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public (ImageSource? Icon, string TypeName) GetEntryMetadata(
        string fullPath,
        string extension,
        bool isDirectory,
        bool largeIcon = false)
    {
        bool isPerFileIconExtension = !isDirectory &&
            (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase));

        bool isUnc = fullPath.StartsWith(@"\\", StringComparison.Ordinal);

        string cacheKey = isDirectory
            ? (largeIcon ? "__DIR_LARGE__" : "__DIR_SMALL__")
            : isPerFileIconExtension && !isUnc
                ? $"{(largeIcon ? "L" : "S")}|PATH|{fullPath}"
                : $"{(largeIcon ? "L" : "S")}|EXT|{(string.IsNullOrEmpty(extension) ? "__NOEXT__" : extension)}";

        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Bound cache growth for huge diverse directories
        if (_cache.Count > 2048)
            _cache.Clear();

        var resolved = QueryShellInfo(fullPath, extension, isDirectory, largeIcon, useRealPath: isPerFileIconExtension && !isUnc);
        _cache[cacheKey] = resolved;
        return resolved;
    }

    public ImageSource? GetDriveIcon(string rootPath)
    {
        string key = $"DRIVE|{rootPath}";
        if (_cache.TryGetValue(key, out var cached))
            return cached.Icon;

        var info = QueryShellInfo(rootPath, string.Empty, isDirectory: true, largeIcon: false, useRealPath: !rootPath.StartsWith(@"\\", StringComparison.Ordinal));
        _cache[key] = info;
        return info.Icon;
    }

    public (bool Success, string? ErrorMessage) OpenWithDefaultApplication(string fullPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty
            };
            Process.Start(psi);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Unable to open '{Path.GetFileName(fullPath)}': {ex.Message}");
        }
    }

    public (bool Success, string? ErrorMessage) RevealInWindowsExplorer(string fullPath)
    {
        try
        {
            string arguments = File.Exists(fullPath) || Directory.Exists(fullPath)
                ? $"/select,\"{fullPath}\""
                : $"\"{Path.GetDirectoryName(fullPath) ?? fullPath}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Unable to open Windows Explorer: {ex.Message}");
        }
    }

    public bool ShowNativePropertiesDialog(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                lpVerb = "properties",
                lpFile = fullPath,
                nShow = 5, // SW_SHOW
                fMask = SEE_MASK_INVOKEIDLIST
            };
            return ShellExecuteEx(ref info);
        }
        catch
        {
            return false;
        }
    }

    private static (ImageSource? Icon, string TypeName) QueryShellInfo(
        string fullPath,
        string extension,
        bool isDirectory,
        bool largeIcon,
        bool useRealPath)
    {
        string fallbackType = FileSizeFormatter.GetFallbackTypeDescription(extension, isDirectory);

        try
        {
            var shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_TYPENAME | (largeIcon ? SHGFI_LARGEICON : SHGFI_SMALLICON);
            uint fileAttributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

            string queryTarget;
            if (useRealPath)
            {
                queryTarget = fullPath;
            }
            else
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
                queryTarget = isDirectory
                    ? "Directory"
                    : (string.IsNullOrEmpty(extension) ? "File" : "Item" + extension);
            }

            IntPtr result = SHGetFileInfo(
                queryTarget,
                fileAttributes,
                ref shfi,
                (uint)Marshal.SizeOf(shfi),
                flags);

            if (result == IntPtr.Zero)
                return (null, fallbackType);

            try
            {
                BitmapSource? bitmap = null;
                if (shfi.hIcon != IntPtr.Zero)
                {
                    bitmap = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                }

                string typeName = string.IsNullOrWhiteSpace(shfi.szTypeName)
                    ? fallbackType
                    : shfi.szTypeName;

                return (bitmap, typeName);
            }
            finally
            {
                if (shfi.hIcon != IntPtr.Zero)
                    DestroyIcon(shfi.hIcon);
            }
        }
        catch
        {
            return (null, fallbackType);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
