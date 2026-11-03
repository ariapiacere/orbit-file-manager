using OrbitFileManager.Models;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.Settings;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class Phase3QueueAndBatchRenameTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly StructuredLogger _logger;

    public Phase3QueueAndBatchRenameTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitPhase3Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        var paths = new AppPathsService(Path.Combine(_tempRoot, "_app"));
        _logger = new StructuredLogger(paths);
    }

    [Fact]
    public async Task BatchRename_GeneratesSequentialNamesExecutesSafelyAndSupportsUndo()
    {
        string dir = Path.Combine(_tempRoot, "RenameBatch");
        Directory.CreateDirectory(dir);

        string f1 = Path.Combine(dir, "image.png");
        string f2 = Path.Combine(dir, "image_2.png");
        string f3 = Path.Combine(dir, "image_3.png");
        File.WriteAllText(f1, "one");
        File.WriteAllText(f2, "two");
        File.WriteAllText(f3, "three");

        var svc = new BatchRenameService(_logger);
        var opts = new BatchRenameOptions
        {
            BaseNameOverride = "Character",
            EnableNumbering = true,
            NumberStart = 1,
            NumberStep = 1,
            NumberDigits = 3,
            NumberSeparator = "_",
            PreserveExtension = true
        };

        var plan = svc.BuildPlan([f1, f2, f3], opts);
        Assert.True(plan.CanApply);
        Assert.Equal("Character_001.png", plan.Items[0].NewName);
        Assert.Equal("Character_002.png", plan.Items[1].NewName);
        Assert.Equal("Character_003.png", plan.Items[2].NewName);

        var (execOk, count, execErr) = await svc.ExecutePlanAsync(plan);
        Assert.True(execOk, execErr);
        Assert.Equal(3, count);
        Assert.True(File.Exists(Path.Combine(dir, "Character_001.png")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(dir, "Character_002.png")));

        // Verify full Undo restores exact original filenames and contents
        Assert.True(svc.CanUndo);
        var (undoOk, restoredCount, undoErr) = await svc.UndoLastBatchAsync();
        Assert.True(undoOk, undoErr);
        Assert.Equal(3, restoredCount);
        Assert.True(File.Exists(f1));
        Assert.True(File.Exists(f2));
        Assert.True(File.Exists(f3));
        Assert.Equal("two", File.ReadAllText(f2));
    }

    [Fact]
    public async Task BatchRename_DetectsDuplicateTargetsAndSupportsCyclicSwap()
    {
        string dir = Path.Combine(_tempRoot, "SwapDir");
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "part_1.txt");
        string b = Path.Combine(dir, "part_2.txt");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");

        var svc = new BatchRenameService(_logger);

        // Colliding constant override without numbering must flag duplicate target error
        var badPlan = svc.BuildPlan([a, b], new BatchRenameOptions { BaseNameOverride = "SameName", EnableNumbering = false });
        Assert.False(badPlan.CanApply);
        Assert.Equal(2, badPlan.ErrorCount);

        // Cyclic swap: part_2 -> part_01 (or reverse order numbering: [b, a] -> part_1.txt, part_2.txt!)
        var swapPlan = svc.BuildPlan([b, a], new BatchRenameOptions
        {
            BaseNameOverride = "part",
            EnableNumbering = true,
            NumberStart = 1,
            NumberDigits = 1,
            NumberSeparator = "_"
        });

        Assert.True(swapPlan.CanApply);
        var (ok, _, err) = await svc.ExecutePlanAsync(swapPlan);
        Assert.True(ok, err);
        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("A", File.ReadAllText(b));
    }

    [Fact]
    public async Task FileOperationQueue_ExecutesBackgroundJobsAndResolvesConflicts()
    {
        string src = Path.Combine(_tempRoot, "QueueSrc");
        string dst = Path.Combine(_tempRoot, "QueueDst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        string fileA = Path.Combine(src, "alpha.bin");
        byte[] payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(fileA, payload);

        // Pre-seed conflict in destination
        File.WriteAllText(Path.Combine(dst, "alpha.bin"), "old");

        using var queue = new FileOperationQueue(_logger)
        {
            ConflictResolver = _ => Task.FromResult(new ConflictDecision(ConflictResolution.KeepBoth, true))
        };

        var job = queue.EnqueueTransfer([fileA], dst, isMove: false);

        var sw = Stopwatch.StartNew();
        while (job.IsActive && sw.ElapsedMilliseconds < 5000)
            await Task.Delay(30);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(payload.Length, job.CompletedBytes);
        Assert.True(File.Exists(Path.Combine(dst, "alpha (2).bin")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }
}
