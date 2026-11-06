using OrbitFileManager.Models;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace OrbitFileManager.Services.FileOperations;

public sealed class FileOperationService
{
    private const int CopyBufferSize = 131_072; // 128 KB buffer for fast large-file I/O
    private readonly StructuredLogger _logger;

    public FileOperationService(StructuredLogger logger)
    {
        _logger = logger;
    }

    public Task<(bool Success, string? CreatedPath, string? ErrorMessage)> CreateFolderAsync(
        string parentDirectory,
        string folderName)
    {
        return Task.Run<(bool, string?, string?)>(() =>
        {
            var (valid, validationError) = PathValidator.ValidateEntryName(folderName);
            if (!valid)
                return (false, null, validationError);

            string normalizedParent = PathValidator.NormalizePath(parentDirectory);
            if (!Directory.Exists(normalizedParent))
                return (false, null, $"Parent folder '{normalizedParent}' does not exist.");

            string targetPath = Path.Combine(normalizedParent, folderName);
            if (PathValidator.EntryExists(targetPath))
                return (false, null, $"An item named '{folderName}' already exists in this folder.");

            try
            {
                Directory.CreateDirectory(targetPath);
                _logger.Info("FileOps", "Created folder.", new { targetPath });
                return (true, targetPath, null);
            }
            catch (Exception ex)
            {
                _logger.Error("FileOps", "Failed to create folder.", ex, new { targetPath });
                return (false, null, $"Could not create folder '{folderName}': {ex.Message}");
            }
        });
    }

    public Task<(bool Success, string? CreatedPath, string? ErrorMessage)> CreateTextFileAsync(
        string parentDirectory,
        string fileName)
    {
        return Task.Run<(bool, string?, string?)>(() =>
        {
            var (valid, validationError) = PathValidator.ValidateEntryName(fileName);
            if (!valid)
                return (false, null, validationError);

            string normalizedParent = PathValidator.NormalizePath(parentDirectory);
            if (!Directory.Exists(normalizedParent))
                return (false, null, $"Parent folder '{normalizedParent}' does not exist.");

            string targetPath = Path.Combine(normalizedParent, fileName);
            if (PathValidator.EntryExists(targetPath))
                return (false, null, $"An item named '{fileName}' already exists in this folder.");

            try
            {
                using var stream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                _logger.Info("FileOps", "Created empty file.", new { targetPath });
                return (true, targetPath, null);
            }
            catch (Exception ex)
            {
                _logger.Error("FileOps", "Failed to create file.", ex, new { targetPath });
                return (false, null, $"Could not create file '{fileName}': {ex.Message}");
            }
        });
    }

