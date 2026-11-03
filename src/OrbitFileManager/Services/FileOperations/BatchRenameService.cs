using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OrbitFileManager.Services.FileOperations;

public enum BatchCaseMode
{
    KeepOriginal = 0,
    Lowercase = 1,
    Uppercase = 2,
    TitleCase = 3,
    SentenceCase = 4
}

public sealed class BatchRenameOptions
{
    public string BaseNameOverride { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public string FindText { get; set; } = string.Empty;
    public string ReplaceText { get; set; } = string.Empty;
    public bool UseRegex { get; set; }
    public bool CaseSensitiveFind { get; set; }
    public string RemoveCharacters { get; set; } = string.Empty;
    public BatchCaseMode Capitalization { get; set; } = BatchCaseMode.KeepOriginal;

    public bool EnableNumbering { get; set; }
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    public int NumberDigits { get; set; } = 3;
    public string NumberSeparator { get; set; } = "_";

    public bool PreserveExtension { get; set; } = true;
}

public sealed class BatchRenamePreviewItem
{
    public required string OriginalFullPath { get; init; }
    public required string ParentDirectory { get; init; }
    public required bool IsDirectory { get; init; }
    public required string OriginalName { get; init; }
    public required string NewName { get; set; }
    public bool IsChanged => !string.Equals(OriginalName, NewName, StringComparison.Ordinal);
    public bool HasError { get; set; }
    public string StatusMessage { get; set; } = "Ready";

    public string TargetFullPath => Path.Combine(ParentDirectory, NewName);
}

public sealed record BatchRenamePlan(
    IReadOnlyList<BatchRenamePreviewItem> Items,
    int ChangedCount,
    int ErrorCount,
    string? GlobalValidationError)
{
    public bool CanApply => ChangedCount > 0 && ErrorCount == 0 && string.IsNullOrEmpty(GlobalValidationError);
}

public sealed record RenamePair(string PreviousFullPath, string NewFullPath, bool IsDirectory);

public sealed class BatchRenameService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly StructuredLogger _logger;
    private readonly Stack<IReadOnlyList<RenamePair>> _undoStack = new();

    public bool CanUndo => _undoStack.Count > 0;

    public BatchRenameService(StructuredLogger logger)
    {
        _logger = logger;
    }

