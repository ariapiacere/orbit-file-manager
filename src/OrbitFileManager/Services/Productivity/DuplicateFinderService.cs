using CommunityToolkit.Mvvm.ComponentModel;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.IO;
using System.IO.Enumeration;
using System.Security.Cryptography;

namespace OrbitFileManager.Services.Productivity;

public partial class DuplicateFileEntry : ObservableObject
{
    public required int GroupIndex { get; init; }
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required string DirectoryPath { get; init; }
    public required long SizeBytes { get; init; }
    public string FormattedSize => FileSizeFormatter.FormatBytes(SizeBytes);
    public required DateTime ModifiedTime { get; init; }
    public required string Sha256Short { get; init; }
    public string GroupHeader => $"Group #{GroupIndex} • {FormattedSize} each ({Sha256Short})";

    [ObservableProperty]
    private bool _isMarkedForRemoval;
}

public sealed record DuplicateFileGroup
{
    public required int GroupIndex { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required IReadOnlyList<DuplicateFileEntry> Files { get; init; }
    public long ReclaimableBytes => Files.Count > 1 ? SizeBytes * (Files.Count - 1) : 0;
}

public sealed record DuplicateScanResult
{
    public required IReadOnlyList<DuplicateFileGroup> Groups { get; init; }
    public required int ScannedFileCount { get; init; }
    public long TotalReclaimableBytes => Groups.Sum(g => g.ReclaimableBytes);
}

public sealed class DuplicateFinderService
{
    private const int EdgeSampleSize = 8192;
    private const int StreamBufferSize = 81920;
    private readonly StructuredLogger _logger;

    public DuplicateFinderService(StructuredLogger logger)
    {
        _logger = logger;
    }

    public async Task<DuplicateScanResult> FindDuplicatesAsync(
        IReadOnlyList<string> rootFolders,
        bool recursive = true,
        long minSizeBytes = 1,
        bool includeHidden = false,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            var sizeGroups = new Dictionary<long, List<(string Path, DateTime ModifiedUtc)>>();
            var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalScanned = 0;

            statusProgress?.Report("Pass 1/3: Indexing files by exact byte size...");

            foreach (string rawRoot in rootFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string root = PathValidator.NormalizePath(rawRoot);
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                EnumerateFolderBySize(root, recursive, minSizeBytes, includeHidden, sizeGroups, visitedPaths, ref totalScanned, cancellationToken);
            }

            var candidateBuckets = sizeGroups
                .Where(kv => kv.Value.Count >= 2)
                .OrderByDescending(kv => kv.Key)
                .ToList();

            if (candidateBuckets.Count == 0)
            {
                return new DuplicateScanResult
                {
                    Groups = [],
                    ScannedFileCount = totalScanned
                };
            }

            // Pass 2: Fast 16 KB edge signature (first 8 KB + last 8 KB) for files > 16 KB
            statusProgress?.Report($"Pass 2/3: Checking headers/footers across {candidateBuckets.Count:N0} size bucket(s)...");
            var edgeMatchedBuckets = new List<(long SizeBytes, List<(string Path, DateTime ModifiedUtc)> Items)>();

            foreach (var (sizeBytes, items) in candidateBuckets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sizeBytes <= EdgeSampleSize * 2)
                {
                    edgeMatchedBuckets.Add((sizeBytes, items));
                    continue;
                }

                var byEdgeHash = new Dictionary<string, List<(string Path, DateTime ModifiedUtc)>>(StringComparer.Ordinal);
                foreach (var entry in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? edgeHash = await ComputeEdgeSignatureAsync(entry.Path, sizeBytes, cancellationToken);
                    if (edgeHash is null)
                        continue;

                    if (!byEdgeHash.TryGetValue(edgeHash, out var list))
                    {
                        list = [];
                        byEdgeHash[edgeHash] = list;
                    }
                    list.Add(entry);
                }

                foreach (var edgeGroup in byEdgeHash.Values.Where(g => g.Count >= 2))
                {
                    edgeMatchedBuckets.Add((sizeBytes, edgeGroup));
                }
            }

            // Pass 3: Full streaming SHA-256 verification
            var confirmedGroups = new List<DuplicateFileGroup>();
            int groupCounter = 1;
            int processedBuckets = 0;

