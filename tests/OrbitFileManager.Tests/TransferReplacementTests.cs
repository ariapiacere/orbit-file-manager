using OrbitFileManager.Models;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.Settings;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class TransferReplacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"OrbitReplaceTests_{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task ReplaceAll_ReplacesFolderAndSiblingVideos(bool queued, bool move, bool reverseOrder)
    {
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        string[] files = ["scene/assets/data.bin", "scene/nested.mp4", "scene.mp4", "other.mp4"];
        var expectedHashes = new Dictionary<string, byte[]>();
        foreach (string relative in files)
        {
            string source = Path.Combine(src, relative);
            string target = Path.Combine(dst, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] contents = new byte[300_000]; // crosses several copy buffers
            Random.Shared.NextBytes(contents);
            File.WriteAllBytes(source, contents);
            File.WriteAllBytes(target, new byte[contents.Length]);
            // Identical size/timestamp must never imply that the existing bytes are already correct.
            DateTime stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(source, stamp);
            File.SetLastWriteTimeUtc(target, stamp);
            expectedHashes[relative] = SHA256.HashData(contents);
        }
        File.WriteAllText(Path.Combine(dst, "scene", "destination-only.txt"), "keep");
        File.SetAttributes(Path.Combine(dst, "scene.mp4"), FileAttributes.ReadOnly);

        string[] sources = [Path.Combine(src, "scene"), Path.Combine(src, "scene.mp4"), Path.Combine(src, "other.mp4")];
        if (reverseOrder)
            Array.Reverse(sources);
        int prompts = 0;
        Task<ConflictDecision> Resolve(FileConflictContext _)
        {
            prompts++;
            return Task.FromResult(new ConflictDecision(ConflictResolution.Overwrite, true));
        }

        var logger = new StructuredLogger(new AppPathsService(Path.Combine(_root, "app")));
        if (queued)
        {
            using var queue = new FileOperationQueue(logger) { ConflictResolver = Resolve };
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.OnJobFinishedRefresh = () => { finished.TrySetResult(); return Task.CompletedTask; };
            var job = queue.EnqueueTransfer(sources, dst, move);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Empty(job.Errors);
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.Equal(files.Length, job.ProcessedFiles);
        }
        else
        {
            var service = new FileOperationService(logger);
            var result = await service.TransferItemsAsync(sources, dst, move, Resolve);
            Assert.Empty(result.Errors);
            Assert.Equal(sources.Length, result.SucceededCount);
        }

        Assert.Equal(1, prompts);
        foreach (var (relative, hash) in expectedHashes)
        {
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(Path.Combine(dst, relative))));
            Assert.Equal(!move, File.Exists(Path.Combine(src, relative)));
        }
        Assert.Equal("keep", File.ReadAllText(Path.Combine(dst, "scene", "destination-only.txt")));
        Assert.Empty(Directory.GetFiles(dst, "*.orbit_part_*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyReplace_RetriesVideoTemporarilyLockedByReader(bool queued)
    {
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        string folder = Path.Combine(src, "scene");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(folder, "data.txt"), "new folder contents");
        string video = Path.Combine(src, "scene.mp4");
        string target = Path.Combine(dst, "scene.mp4");
        File.WriteAllText(video, "new video contents");
        File.WriteAllText(target, "old video contents");
        var logger = new StructuredLogger(new AppPathsService(Path.Combine(_root, "app")));

        // Shell thumbnail/property readers can allow reads while temporarily denying replacement.
        var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task releaseReader = Task.Run(async () => { await Task.Delay(400); reader.Dispose(); });
        try
        {
            Task<ConflictDecision> Resolve(FileConflictContext _) =>
                Task.FromResult(new ConflictDecision(ConflictResolution.Overwrite, true));
            if (queued)
            {
                using var queue = new FileOperationQueue(logger) { ConflictResolver = Resolve };
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                queue.OnJobFinishedRefresh = () => { finished.TrySetResult(); return Task.CompletedTask; };
                var job = queue.EnqueueTransfer([folder, video], dst, isMove: false);
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Empty(job.Errors);
                Assert.Equal(JobStatus.Completed, job.Status);
                Assert.Equal(2, job.ProcessedFiles);
            }
            else
            {
                var result = await new FileOperationService(logger).TransferItemsAsync([folder, video], dst, false, Resolve);
                Assert.Empty(result.Errors);
                Assert.Equal(2, result.SucceededCount);
            }
            Assert.Equal("new folder contents", File.ReadAllText(Path.Combine(dst, "scene", "data.txt")));
            Assert.Equal("new video contents", File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(dst, "*.orbit_part_*", SearchOption.AllDirectories));
        }
        finally
        {
            await releaseReader;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyReplace_PreservesOldVideoAndReportsPersistentLock(bool queued)
    {
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        string source = Path.Combine(src, "scene.mp4");
        string target = Path.Combine(dst, "scene.mp4");
        File.WriteAllText(source, "new video");
        File.WriteAllText(target, "old video");
        using var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var logger = new StructuredLogger(new AppPathsService(Path.Combine(_root, "app")));
        Task<ConflictDecision> Resolve(FileConflictContext _) =>
            Task.FromResult(new ConflictDecision(ConflictResolution.Overwrite, true));
        if (queued)
        {
            using var queue = new FileOperationQueue(logger) { ConflictResolver = Resolve };
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.OnJobFinishedRefresh = () => { finished.TrySetResult(); return Task.CompletedTask; };
            var job = queue.EnqueueTransfer([source], dst, isMove: false);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(JobStatus.CompletedWithErrors, job.Status);
            Assert.Equal(0, job.ProcessedFiles);
            Assert.Contains(target, job.ErrorDetails);
            Assert.Contains("existing destination file was kept", job.ErrorDetails);
        }
        else
        {
            var result = await new FileOperationService(logger).TransferItemsAsync([source], dst, false, Resolve);
            Assert.Equal(0, result.SucceededCount);
            Assert.Contains(target, Assert.Single(result.Errors));
        }
        Assert.Equal("old video", File.ReadAllText(target));
        Assert.Equal("new video", File.ReadAllText(source));
        Assert.Empty(Directory.GetFiles(dst, "*.orbit_part_*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyReplace_CanCancelWhileWaitingForVideoReader(bool queued)
    {
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        string source = Path.Combine(src, "scene.mp4");
        string target = Path.Combine(dst, "scene.mp4");
        File.WriteAllText(source, "new video");
        File.WriteAllText(target, "old video");
        using var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var logger = new StructuredLogger(new AppPathsService(Path.Combine(_root, "app")));
        Task<ConflictDecision> Resolve(FileConflictContext _) =>
            Task.FromResult(new ConflictDecision(ConflictResolution.Overwrite, true));
        if (queued)
        {
            using var queue = new FileOperationQueue(logger) { ConflictResolver = Resolve };
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.OnJobFinishedRefresh = () => { finished.TrySetResult(); return Task.CompletedTask; };
            var job = queue.EnqueueTransfer([source], dst, isMove: false);
            await Task.Delay(300);
            job.Cancel();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobStatus.Cancelled, job.Status);
            Assert.Equal(0, job.ProcessedFiles);
        }
        else
        {
            using var cts = new CancellationTokenSource(300);
            var result = await new FileOperationService(logger).TransferItemsAsync([source], dst, false, Resolve,
                cancellationToken: cts.Token);
            Assert.True(result.IsCancelled);
            Assert.Equal(0, result.SucceededCount);
        }
        Assert.Equal("old video", File.ReadAllText(target));
        Assert.Equal("new video", File.ReadAllText(source));
        Assert.Empty(Directory.GetFiles(dst, "*.orbit_part_*"));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
