using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrbitFileManager.Models;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows;

namespace OrbitFileManager.Services.FileOperations;

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    CompletedWithErrors = 4,
    Cancelled = 5
}

public partial class FileOperationJob : ObservableObject
{
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private readonly CancellationTokenSource _cts = new();

    public Guid Id { get; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required IReadOnlyList<string> SourcePaths { get; init; }
    public required string DestinationDirectory { get; init; }
    public required bool IsMove { get; init; }

    [ObservableProperty]
    private JobStatus _status = JobStatus.Queued;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _currentFileName = "Waiting in queue...";

    [ObservableProperty]
    private int _processedFiles;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private long _completedBytes;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private string _summaryText = "Queued";

    public List<string> Errors { get; } = [];

    public CancellationToken CancellationToken => _cts.Token;
    public bool IsActive => Status is JobStatus.Queued or JobStatus.Running or JobStatus.Paused;
    public bool CanPauseResume => Status is JobStatus.Running or JobStatus.Paused;
    public string PauseResumeLabel => Status == JobStatus.Paused ? "Resume" : "Pause";

    partial void OnStatusChanged(JobStatus value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanPauseResume));
        OnPropertyChanged(nameof(PauseResumeLabel));
    }

    public void WaitIfPaused()
    {
        _pauseGate.Wait(_cts.Token);
    }

    [RelayCommand]
    public void TogglePause()
    {
        if (Status == JobStatus.Running)
        {
            _pauseGate.Reset();
            Status = JobStatus.Paused;
            SummaryText = "Paused";
        }
        else if (Status == JobStatus.Paused)
        {
            Status = JobStatus.Running;
            _pauseGate.Set();
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        _pauseGate.Set();
        _cts.Cancel();
    }
}

public sealed class FileOperationQueue : ObservableObject, IDisposable
{
    private const int CopyBufferSize = 131_072;

    private readonly StructuredLogger _logger;
    private readonly Channel<FileOperationJob> _channel = Channel.CreateUnbounded<FileOperationJob>();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _workerTask;
    private bool _disposed;

    public ObservableCollection<FileOperationJob> Jobs { get; } = [];

    public Func<FileConflictContext, Task<ConflictDecision>>? ConflictResolver { get; set; }
    public Func<Task>? OnJobFinishedRefresh { get; set; }

    private int _activeJobCount;
    public int ActiveJobCount
    {
        get => _activeJobCount;
        private set => SetProperty(ref _activeJobCount, value);
    }

    public FileOperationQueue(StructuredLogger logger)
    {
        _logger = logger;
        _workerTask = Task.Run(ProcessQueueLoopAsync);
    }

    public FileOperationJob EnqueueTransfer(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool isMove)
    {
        string destName = Path.GetFileName(destinationDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(destName))
            destName = destinationDirectory;

        var job = new FileOperationJob
        {
            Title = $"{(isMove ? "Move" : "Copy")} {sourcePaths.Count:N0} item{(sourcePaths.Count == 1 ? "" : "s")} → {destName}",
            SourcePaths = sourcePaths.ToList(),
            DestinationDirectory = PathValidator.NormalizePath(destinationDirectory),
            IsMove = isMove
        };

        DispatchUi(() =>
        {
            Jobs.Insert(0, job);
            RecalculateActiveCount();
        });

        _channel.Writer.TryWrite(job);
        return job;
    }

    public void ClearFinishedJobs()
    {
        DispatchUi(() =>
        {
            var finished = Jobs.Where(j => !j.IsActive).ToList();
            foreach (var f in finished)
                Jobs.Remove(f);
            RecalculateActiveCount();
        });
    }