            foreach (var (sizeBytes, items) in edgeMatchedBuckets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedBuckets++;
                statusProgress?.Report($"Pass 3/3: Computing full SHA-256 ({processedBuckets:N0}/{edgeMatchedBuckets.Count:N0} candidate groups)...");

                var byFullHash = new Dictionary<string, List<(string Path, DateTime ModifiedUtc)>>(StringComparer.Ordinal);
                foreach (var entry in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? sha256 = await ComputeFullSha256Async(entry.Path, cancellationToken);
                    if (sha256 is null)
                        continue;

                    if (!byFullHash.TryGetValue(sha256, out var list))
                    {
                        list = [];
                        byFullHash[sha256] = list;
                    }
                    list.Add(entry);
                }

                foreach (var (hash, matchedItems) in byFullHash.Where(kv => kv.Value.Count >= 2))
                {
                    int currentGroupIndex = groupCounter++;
                    string shortHash = hash[..12];

                    var entries = matchedItems
                        .OrderBy(m => m.ModifiedUtc)
                        .ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(m => new DuplicateFileEntry
                        {
                            GroupIndex = currentGroupIndex,
                            FullPath = m.Path,
                            FileName = Path.GetFileName(m.Path),
                            DirectoryPath = Path.GetDirectoryName(m.Path) ?? string.Empty,
                            SizeBytes = sizeBytes,
                            ModifiedTime = m.ModifiedUtc.ToLocalTime(),
                            Sha256Short = shortHash
                        })
                        .ToList();

                    confirmedGroups.Add(new DuplicateFileGroup
                    {
                        GroupIndex = currentGroupIndex,
                        Sha256 = hash,
                        SizeBytes = sizeBytes,
                        Files = entries
                    });
                }
            }

            var sortedGroups = confirmedGroups
                .OrderByDescending(g => g.ReclaimableBytes)
                .ThenBy(g => g.GroupIndex)
                .ToList();

            return new DuplicateScanResult
            {
                Groups = sortedGroups,
                ScannedFileCount = totalScanned
            };
        }, cancellationToken);
    }

    private void EnumerateFolderBySize(
        string rootDir,
        bool recursive,
        long minSizeBytes,
        bool includeHidden,
        Dictionary<long, List<(string Path, DateTime ModifiedUtc)>> sizeGroups,
        HashSet<string> visitedPaths,
        ref int totalScanned,
        CancellationToken cancellationToken)
    {
        var dirs = new Stack<string>();
        dirs.Push(rootDir);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        while (dirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = dirs.Pop();

            try
            {
                var enumerable = new FileSystemEnumerable<(bool IsDir, bool IsHidden, bool IsReparse, long Length, DateTime ModifiedUtc, string FullPath)>(
                    current,
                    (ref FileSystemEntry entry) => (
                        entry.IsDirectory,
                        (entry.Attributes & FileAttributes.Hidden) != 0,
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                        entry.IsDirectory ? 0L : entry.Length,
                        entry.LastWriteTimeUtc.UtcDateTime,
                        entry.ToFullPath()),
                    options);

                foreach (var item in enumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.IsReparse)
                        continue;
                    if (!includeHidden && item.IsHidden)
                        continue;

                    if (item.IsDir)
                    {
                        if (recursive)
                            dirs.Push(item.FullPath);
                        continue;
                    }

                    totalScanned++;
                    if (item.Length < minSizeBytes)
                        continue;

                    if (!visitedPaths.Add(item.FullPath))
                        continue;

                    if (!sizeGroups.TryGetValue(item.Length, out var bucket))
                    {
                        bucket = [];
                        sizeGroups[item.Length] = bucket;
                    }
                    bucket.Add((item.FullPath, item.ModifiedUtc));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("DuplicateFinder", $"Skipped folder '{current}': {ex.Message}");
            }
        }
    }

    private async Task<string?> ComputeEdgeSignatureAsync(string filePath, long expectedLength, CancellationToken cancellationToken)
    {
        try
        {
            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                EdgeSampleSize,
                FileOptions.Asynchronous);

            if (fs.Length != expectedLength)
                return null;

            byte[] buffer = new byte[EdgeSampleSize * 2];
            int headRead = await fs.ReadAsync(buffer.AsMemory(0, EdgeSampleSize), cancellationToken);
            fs.Seek(-EdgeSampleSize, SeekOrigin.End);
            int tailRead = await fs.ReadAsync(buffer.AsMemory(headRead, EdgeSampleSize), cancellationToken);

            byte[] hash = SHA256.HashData(buffer.AsSpan(0, headRead + tailRead));
            return Convert.ToHexString(hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("DuplicateFinder", $"Skipped unreadable file '{filePath}': {ex.Message}");
            return null;
        }
    }

    private async Task<string?> ComputeFullSha256Async(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                StreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[StreamBufferSize];
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, StreamBufferSize), cancellationToken)) > 0)
            {
                sha256.AppendData(buffer.AsSpan(0, read));
            }

            return Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("DuplicateFinder", $"Skipped unreadable file '{filePath}': {ex.Message}");
            return null;
        }
    }
}
