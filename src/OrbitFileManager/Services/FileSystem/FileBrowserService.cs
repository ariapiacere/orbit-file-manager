using OrbitFileManager.Models;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.IO;

namespace OrbitFileManager.Services.FileSystem;

public sealed record DirectoryLoadResult(
    bool Success,
    string NormalizedPath,
    IReadOnlyList<FileEntryItem> Items,
    string? ErrorMessage);

public sealed class FileBrowserService
{
    private readonly ShellIntegrationService _shell;
    private readonly StructuredLogger _logger;

    public FileBrowserService(ShellIntegrationService shell, StructuredLogger logger)
    {
        _shell = shell;
        _logger = logger;
    }

    public IReadOnlyList<DriveEntryItem> GetConnectedDrives()
    {
        var drives = new List<DriveEntryItem>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                bool ready = drive.IsReady;
                string root = drive.RootDirectory.FullName;
                string letter = root.TrimEnd(Path.DirectorySeparatorChar);
                string volumeLabel = ready ? SafeGetVolumeLabel(drive) : string.Empty;
                string defaultLabel = drive.DriveType switch
                {
                    DriveType.Removable => "Removable Drive",
                    DriveType.Network => "Network Drive",
                    DriveType.CDRom => "Optical Drive",
                    _ => "Local Disk"
                };

                string title = string.IsNullOrWhiteSpace(volumeLabel)
                    ? $"{defaultLabel} ({letter})"
                    : $"{volumeLabel} ({letter})";

                long total = ready ? drive.TotalSize : 0;
                long free = ready ? drive.AvailableFreeSpace : 0;
                string summary = ready
                    ? $"{FileSizeFormatter.FormatBytes(free)} free of {FileSizeFormatter.FormatBytes(total)}"
                    : "Drive not ready";

                drives.Add(new DriveEntryItem
                {
                    Name = letter,
                    RootPath = root,
                    DisplayLabel = title,
                    DriveType = drive.DriveType,
                    DriveFormat = ready ? SafeGetDriveFormat(drive) : string.Empty,
                    IsReady = ready,
                    TotalBytes = total,
                    FreeBytes = free,
                    CapacitySummary = summary,
                    Icon = _shell.GetDriveIcon(root)
                });
            }
            catch (Exception ex)
            {
                _logger.Warn("FileBrowser", $"Skipped drive enumeration error for {drive.Name}.", ex);
            }
        }

        return drives;
    }

    public Task<DirectoryLoadResult> LoadDirectoryAsync(
        string rawPath,
        bool includeHidden,
        bool includeSystem,
        bool largeIcons,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            string normalized = PathValidator.NormalizePath(rawPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return new DirectoryLoadResult(false, string.Empty, [], "Folder path cannot be empty.");

            if (!Directory.Exists(normalized))
                return new DirectoryLoadResult(false, normalized, [], $"The folder '{normalized}' does not exist or is disconnected.");

            try
            {
                var dirInfo = new DirectoryInfo(normalized);
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    RecurseSubdirectories = false,
                    AttributesToSkip = BuildSkippedAttributes(includeHidden, includeSystem)
                };

                var entries = new List<FileEntryItem>(256);

                foreach (FileSystemInfo fsi in dirInfo.EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileAttributes attrs = fsi.Attributes;
                    bool isDir = (attrs & FileAttributes.Directory) != 0;
                    bool isHidden = (attrs & FileAttributes.Hidden) != 0;
                    bool isSystem = (attrs & FileAttributes.System) != 0;
                    bool isReadOnly = (attrs & FileAttributes.ReadOnly) != 0;
                    bool isSymlink = (attrs & FileAttributes.ReparsePoint) != 0;

                    if (!includeHidden && isHidden)
                        continue;
                    if (!includeSystem && isSystem)
                        continue;

                    string ext = isDir ? string.Empty : fsi.Extension;
                    long? bytes = null;
                    if (!isDir && fsi is FileInfo fi)
                    {
                        try { bytes = fi.Length; } catch { bytes = 0; }
                    }

                    var (icon, typeDesc) = _shell.GetEntryMetadata(fsi.FullName, ext, isDir, largeIcons);

                    DateTime created = SafeReadDate(() => fsi.CreationTime);
                    DateTime modified = SafeReadDate(() => fsi.LastWriteTime);

                    entries.Add(new FileEntryItem
                    {
                        Name = fsi.Name,
                        FullPath = fsi.FullName,
                        Extension = ext,
                        IsDirectory = isDir,
                        IsHidden = isHidden,
                        IsSystem = isSystem,
                        IsReadOnly = isReadOnly,
                        IsSymbolicLink = isSymlink,
                        LinkTarget = isSymlink ? SafeGetLinkTarget(fsi) : null,
                        SizeBytes = bytes,
                        FormattedSize = bytes.HasValue ? FileSizeFormatter.FormatBytes(bytes.Value) : string.Empty,
                        TypeDescription = isSymlink ? $"{typeDesc} (Link)" : typeDesc,
                        CreatedTime = created,
                        ModifiedTime = modified,
                        AttributesSummary = FileSizeFormatter.FormatAttributes(attrs),
                        Icon = icon
                    });
                }

                return new DirectoryLoadResult(true, normalized, entries, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warn("FileBrowser", $"Access denied reading directory: {normalized}", ex);
                return new DirectoryLoadResult(false, normalized, [], $"Access denied to '{normalized}'.");
            }
            catch (IOException ex)
            {
                _logger.Warn("FileBrowser", $"I/O error reading directory: {normalized}", ex);
                return new DirectoryLoadResult(false, normalized, [], $"Unable to read '{normalized}': {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error("FileBrowser", $"Unexpected error reading directory: {normalized}", ex);
                return new DirectoryLoadResult(false, normalized, [], $"Unexpected error opening '{normalized}': {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DirectoryInfo>> GetSubdirectoriesAsync(
        string directoryPath,
        bool includeHidden,
        bool includeSystem,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<DirectoryInfo>>(() =>
        {
            try
            {
                var info = new DirectoryInfo(directoryPath);
                if (!info.Exists)
                    return [];

                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    RecurseSubdirectories = false,
                    AttributesToSkip = BuildSkippedAttributes(includeHidden, includeSystem)
                };

                var list = new List<DirectoryInfo>();
                foreach (DirectoryInfo sub in info.EnumerateDirectories("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    list.Add(sub);
                }

                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return list;
            }
            catch
            {
                return [];
            }
        }, cancellationToken);
    }

    public bool HasAccessibleSubdirectories(string directoryPath, bool includeHidden, bool includeSystem)
    {
        try
        {
            var info = new DirectoryInfo(directoryPath);
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                RecurseSubdirectories = false,
                AttributesToSkip = BuildSkippedAttributes(includeHidden, includeSystem)
            };

            using var enumerator = info.EnumerateDirectories("*", options).GetEnumerator();
            return enumerator.MoveNext();
        }
        catch
        {
            return false;
        }
    }

    private static FileAttributes BuildSkippedAttributes(bool includeHidden, bool includeSystem)
    {
        FileAttributes skip = 0;
        if (!includeHidden)
            skip |= FileAttributes.Hidden;
        if (!includeSystem)
            skip |= FileAttributes.System;
        return skip;
    }

    private static string SafeGetVolumeLabel(DriveInfo drive)
    {
        try { return drive.VolumeLabel; } catch { return string.Empty; }
    }

    private static string SafeGetDriveFormat(DriveInfo drive)
    {
        try { return drive.DriveFormat; } catch { return string.Empty; }
    }

    private static DateTime SafeReadDate(Func<DateTime> getter)
    {
        try { return getter(); } catch { return DateTime.MinValue; }
    }

    private static string? SafeGetLinkTarget(FileSystemInfo fsi)
    {
        try { return fsi.LinkTarget; } catch { return null; }
    }
}