    public Task<(bool Success, string? NewPath, string? ErrorMessage)> RenameAsync(
        string sourcePath,
        string newName)
    {
        return Task.Run<(bool, string?, string?)>(() =>
        {
            var (valid, validationError) = PathValidator.ValidateEntryName(newName);
            if (!valid)
                return (false, null, validationError);

            string normalizedSource = PathValidator.NormalizePath(sourcePath);
            bool isDir = Directory.Exists(normalizedSource);
            bool isFile = !isDir && File.Exists(normalizedSource);

            if (!isDir && !isFile)
                return (false, null, "The selected item no longer exists.");

            string? parentDir = Path.GetDirectoryName(normalizedSource);
            if (string.IsNullOrEmpty(parentDir))
                return (false, null, "Root drives cannot be renamed here.");

            string currentName = Path.GetFileName(normalizedSource);
            if (string.Equals(currentName, newName, StringComparison.Ordinal))
                return (true, normalizedSource, null);

            string targetPath = Path.Combine(parentDir, newName);
            bool isCaseOnlyRename = string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase);

            if (!isCaseOnlyRename && PathValidator.EntryExists(targetPath))
                return (false, null, $"An item named '{newName}' already exists in this folder.");

            try
            {
                if (isDir)
                {
                    if (isCaseOnlyRename)
                    {
                        string tempPath = Path.Combine(parentDir, $".orbit_case_{Guid.NewGuid():N}");
                        Directory.Move(normalizedSource, tempPath);
                        Directory.Move(tempPath, targetPath);
                    }
                    else
                    {
                        Directory.Move(normalizedSource, targetPath);
                    }
                }
                else
                {
                    File.Move(normalizedSource, targetPath);
                }

                _logger.Info("FileOps", "Renamed item.", new { from = normalizedSource, to = targetPath });
                return (true, targetPath, null);
            }
            catch (Exception ex)
            {
                _logger.Error("FileOps", "Failed to rename item.", ex, new { from = normalizedSource, to = targetPath });
                return (false, null, $"Could not rename '{currentName}': {ex.Message}");
            }
        });
    }

    public async Task<FileOperationResult> TransferItemsAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool isMove,
        Func<FileConflictContext, Task<ConflictDecision>>? conflictPrompt,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedDestDir = PathValidator.NormalizePath(destinationDirectory);
        if (!Directory.Exists(normalizedDestDir))
        {
            return new FileOperationResult
            {
                Errors = [$"Destination folder '{normalizedDestDir}' does not exist or is unavailable."]
            };
        }

        int succeeded = 0;
        int skipped = 0;
        var createdPaths = new List<string>();
        var errors = new List<string>();
        ConflictResolution? stickyResolution = null;

        foreach (string rawSource in sourcePaths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new FileOperationResult
                {
                    SucceededCount = succeeded,
                    SkippedCount = skipped,
                    IsCancelled = true,
                    CreatedOrUpdatedPaths = createdPaths,
                    Errors = errors
                };
            }

            string sourcePath = PathValidator.NormalizePath(rawSource);
            bool isDir = Directory.Exists(sourcePath);
            bool isFile = !isDir && File.Exists(sourcePath);

            if (!isDir && !isFile)
            {
                errors.Add($"Source item not found: '{sourcePath}'.");
                continue;
            }

            string itemName = Path.GetFileName(sourcePath);
            string? sourceParent = Path.GetDirectoryName(sourcePath);
            bool sameFolder = sourceParent is not null && PathValidator.PathsEqual(sourceParent, normalizedDestDir);

            if (sameFolder && isMove)
            {
                // Moving an item into its own current folder is a safe no-op.
                skipped++;
                continue;
            }

            string targetPath;
            bool overwriteTarget = false;

            if (sameFolder && !isMove)
            {
                // Copying into same directory creates "Item - Copy" automatically.
                targetPath = PathValidator.GenerateUniqueDestinationPath(normalizedDestDir, itemName, isDir, useCopyLabel: true);
            }
            else
            {
                targetPath = Path.Combine(normalizedDestDir, itemName);

                if (isDir && PathValidator.IsSameOrSubPath(sourcePath, targetPath))
                {
                    errors.Add($"Cannot {(isMove ? "move" : "copy")} folder '{itemName}' into itself or one of its subfolders.");
                    continue;
                }

                if (PathValidator.EntryExists(targetPath))
                {
                    ConflictResolution resolution;
                    if (stickyResolution.HasValue)
                    {
                        resolution = stickyResolution.Value;
                    }
                    else if (conflictPrompt is not null)
                    {
                        var ctx = BuildConflictContext(sourcePath, targetPath, isDir);
                        ConflictDecision decision = await conflictPrompt(ctx);
                        if (decision.ApplyToAll && decision.Resolution != ConflictResolution.Cancel)
                            stickyResolution = decision.Resolution;
                        resolution = decision.Resolution;
                    }
                    else
                    {
                        resolution = ConflictResolution.Skip;
                    }

                    if (resolution == ConflictResolution.Cancel)
                    {
                        return new FileOperationResult
                        {
                            SucceededCount = succeeded,
                            SkippedCount = skipped,
                            IsCancelled = true,
                            CreatedOrUpdatedPaths = createdPaths,
                            Errors = errors
                        };
                    }

                    if (resolution == ConflictResolution.Skip)
                    {
                        skipped++;
                        continue;
                    }

                    if (resolution == ConflictResolution.KeepBoth)
                    {
                        targetPath = PathValidator.GenerateUniqueDestinationPath(normalizedDestDir, itemName, isDir, useCopyLabel: false);
                    }
                    else if (resolution == ConflictResolution.Overwrite)
                    {
                        overwriteTarget = true;
                    }
                }
            }

            statusProgress?.Report($"{(isMove ? "Moving" : "Copying")} {itemName}...");

            try
            {
                await Task.Run(() =>
                {
                    if (isFile)
                    {
                        TransferSingleFile(sourcePath, targetPath, isMove, overwriteTarget, cancellationToken);
                    }
                    else
                    {
                        TransferDirectory(sourcePath, targetPath, isMove, overwriteTarget, cancellationToken);
                    }
                }, cancellationToken);

                createdPaths.Add(targetPath);
                succeeded++;
                _logger.Info("FileOps", isMove ? "Moved item." : "Copied item.", new { from = sourcePath, to = targetPath });
            }
            catch (OperationCanceledException)
            {
                return new FileOperationResult
                {
                    SucceededCount = succeeded,
                    SkippedCount = skipped,
                    IsCancelled = true,
                    CreatedOrUpdatedPaths = createdPaths,
                    Errors = errors
                };
            }
            catch (Exception ex)
            {
                _logger.Error("FileOps", $"Failed to {(isMove ? "move" : "copy")} '{sourcePath}'.", ex);
                errors.Add($"'{itemName}': {ex.Message}");
            }
        }

        return new FileOperationResult
        {
            SucceededCount = succeeded,
            SkippedCount = skipped,
            CreatedOrUpdatedPaths = createdPaths,
            Errors = errors
        };
    }

    public Task<FileOperationResult> DeleteItemsAsync(
        IReadOnlyList<string> paths,
        bool permanent,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var removed = new List<string>();
            var errors = new List<string>();
            // Recycle Bin moves are collected and sent to the shell in batches after this loop.
            var toRecycle = new List<(string RawPath, string FullPath)>();

            FileOperationResult Result(bool cancelled = false) => new()
            {
                SucceededCount = removed.Count,
                IsCancelled = cancelled,
                RemovedPaths = removed,
                Errors = errors
            };

            foreach (string rawPath in paths)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result(cancelled: true);

                string fullPath = PathValidator.NormalizePath(rawPath);
                string itemName = Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(itemName))
                {
                    errors.Add($"Cannot delete root path '{fullPath}'.");
                    continue;
                }

                try
                {
                    bool isDir = Directory.Exists(fullPath);
                    bool isFile = !isDir && File.Exists(fullPath);

                    if (!isDir && !isFile)
                        continue;

                    FileAttributes attrs = File.GetAttributes(fullPath);
                    bool isReparsePoint = (attrs & FileAttributes.ReparsePoint) != 0;

                    if (isDir && isReparsePoint)
                    {
                        // Critical safety rule: deleting a junction or directory symlink must ONLY remove
                        // the link itself and never recursively delete the target folder's contents.
                        statusProgress?.Report($"Deleting {itemName}...");
                        Directory.Delete(fullPath, recursive: false);
                    }
                    else if (!permanent)
                    {
                        toRecycle.Add((rawPath, fullPath));
                        continue;
                    }
                    else
                    {
                        statusProgress?.Report($"Deleting {itemName}...");
                        if (isDir)
                        {
                            ClearReadOnlyAttributesRecursively(new DirectoryInfo(fullPath));
                            Directory.Delete(fullPath, recursive: true);
                        }
                        else
                        {
                            if ((attrs & FileAttributes.ReadOnly) != 0)
                                File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);
                            File.Delete(fullPath);
                        }
                    }

                    removed.Add(rawPath);
                    _logger.Info("FileOps", "Permanently deleted item.", new { fullPath });
                }
                catch (OperationCanceledException)
                {
                    return Result(cancelled: true);
                }
                catch (Exception ex)
                {
                    _logger.Error("FileOps", $"Failed to delete '{fullPath}'.", ex);
                    errors.Add($"'{itemName}': {ex.Message}");
                }
            }

            bool recycleCancelled = RecycleInBatches(toRecycle, removed, errors, statusProgress, cancellationToken);
            return Result(recycleCancelled);
        }, cancellationToken);
    }

    // Each shell round-trip into the Recycle Bin costs roughly 30 ms, so recycling one item per call made
    // large selections take seconds to minutes. Batches keep that to one call per chunk while still letting
    // the user cancel between chunks and see progress.
    private const int RecycleBatchSize = 250;

    /// <summary>Moves items to the Recycle Bin in shell batches. Returns true when the operation was cancelled.</summary>
    private bool RecycleInBatches(
        List<(string RawPath, string FullPath)> items,
        List<string> removed,
        List<string> errors,
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken)
    {
        for (int start = 0; start < items.Count; start += RecycleBatchSize)
        {
            if (cancellationToken.IsCancellationRequested)
                return true;

            var batch = items.GetRange(start, Math.Min(RecycleBatchSize, items.Count - start));
            statusProgress?.Report(items.Count == 1
                ? $"Moving {Path.GetFileName(batch[0].FullPath)} to Recycle Bin..."
                : $"Moving {start + batch.Count:N0} of {items.Count:N0} items to Recycle Bin...");

            var (errorCode, aborted) = ShellRecycle(batch.Select(i => i.FullPath));

            // The shell reports one result for the whole batch, so check what is actually gone.
            var recycledNow = new List<string>(batch.Count);
            foreach (var (rawPath, fullPath) in batch)
            {
                if (!PathValidator.EntryExists(fullPath))
                {
                    removed.Add(rawPath);
                    recycledNow.Add(fullPath);
                }
                else if (!aborted)
                {
                    string code = errorCode != 0 ? $" (shell error 0x{errorCode:X})" : string.Empty;
                    errors.Add($"'{Path.GetFileName(fullPath)}': could not be moved to the Recycle Bin{code}.");
                }
            }

            if (recycledNow.Count > 0)
                _logger.Info("FileOps", "Recycled items.", new { count = recycledNow.Count, paths = recycledNow });

            if (aborted)
                return true;
        }

        return false;
    }

    private static (int ErrorCode, bool Aborted) ShellRecycle(IEnumerable<string> fullPaths)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Double-null-terminated list: the marshaller appends the final terminator.
            pFrom = string.Join('\0', fullPaths) + '\0',
            // Same quiet behaviour as before (only error dialogs), plus WANTNUKEWARNING so an item that cannot
            // go to the Recycle Bin (too large, no bin on that volume) prompts instead of being destroyed silently.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING
        };

        int hr = SHFileOperation(ref op);
        return (hr, op.fAnyOperationsAborted);
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    public void SetClipboardFiles(IReadOnlyList<string> paths, bool isCut)
    {
        if (paths.Count == 0)
            return;

        var dropList = new StringCollection();
        foreach (string p in paths)
            dropList.Add(p);

        var dataObject = new DataObject();
        dataObject.SetFileDropList(dropList);

        // Windows Shell Preferred DropEffect: 2 = DROPEFFECT_MOVE, 5 = DROPEFFECT_COPY | DROPEFFECT_LINK
        byte[] moveEffect = isCut ? [2, 0, 0, 0] : [5, 0, 0, 0];
        dataObject.SetData("Preferred DropEffect", new MemoryStream(moveEffect));

        Clipboard.SetDataObject(dataObject, copy: true);
    }

    public (IReadOnlyList<string> Paths, bool IsCut) GetClipboardFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
                return ([], false);

            StringCollection? rawList = Clipboard.GetFileDropList();
            if (rawList is null || rawList.Count == 0)
                return ([], false);

            var paths = new List<string>(rawList.Count);
            foreach (string? item in rawList)
            {
                if (!string.IsNullOrWhiteSpace(item))
                    paths.Add(item);
            }

            bool isCut = false;
            var dataObject = Clipboard.GetDataObject();
            if (dataObject?.GetDataPresent("Preferred DropEffect") == true &&
                dataObject.GetData("Preferred DropEffect") is MemoryStream stream &&
                stream.Length >= 4)
            {
                byte[] bytes = new byte[4];
                int read = stream.Read(bytes, 0, 4);
                if (read == 4)
                {
                    int effect = BitConverter.ToInt32(bytes, 0);
                    isCut = (effect & 2) == 2 && (effect & 1) == 0;
                }
            }

            return (paths, isCut);
        }
        catch
        {
            return ([], false);
        }
    }

    private static void TransferSingleFile(
        string sourceFile,
        string destinationFile,
        bool isMove,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!overwrite && PathValidator.EntryExists(destinationFile))
            throw new IOException($"Destination '{Path.GetFileName(destinationFile)}' already exists.");

        // Fast same-volume move when not overwriting an existing directory
        string? sourceRoot = Path.GetPathRoot(sourceFile);
        string? destRoot = Path.GetPathRoot(destinationFile);
        bool sameVolume = !string.IsNullOrEmpty(sourceRoot) &&
                          string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase);

        if (isMove && sameVolume)
        {
            FileTransferCommit.MoveFile(sourceFile, destinationFile, overwrite, cancellationToken);
            return;
        }

        // Safe staged copy: write to temporary file in destination directory first so an interrupted
        // copy never leaves a truncated file with the final filename or corrupts an existing file.
        string destParent = Path.GetDirectoryName(destinationFile)
            ?? throw new IOException("Invalid destination directory.");

        string tempFile = Path.Combine(destParent, $".{Path.GetFileName(destinationFile)}.orbit_part_{Guid.NewGuid():N}");

        try
        {
            using (var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan))
            using (var tempStream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                byte[] buffer = new byte[CopyBufferSize];
                int bytesRead;
                while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tempStream.Write(buffer, 0, bytesRead);
                }
                tempStream.Flush(flushToDisk: true);
            }

            var sourceInfo = new FileInfo(sourceFile);
            File.SetLastWriteTimeUtc(tempFile, sourceInfo.LastWriteTimeUtc);

            FileTransferCommit.MoveFile(tempFile, destinationFile, overwrite, cancellationToken);

            if (isMove)
            {
                File.Delete(sourceFile);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch
            {
                // Ignore cleanup failure
            }
            throw;
        }
    }

    private static void TransferDirectory(
        string sourceDir,
        string destinationDir,
        bool isMove,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? sourceRoot = Path.GetPathRoot(sourceDir);
        string? destRoot = Path.GetPathRoot(destinationDir);
        bool sameVolume = !string.IsNullOrEmpty(sourceRoot) &&
                          string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase);

        if (isMove && sameVolume && !PathValidator.EntryExists(destinationDir))
        {
            Directory.Move(sourceDir, destinationDir);
            return;
        }

        CopyDirectoryRecursive(new DirectoryInfo(sourceDir), destinationDir, overwrite, cancellationToken);

        if (isMove)
        {
            var dirInfo = new DirectoryInfo(sourceDir);
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(sourceDir, recursive: false);
            }
            else
            {
                Directory.Delete(sourceDir, recursive: true);
            }
        }
    }

    private static void CopyDirectoryRecursive(
        DirectoryInfo source,
        string destinationDir,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(destinationDir))
            throw new IOException($"Cannot merge folder '{source.Name}' onto an existing file of the same name.");

        Directory.CreateDirectory(destinationDir);

        // If source itself is a symlink/junction, do not traverse into potential cycles.
        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
            return;

        foreach (FileInfo file in source.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetFile = Path.Combine(destinationDir, file.Name);
            if (!overwrite && File.Exists(targetFile))
                continue;

            TransferSingleFile(file.FullName, targetFile, isMove: false, overwrite: overwrite, cancellationToken);
        }

        foreach (DirectoryInfo subDir in source.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetSubDir = Path.Combine(destinationDir, subDir.Name);

            if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Create placeholder directory or preserve link target safely without infinite recursion
                string? linkTarget = null;
                try { linkTarget = subDir.LinkTarget; } catch { }

                if (!string.IsNullOrEmpty(linkTarget) && !Directory.Exists(targetSubDir))
                {
                    try
                    {
                        Directory.CreateSymbolicLink(targetSubDir, linkTarget);
                        continue;
                    }
                    catch
                    {
                        // Standard user may not have SeCreateSymbolicLinkPrivilege; create empty dir safely instead of looping
                    }
                }

                Directory.CreateDirectory(targetSubDir);
                continue;
            }

            CopyDirectoryRecursive(subDir, targetSubDir, overwrite, cancellationToken);
        }
    }

    private static void ClearReadOnlyAttributesRecursively(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            return;

        foreach (FileInfo file in directory.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }))
        {
            try
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;
            }
            catch { }
        }

        foreach (DirectoryInfo sub in directory.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }))
        {
            ClearReadOnlyAttributesRecursively(sub);
        }
    }

    private static FileConflictContext BuildConflictContext(string sourcePath, string destPath, bool isDir)
    {
        long? srcSize = null;
        DateTime srcMod = DateTime.MinValue;
        long? dstSize = null;
        DateTime dstMod = DateTime.MinValue;

        try
        {
            if (isDir)
            {
                srcMod = Directory.GetLastWriteTime(sourcePath);
                dstMod = Directory.GetLastWriteTime(destPath);
            }
            else
            {
                var s = new FileInfo(sourcePath);
                srcSize = s.Length;
                srcMod = s.LastWriteTime;

                if (File.Exists(destPath))
                {
                    var d = new FileInfo(destPath);
                    dstSize = d.Length;
                    dstMod = d.LastWriteTime;
                }
            }
        }
        catch { }

        return new FileConflictContext(sourcePath, destPath, isDir, srcSize, srcMod, dstSize, dstMod);
    }
}