    public BatchRenamePlan BuildPlan(IReadOnlyList<string> sourcePaths, BatchRenameOptions options)
    {
        Regex? compiledRegex = null;
        if (!string.IsNullOrEmpty(options.FindText) && options.UseRegex)
        {
            try
            {
                RegexOptions rxOpts = RegexOptions.CultureInvariant;
                if (!options.CaseSensitiveFind)
                    rxOpts |= RegexOptions.IgnoreCase;
                compiledRegex = new Regex(options.FindText, rxOpts, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return new BatchRenamePlan([], 0, 1, $"Invalid Regular Expression: {ex.Message}");
            }
        }

        var items = new List<BatchRenamePreviewItem>(sourcePaths.Count);
        var sourceSet = new HashSet<string>(sourcePaths.Select(PathValidator.NormalizePath), StringComparer.OrdinalIgnoreCase);

        int currentNumber = options.NumberStart;
        int step = options.NumberStep == 0 ? 1 : options.NumberStep;
        int digits = Math.Clamp(options.NumberDigits, 1, 10);

        foreach (string rawPath in sourcePaths)
        {
            string fullPath = PathValidator.NormalizePath(rawPath);
            bool isDir = Directory.Exists(fullPath);
            string parentDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string originalName = Path.GetFileName(fullPath);

            bool splitExt = options.PreserveExtension && !isDir;
            string workingStem = splitExt ? Path.GetFileNameWithoutExtension(originalName) : originalName;
            string preservedExt = splitExt ? Path.GetExtension(originalName) : string.Empty;

            string? transformError = null;

            try
            {
                if (!string.IsNullOrEmpty(options.BaseNameOverride))
                {
                    workingStem = options.BaseNameOverride;
                }
                else if (!string.IsNullOrEmpty(options.FindText))
                {
                    if (compiledRegex is not null)
                    {
                        workingStem = compiledRegex.Replace(workingStem, options.ReplaceText);
                    }
                    else
                    {
                        StringComparison cmp = options.CaseSensitiveFind
                            ? StringComparison.Ordinal
                            : StringComparison.OrdinalIgnoreCase;
                        workingStem = workingStem.Replace(options.FindText, options.ReplaceText, cmp);
                    }
                }

                if (!string.IsNullOrEmpty(options.RemoveCharacters))
                {
                    var removeSet = options.RemoveCharacters.ToHashSet();
                    var sb = new StringBuilder(workingStem.Length);
                    foreach (char c in workingStem)
                    {
                        if (!removeSet.Contains(c))
                            sb.Append(c);
                    }
                    workingStem = sb.ToString();
                }

                workingStem = ApplyCase(workingStem, options.Capitalization);
                workingStem = options.Prefix + workingStem + options.Suffix;

                if (options.EnableNumbering)
                {
                    string numStr = currentNumber.ToString($"D{digits}", CultureInfo.InvariantCulture);
                    string sep = string.IsNullOrEmpty(workingStem) ? string.Empty : options.NumberSeparator;
                    workingStem = $"{workingStem}{sep}{numStr}";
                    currentNumber += step;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                transformError = "Regex evaluation timed out.";
            }
            catch (Exception ex)
            {
                transformError = ex.Message;
            }

            string candidateName = workingStem + preservedExt;
            var previewItem = new BatchRenamePreviewItem
            {
                OriginalFullPath = fullPath,
                ParentDirectory = parentDir,
                IsDirectory = isDir,
                OriginalName = originalName,
                NewName = candidateName
            };

            if (transformError is not null)
            {
                previewItem.HasError = true;
                previewItem.StatusMessage = transformError;
            }
            else
            {
                var (valid, err) = PathValidator.ValidateEntryName(candidateName);
                if (!valid)
                {
                    previewItem.HasError = true;
                    previewItem.StatusMessage = err ?? "Invalid filename";
                }
                else if (!previewItem.IsChanged)
                {
                    previewItem.StatusMessage = "Unchanged";
                }
            }

            items.Add(previewItem);
        }

        // Detect duplicate target paths inside the batch (case-insensitive per Windows NTFS rules)
        var targetGroups = items
            .GroupBy(i => PathValidator.NormalizePath(i.TargetFullPath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var dupGroup in targetGroups)
        {
            foreach (var dup in dupGroup)
            {
                dup.HasError = true;
                dup.StatusMessage = "Duplicate target filename in batch";
            }
        }

        // Detect collisions with existing files on disk outside the batch
        foreach (var item in items)
        {
            if (item.HasError || !item.IsChanged)
                continue;

            string normalizedTarget = PathValidator.NormalizePath(item.TargetFullPath);
            if (!sourceSet.Contains(normalizedTarget) && PathValidator.EntryExists(normalizedTarget))
            {
                item.HasError = true;
                item.StatusMessage = "Target name already exists in folder";
            }
        }

        int changedCount = items.Count(i => i.IsChanged);
        int errorCount = items.Count(i => i.HasError);

        return new BatchRenamePlan(items, changedCount, errorCount, null);
    }

    public Task<(bool Success, int RenamedCount, string? ErrorMessage)> ExecutePlanAsync(BatchRenamePlan plan)
    {
        return Task.Run<(bool, int, string?)>(() =>
        {
            if (!plan.CanApply)
                return (false, 0, "The rename plan contains unresolved validation conflicts or no changes.");

            var toRename = plan.Items.Where(i => i.IsChanged).ToList();
            var stagedMoves = new List<(BatchRenamePreviewItem Item, string TempPath)>(toRename.Count);

            try
            {
                // Phase 1: Move all participating items to collision-free temporary sibling names
                foreach (var item in toRename)
                {
                    string tempPath = Path.Combine(item.ParentDirectory, $".orbit_ren_{Guid.NewGuid():N}");
                    MoveEntry(item.OriginalFullPath, tempPath, item.IsDirectory);
                    stagedMoves.Add((item, tempPath));
                }

                // Phase 2: Move from temporary names to validated final target names
                var completedPairs = new List<RenamePair>(stagedMoves.Count);
                foreach (var (item, tempPath) in stagedMoves)
                {
                    MoveEntry(tempPath, item.TargetFullPath, item.IsDirectory);
                    completedPairs.Add(new RenamePair(item.OriginalFullPath, item.TargetFullPath, item.IsDirectory));
                }

                lock (_undoStack)
                {
                    _undoStack.Push(completedPairs);
                }

                _logger.Info("BatchRename", $"Successfully renamed {completedPairs.Count} items.");
                return (true, completedPairs.Count, null);
            }
            catch (Exception ex)
            {
                _logger.Error("BatchRename", "Batch rename failed; rolling back staged items.", ex);

                // Roll back any items still sitting at their temporary path
                for (int i = stagedMoves.Count - 1; i >= 0; i--)
                {
                    var (item, tempPath) = stagedMoves[i];
                    try
                    {
                        if (PathValidator.EntryExists(tempPath) && !PathValidator.EntryExists(item.OriginalFullPath))
                            MoveEntry(tempPath, item.OriginalFullPath, item.IsDirectory);
                    }
                    catch { }
                }

                return (false, 0, $"Batch rename aborted safely: {ex.Message}");
            }
        });
    }

    public Task<(bool Success, int RestoredCount, string? ErrorMessage)> UndoLastBatchRenameWrapperAsync() => UndoLastBatchAsync();

    public Task<(bool Success, int RestoredCount, string? ErrorMessage)> UndoLastBatchAsync()
    {
        return Task.Run<(bool, int, string?)>(() =>
        {
            IReadOnlyList<RenamePair>? batch;
            lock (_undoStack)
            {
                if (_undoStack.Count == 0)
                    return (false, 0, "No batch rename operation is available to undo.");
                batch = _undoStack.Pop();
            }

            // Validate before mutating: every renamed item must still exist, and original slots must be free
            var currentSet = new HashSet<string>(batch.Select(b => PathValidator.NormalizePath(b.NewFullPath)), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in batch)
            {
                if (!PathValidator.EntryExists(pair.NewFullPath))
                    return (false, 0, $"Cannot undo: '{Path.GetFileName(pair.NewFullPath)}' was moved or deleted externally.");

                string origNorm = PathValidator.NormalizePath(pair.PreviousFullPath);
                if (!currentSet.Contains(origNorm) && PathValidator.EntryExists(origNorm))
                    return (false, 0, $"Cannot undo: '{Path.GetFileName(pair.PreviousFullPath)}' now exists in the folder.");
            }

            var staged = new List<(RenamePair Pair, string TempPath)>(batch.Count);
            try
            {
                foreach (var pair in batch)
                {
                    string parent = Path.GetDirectoryName(pair.NewFullPath) ?? string.Empty;
                    string tempPath = Path.Combine(parent, $".orbit_undo_{Guid.NewGuid():N}");
                    MoveEntry(pair.NewFullPath, tempPath, pair.IsDirectory);
                    staged.Add((pair, tempPath));
                }

                foreach (var (pair, tempPath) in staged)
                {
                    MoveEntry(tempPath, pair.PreviousFullPath, pair.IsDirectory);
                }

                _logger.Info("BatchRename", $"Undid batch rename of {batch.Count} items.");
                return (true, batch.Count, null);
            }
            catch (Exception ex)
            {
                _logger.Error("BatchRename", "Failed during batch rename undo.", ex);
                return (false, 0, $"Undo encountered an error: {ex.Message}");
            }
        });
    }

    private static void MoveEntry(string source, string destination, bool isDirectory)
    {
        if (isDirectory)
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }

    private static string ApplyCase(string input, BatchCaseMode mode)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return mode switch
        {
            BatchCaseMode.Lowercase => input.ToLowerInvariant(),
            BatchCaseMode.Uppercase => input.ToUpperInvariant(),
            BatchCaseMode.TitleCase => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(input.ToLowerInvariant()),
            BatchCaseMode.SentenceCase => char.ToUpperInvariant(input[0]) + (input.Length > 1 ? input[1..].ToLowerInvariant() : string.Empty),
            _ => input
        };
    }
}
