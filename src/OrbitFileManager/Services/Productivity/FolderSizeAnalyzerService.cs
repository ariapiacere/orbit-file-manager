using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.IO;
using System.IO.Enumeration;

namespace OrbitFileManager.Services.Productivity;

public sealed record FolderSizeItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required long SizeBytes { get; init; }
    public string FormattedSize => FileSizeFormatter.FormatBytes(SizeBytes);
    public required int FileCount { get; init; }
    public required int SubfolderCount { get; init; }
    public required double RatioOfParent { get; init; }
    public string PercentageText => $"{RatioOfParent * 100.0:0.0}%";
}

public sealed record FolderSizeReport
{
    public required string RootPath { get; init; }
    public required long TotalSizeBytes { get; init; }
    public string FormattedTotalSize => FileSizeFormatter.FormatBytes(TotalSizeBytes);
    public required int TotalFiles { get; init; }
    public required int TotalSubfolders { get; init; }
    public required int SkippedInaccessibleCount { get; init; }
    public required IReadOnlyList<FolderSizeItem> Items { get; init; }
}

public sealed class FolderSizeAnalyzerService
{
    private readonly StructuredLogger _logger;

    public FolderSizeAnalyzerService(StructuredLogger logger)
    {
        _logger = logger;
    }

    public async Task<FolderSizeReport> AnalyzeFolderAsync(
        string folderPath,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedRoot = PathValidator.NormalizePath(folderPath);
        if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException($"Directory not found: '{folderPath}'.");

        return await Task.Run(() =>
        {
            var topLevelFolders = new List<string>();
            var rootFiles = new List<(string Name, string FullPath, long Length)>();
            int skipped = 0;

            var topOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0
            };

            statusProgress?.Report($"Scanning top-level entries in '{normalizedRoot}'...");

            try
            {
                var topEnumerable = new FileSystemEnumerable<(bool IsDir, bool IsReparse, string Name, string FullPath, long Length)>(
                    normalizedRoot,
                    (ref FileSystemEntry entry) => (
                        entry.IsDirectory,
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                        entry.FileName.ToString(),
                        entry.ToFullPath(),
                        entry.IsDirectory ? 0L : entry.Length),
                    topOptions);

                foreach (var item in topEnumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.IsReparse)
                        continue;

                    if (item.IsDir)
                        topLevelFolders.Add(item.FullPath);
                    else
                        rootFiles.Add((item.Name, item.FullPath, item.Length));
                }
            }
            catch (Exception ex)
            {
                skipped++;
                _logger.Warn("FolderSizeAnalyzer", $"Unable to enumerate root '{normalizedRoot}': {ex.Message}");
            }

            var rawResults = new List<(string Name, string FullPath, bool IsDir, long Bytes, int Files, int Folders)>();
            long grandTotalBytes = 0;
            int grandTotalFiles = rootFiles.Count;
            int grandTotalFolders = topLevelFolders.Count;

            foreach (var rf in rootFiles)
            {
                grandTotalBytes += rf.Length;
                rawResults.Add((rf.Name, rf.FullPath, false, rf.Length, 1, 0));
            }

            int idx = 0;
            foreach (string subDir in topLevelFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                idx++;
                string subName = Path.GetFileName(subDir.TrimEnd(Path.DirectorySeparatorChar));
                statusProgress?.Report($"Calculating size ({idx}/{topLevelFolders.Count}): {subName}...");

                var (bytes, files, folders, subSkipped) = ComputeSubtreeTotals(subDir, cancellationToken);
                grandTotalBytes += bytes;
                grandTotalFiles += files;
                grandTotalFolders += folders;
                skipped += subSkipped;

                rawResults.Add((subName, subDir, true, bytes, files, folders));
            }

            double denom = grandTotalBytes > 0 ? grandTotalBytes : 1.0;

            var sortedItems = rawResults
                .OrderByDescending(r => r.Bytes)
                .ThenByDescending(r => r.IsDir)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new FolderSizeItem
                {
                    Name = r.Name,
                    FullPath = r.FullPath,
                    IsDirectory = r.IsDir,
                    SizeBytes = r.Bytes,
                    FileCount = r.Files,
                    SubfolderCount = r.Folders,
                    RatioOfParent = grandTotalBytes > 0 ? Math.Clamp(r.Bytes / denom, 0.0, 1.0) : 0.0
                })
                .ToList();

            return new FolderSizeReport
            {
                RootPath = normalizedRoot,
                TotalSizeBytes = grandTotalBytes,
                TotalFiles = grandTotalFiles,
                TotalSubfolders = grandTotalFolders,
                SkippedInaccessibleCount = skipped,
                Items = sortedItems
            };
        }, cancellationToken);
    }

    private static (long Bytes, int Files, int Folders, int Skipped) ComputeSubtreeTotals(
        string rootSubfolder,
        CancellationToken cancellationToken)
    {
        long bytes = 0;
        int files = 0;
        int folders = 0;
        int skipped = 0;

        var stack = new Stack<string>();
        stack.Push(rootSubfolder);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = stack.Pop();

            try
            {
                var enumerable = new FileSystemEnumerable<(bool IsDir, bool IsReparse, long Length, string FullPath)>(
                    current,
                    (ref FileSystemEntry entry) => (
                        entry.IsDirectory,
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                        entry.IsDirectory ? 0L : entry.Length,
                        entry.ToFullPath()),
                    options);

                foreach (var item in enumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.IsReparse)
                        continue;

                    if (item.IsDir)
                    {
                        folders++;
                        stack.Push(item.FullPath);
                    }
                    else
                    {
                        files++;
                        bytes += item.Length;
                    }
                }
            }
            catch
            {
                skipped++;
            }
        }

        return (bytes, files, folders, skipped);
    }
}
