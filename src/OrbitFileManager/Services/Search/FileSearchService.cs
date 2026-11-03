using OrbitFileManager.Models;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrbitFileManager.Services.Search;

public sealed class SearchRequest
{
    public required string QueryText { get; init; }
    public required IReadOnlyList<string> RootLocations { get; init; }
    public bool Recursive { get; init; } = true;
    public bool CaseSensitive { get; init; }
    public bool IncludeHidden { get; init; }
}

public sealed class SavedSearchPreset
{
    public required string Name { get; set; }
    public required string QueryText { get; set; }
    public bool Recursive { get; set; } = true;
    public bool CaseSensitive { get; set; }
    public bool IncludeHidden { get; set; }
}

public sealed class FileSearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppPathsService _paths;
    private readonly ShellIntegrationService _shell;
    private readonly StructuredLogger _logger;

    public string PresetsFilePath => Path.Combine(_paths.DataDirectory, "search_presets.json");

    public FileSearchService(AppPathsService paths, ShellIntegrationService shell, StructuredLogger logger)
    {
        _paths = paths;
        _shell = shell;
        _logger = logger;
    }

    public List<SavedSearchPreset> LoadPresets()
    {
        try
        {
            if (File.Exists(PresetsFilePath))
            {
                string json = File.ReadAllText(PresetsFilePath);
                return JsonSerializer.Deserialize<List<SavedSearchPreset>>(json, JsonOptions) ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Search", "Unable to load search_presets.json.", ex);
        }

        return
        [
            new SavedSearchPreset { Name = "Large Files (>100 MB)", QueryText = "size:>100MB", Recursive = true },
            new SavedSearchPreset { Name = "Modified Last 7 Days", QueryText = "modified:<7d", Recursive = true },
            new SavedSearchPreset { Name = "Images (PNG / JPG)", QueryText = "ext:png OR ext:jpg", Recursive = true }
        ];
    }

    public void SavePresets(IEnumerable<SavedSearchPreset> presets)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            string temp = PresetsFilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(presets.ToList(), JsonOptions));
            File.Move(temp, PresetsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Search", "Unable to save search_presets.json.", ex);
        }
    }

    public Task<(int TotalMatched, string? ErrorMessage)> ExecuteSearchAsync(
        SearchRequest request,
        Action<IReadOnlyList<FileEntryItem>> onBatchReady,
        CancellationToken cancellationToken)
    {
        return Task.Run<(int, string?)>(() =>
        {
            var (ast, parseError) = SearchQueryParser.TryParse(request.QueryText);
            if (ast is null)
                return (0, parseError ?? "Invalid search query.");

            DateTime utcNow = DateTime.UtcNow;
            var batch = new List<FileEntryItem>(64);
            int totalMatched = 0;
            var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawRoot in request.RootLocations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string root = PathValidator.NormalizePath(rawRoot);
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    continue;

                SearchDirectoryIterative(
                    root,
                    request,
                    ast,
                    utcNow,
                    visitedDirs,
                    batch,
                    ref totalMatched,
                    onBatchReady,
                    cancellationToken);
            }

            if (batch.Count > 0)
            {
                onBatchReady(batch.ToList());
                batch.Clear();
            }

            return (totalMatched, null);
        }, cancellationToken);
    }

    private void SearchDirectoryIterative(
        string startDir,
        SearchRequest request,
        SearchNode ast,
        DateTime utcNow,
        HashSet<string> visitedDirs,
        List<FileEntryItem> batch,
        ref int totalMatched,
        Action<IReadOnlyList<FileEntryItem>> onBatchReady,
        CancellationToken ct)
    {
        var dirQueue = new Queue<string>();
        dirQueue.Enqueue(startDir);

        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            RecurseSubdirectories = false,
            AttributesToSkip = request.IncludeHidden ? FileAttributes.System : (FileAttributes.Hidden | FileAttributes.System)
        };

        while (dirQueue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string currentDir = dirQueue.Dequeue();
            if (!visitedDirs.Add(currentDir))
                continue;

            DirectoryInfo dirInfo;
            try
            {
                dirInfo = new DirectoryInfo(currentDir);
                if (!dirInfo.Exists)
                    continue;
            }
            catch
            {
                continue;
            }

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = dirInfo.EnumerateFileSystemInfos("*", enumOpts);
            }
            catch
            {
                continue;
            }

            foreach (FileSystemInfo fsi in children)
            {
                ct.ThrowIfCancellationRequested();

                FileAttributes attrs;
                try { attrs = fsi.Attributes; } catch { continue; }

                bool isDir = (attrs & FileAttributes.Directory) != 0;
                bool isHidden = (attrs & FileAttributes.Hidden) != 0;
                bool isReparse = (attrs & FileAttributes.ReparsePoint) != 0;

                if (!request.IncludeHidden && isHidden)
                    continue;

                if (isDir && request.Recursive && !isReparse)
                    dirQueue.Enqueue(fsi.FullName);

                long size = 0;
                if (!isDir && fsi is FileInfo fi)
                {
                    try { size = fi.Length; } catch { }
                }

                string ext = isDir ? string.Empty : fsi.Extension;
                DateTime createdUtc = SafeDate(() => fsi.CreationTimeUtc);
                DateTime modifiedUtc = SafeDate(() => fsi.LastWriteTimeUtc);

                var candidate = new SearchCandidate(
                    fsi.Name,
                    ext,
                    fsi.FullName,
                    isDir,
                    isHidden,
                    size,
                    createdUtc,
                    modifiedUtc);

                bool matched;
                try
                {
                    matched = ast.Matches(in candidate, request.CaseSensitive, utcNow);
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new InvalidOperationException("Search aborted: Regular expression timed out on input.");
                }

                if (matched)
                {
                    var (icon, typeName) = _shell.GetEntryMetadata(fsi.FullName, ext, isDir, largeIcon: false);
                    batch.Add(new FileEntryItem
                    {
                        Name = fsi.Name,
                        FullPath = fsi.FullName,
                        Extension = ext,
                        IsDirectory = isDir,
                        IsHidden = isHidden,
                        IsSystem = (attrs & FileAttributes.System) != 0,
                        IsReadOnly = (attrs & FileAttributes.ReadOnly) != 0,
                        IsSymbolicLink = isReparse,
                        SizeBytes = isDir ? null : size,
                        FormattedSize = isDir ? string.Empty : FileSizeFormatter.FormatBytes(size),
                        TypeDescription = typeName,
                        CreatedTime = createdUtc == DateTime.MinValue ? DateTime.MinValue : createdUtc.ToLocalTime(),
                        ModifiedTime = modifiedUtc == DateTime.MinValue ? DateTime.MinValue : modifiedUtc.ToLocalTime(),
                        AttributesSummary = FileSizeFormatter.FormatAttributes(attrs),
                        Icon = icon
                    });

                    totalMatched++;
                    if (batch.Count >= 50)
                    {
                        onBatchReady(batch.ToList());
                        batch.Clear();
                    }
                }
            }
        }
    }

    private static DateTime SafeDate(Func<DateTime> fn)
    {
        try { return fn(); } catch { return DateTime.MinValue; }
    }
}