    private async Task ProcessQueueLoopAsync()
    {
        try
        {
            await foreach (FileOperationJob job in _channel.Reader.ReadAllAsync(_shutdownCts.Token))
            {
                if (job.CancellationToken.IsCancellationRequested)
                {
                    UpdateJob(job, () =>
                    {
                        job.Status = JobStatus.Cancelled;
                        job.SummaryText = "Cancelled before start";
                        RecalculateActiveCount();
                    });
                    continue;
                }

                await ExecuteJobAsync(job);

                if (OnJobFinishedRefresh is not null)
                {
                    try { await OnJobFinishedRefresh(); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExecuteJobAsync(FileOperationJob job)
    {
        UpdateJob(job, () =>
        {
            job.Status = JobStatus.Running;
            job.CurrentFileName = "Calculating total size...";
            job.SummaryText = "Scanning items...";
            RecalculateActiveCount();
        });

        if (!Directory.Exists(job.DestinationDirectory))
        {
            UpdateJob(job, () =>
            {
                job.Errors.Add($"Destination '{job.DestinationDirectory}' does not exist.");
                job.Status = JobStatus.CompletedWithErrors;
                job.SummaryText = "Destination unavailable";
                RecalculateActiveCount();
            });
            return;
        }

        try
        {
            var (totalFiles, totalBytes) = MeasureWorkload(job.SourcePaths, job.CancellationToken);
            UpdateJob(job, () =>
            {
                job.TotalFiles = Math.Max(1, totalFiles);
                job.TotalBytes = totalBytes;
            });

            var stopwatch = Stopwatch.StartNew();
            long transferredBytes = 0;
            int processedFiles = 0;
            ConflictResolution? stickyConflict = null;
            long lastUiReportMs = 0;

            void ReportProgress(string currentName, bool force = false)
            {
                long elapsedMs = stopwatch.ElapsedMilliseconds;
                if (!force && elapsedMs - lastUiReportMs < 80)
                    return;

                lastUiReportMs = elapsedMs;
                double pct = totalBytes > 0
                    ? Math.Clamp((double)transferredBytes * 100.0 / totalBytes, 0, 100)
                    : Math.Clamp((double)processedFiles * 100.0 / Math.Max(1, totalFiles), 0, 100);

                double elapsedSec = Math.Max(0.1, stopwatch.Elapsed.TotalSeconds);
                double bytesPerSec = transferredBytes / elapsedSec;
                string speed = transferredBytes > 0 ? $"{FileSizeFormatter.FormatBytes((long)bytesPerSec)}/s" : string.Empty;

                string eta = string.Empty;
                if (bytesPerSec > 1024 && totalBytes > transferredBytes)
                {
                    double remSec = (totalBytes - transferredBytes) / bytesPerSec;
                    eta = remSec < 60
                        ? $"~{Math.Ceiling(remSec):0}s left"
                        : $"~{Math.Ceiling(remSec / 60):0}m left";
                }

                UpdateJob(job, () =>
                {
                    job.CurrentFileName = currentName;
                    job.ProcessedFiles = processedFiles;
                    job.CompletedBytes = transferredBytes;
                    job.ProgressPercent = pct;
                    job.SpeedText = speed;
                    job.EtaText = eta;
                    job.SummaryText = $"{processedFiles:N0}/{job.TotalFiles:N0} files • {FileSizeFormatter.FormatBytes(transferredBytes)} / {FileSizeFormatter.FormatBytes(totalBytes)}";
                });
            }

            foreach (string rawSource in job.SourcePaths)
            {
                job.CancellationToken.ThrowIfCancellationRequested();
                job.WaitIfPaused();

                string sourcePath = PathValidator.NormalizePath(rawSource);
                bool isDir = Directory.Exists(sourcePath);
                bool isFile = !isDir && File.Exists(sourcePath);

                if (!isDir && !isFile)
                {
                    job.Errors.Add($"Source not found: '{sourcePath}'");
                    continue;
                }

                string itemName = Path.GetFileName(sourcePath);
                string? srcParent = Path.GetDirectoryName(sourcePath);
                bool sameFolder = srcParent is not null && PathValidator.PathsEqual(srcParent, job.DestinationDirectory);

                if (sameFolder && job.IsMove)
                    continue;

                string targetPath;
                bool overwrite = false;

                if (sameFolder && !job.IsMove)
                {
                    targetPath = PathValidator.GenerateUniqueDestinationPath(job.DestinationDirectory, itemName, isDir, useCopyLabel: true);
                }
                else
                {
                    targetPath = Path.Combine(job.DestinationDirectory, itemName);

                    if (isDir && PathValidator.IsSameOrSubPath(sourcePath, targetPath))
                    {
                        job.Errors.Add($"Cannot {(job.IsMove ? "move" : "copy")} '{itemName}' into itself or its subfolder.");
                        continue;
                    }

                    if (PathValidator.EntryExists(targetPath))
                    {
                        ConflictResolution res;
                        if (stickyConflict.HasValue)
                        {
                            res = stickyConflict.Value;
                        }
                        else if (ConflictResolver is not null)
                        {
                            var ctx = BuildConflict(sourcePath, targetPath, isDir);
                            var decision = await ConflictResolver(ctx);
                            if (decision.ApplyToAll && decision.Resolution != ConflictResolution.Cancel)
                                stickyConflict = decision.Resolution;
                            res = decision.Resolution;
                        }
                        else
                        {
                            res = ConflictResolution.Skip;
                        }

                        if (res == ConflictResolution.Cancel)
                            throw new OperationCanceledException();

                        if (res == ConflictResolution.Skip)
                            continue;

                        if (res == ConflictResolution.KeepBoth)
                            targetPath = PathValidator.GenerateUniqueDestinationPath(job.DestinationDirectory, itemName, isDir, useCopyLabel: false);
                        else if (res == ConflictResolution.Overwrite)
                            overwrite = true;
                    }
                }

                try
                {
                    if (isFile)
                    {
                        TransferFileWithProgress(
                            sourcePath,
                            targetPath,
                            job.IsMove,
                            overwrite,
                            job,
                            bytesDelta =>
                            {
                                transferredBytes += bytesDelta;
                                ReportProgress(itemName);
                            });
                        processedFiles++;
                        ReportProgress(itemName, force: true);
                    }
                    else
                    {
                        TransferDirectoryWithProgress(
                            new DirectoryInfo(sourcePath),
                            targetPath,
                            job.IsMove,
                            overwrite,
                            job,
                            (fileName, bytesDelta, fileDone) =>
                            {
                                transferredBytes += bytesDelta;
                                if (fileDone) processedFiles++;
                                ReportProgress(fileName, force: fileDone);
                            });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error("Queue", $"Error transferring '{sourcePath}'.", ex);
                    job.Errors.Add($"'{itemName}': {ex.Message}");
                }
            }

            UpdateJob(job, () =>
            {
                job.ProgressPercent = 100;
                job.EtaText = string.Empty;
                if (job.Errors.Count > 0)
                {
                    job.Status = JobStatus.CompletedWithErrors;
                    job.SummaryText = $"Completed with {job.Errors.Count} error(s)";
                }
                else
                {
                    job.Status = JobStatus.Completed;
                    job.SummaryText = $"Completed ({processedFiles:N0} file{(processedFiles == 1 ? "" : "s")})";
                }
                RecalculateActiveCount();
            });
        }
        catch (OperationCanceledException)
        {
            UpdateJob(job, () =>
            {
                job.Status = JobStatus.Cancelled;
                job.EtaText = string.Empty;
                job.SummaryText = "Cancelled";
                RecalculateActiveCount();
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Queue", "Unexpected queue job failure.", ex);
            UpdateJob(job, () =>
            {
                job.Errors.Add(ex.Message);
                job.Status = JobStatus.CompletedWithErrors;
                job.SummaryText = $"Failed: {ex.Message}";
                RecalculateActiveCount();
            });
        }
    }

    private static void TransferFileWithProgress(
        string sourceFile,
        string destFile,
        bool isMove,
        bool overwrite,
        FileOperationJob job,
        Action<long> onBytesCopied)
    {
        job.CancellationToken.ThrowIfCancellationRequested();
        job.WaitIfPaused();

        var srcInfo = new FileInfo(sourceFile);
        long fileLen = srcInfo.Exists ? srcInfo.Length : 0;

        string? srcRoot = Path.GetPathRoot(sourceFile);
        string? dstRoot = Path.GetPathRoot(destFile);
        bool sameVolume = !string.IsNullOrEmpty(srcRoot) &&
                          string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);

        if (isMove && sameVolume)
        {
            if (overwrite && File.Exists(destFile))
            {
                FileAttributes attrs = File.GetAttributes(destFile);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(destFile, attrs & ~FileAttributes.ReadOnly);
            }

            File.Move(sourceFile, destFile, overwrite: overwrite);
            onBytesCopied(fileLen);
            return;
        }

        string destDir = Path.GetDirectoryName(destFile) ?? throw new IOException("Invalid destination path.");
        string tempFile = Path.Combine(destDir, $".{Path.GetFileName(destFile)}.orbit_part_{Guid.NewGuid():N}");

        try
        {
            using (var srcStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan))
            using (var tmpStream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                byte[] buffer = new byte[CopyBufferSize];
                int read;
                while ((read = srcStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    job.CancellationToken.ThrowIfCancellationRequested();
                    job.WaitIfPaused();
                    tmpStream.Write(buffer, 0, read);
                    onBytesCopied(read);
                }
                tmpStream.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(tempFile, srcInfo.LastWriteTimeUtc);

            if (overwrite && File.Exists(destFile))
            {
                FileAttributes attrs = File.GetAttributes(destFile);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(destFile, attrs & ~FileAttributes.ReadOnly);
            }

            File.Move(tempFile, destFile, overwrite: overwrite);

            if (isMove)
                File.Delete(sourceFile);
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            throw;
        }
    }

    private static void TransferDirectoryWithProgress(
        DirectoryInfo source,
        string destDir,
        bool isMove,
        bool overwrite,
        FileOperationJob job,
        Action<string, long, bool> onProgress)
    {
        job.CancellationToken.ThrowIfCancellationRequested();
        job.WaitIfPaused();

        Directory.CreateDirectory(destDir);

        if ((source.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (isMove)
                Directory.Delete(source.FullName, recursive: false);
            return;
        }

        foreach (FileInfo file in source.EnumerateFiles())
        {
            job.CancellationToken.ThrowIfCancellationRequested();
            string targetFile = Path.Combine(destDir, file.Name);
            if (!overwrite && File.Exists(targetFile))
                continue;

            TransferFileWithProgress(
                file.FullName,
                targetFile,
                isMove: false,
                overwrite: overwrite,
                job,
                delta => onProgress(file.Name, delta, false));

            onProgress(file.Name, 0, true);
        }

        foreach (DirectoryInfo sub in source.EnumerateDirectories())
        {
            job.CancellationToken.ThrowIfCancellationRequested();
            string targetSub = Path.Combine(destDir, sub.Name);

            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.CreateDirectory(targetSub);
                continue;
            }

            TransferDirectoryWithProgress(sub, targetSub, isMove: false, overwrite: overwrite, job, onProgress);
        }

        if (isMove)
            Directory.Delete(source.FullName, recursive: true);
    }

    private static (int FileCount, long TotalBytes) MeasureWorkload(IReadOnlyList<string> paths, CancellationToken ct)
    {
        int count = 0;
        long bytes = 0;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

        foreach (string raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            string p = PathValidator.NormalizePath(raw);
            if (File.Exists(p))
            {
                count++;
                try { bytes += new FileInfo(p).Length; } catch { }
            }
            else if (Directory.Exists(p))
            {
                var di = new DirectoryInfo(p);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                foreach (FileInfo fi in di.EnumerateFiles("*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    count++;
                    try { bytes += fi.Length; } catch { }
                }
            }
        }

        return (count, bytes);
    }

    private static FileConflictContext BuildConflict(string src, string dst, bool isDir)
    {
        long? sLen = null, dLen = null;
        DateTime sMod = DateTime.MinValue, dMod = DateTime.MinValue;
        try
        {
            if (!isDir)
            {
                var sf = new FileInfo(src); sLen = sf.Length; sMod = sf.LastWriteTime;
                if (File.Exists(dst)) { var df = new FileInfo(dst); dLen = df.Length; dMod = df.LastWriteTime; }
            }
            else
            {
                sMod = Directory.GetLastWriteTime(src);
                dMod = Directory.GetLastWriteTime(dst);
            }
        }
        catch { }

        return new FileConflictContext(src, dst, isDir, sLen, sMod, dLen, dMod);
    }

    private void RecalculateActiveCount()
    {
        ActiveJobCount = Jobs.Count(j => j.IsActive);
    }

    private static void UpdateJob(FileOperationJob job, Action action) => DispatchUi(action);

    private static void DispatchUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdownCts.Cancel();
        _channel.Writer.TryComplete();
        foreach (var j in Jobs)
        {
            if (j.IsActive)
                j.Cancel();
        }
        _shutdownCts.Dispose();
    }
}
