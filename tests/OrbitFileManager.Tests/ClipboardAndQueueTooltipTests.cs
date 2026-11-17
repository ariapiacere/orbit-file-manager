using OrbitFileManager.Models;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class ClipboardAndQueueTooltipTests : IDisposable
{
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);
    private static readonly Func<TimeSpan, Task> NoWait = _ => Task.CompletedTask;
    private static readonly Func<bool> AlwaysFree = () => true;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OrbitClipboardQueueTests_" + Guid.NewGuid().ToString("N"));

    public ClipboardAndQueueTooltipTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void BusyClipboardErrorsAreRecognised()
    {
        Assert.True(ClipboardAccess.IsBusy(new COMException("OpenClipboard Failed", ClipboardCantOpen)));
        Assert.True(ClipboardAccess.IsBusy(new ExternalException("CloseClipboard Failed", unchecked((int)0x800401D4))));
        Assert.False(ClipboardAccess.IsBusy(new COMException("other", unchecked((int)0x80004005))));
        Assert.False(ClipboardAccess.IsBusy(new IOException("disk")));
    }

    [Fact]
    public async Task BusyClipboardIsRetriedUntilItFrees()
    {
        int calls = 0;
        bool ok = await ClipboardAccess.TryRunAsync(() =>
        {
            if (++calls < 3)
                throw new COMException("OpenClipboard Failed", ClipboardCantOpen);
        }, NoWait, AlwaysFree);

        Assert.True(ok);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ClipboardThatStaysBusyReportsFalseInsteadOfThrowing()
    {
        int calls = 0;
        var waits = new List<TimeSpan>();
        bool ok = await ClipboardAccess.TryRunAsync(() =>
        {
            calls++;
            throw new COMException("OpenClipboard Failed", ClipboardCantOpen);
        }, d => { waits.Add(d); return Task.CompletedTask; }, AlwaysFree);

        Assert.False(ok);
        Assert.Equal(ClipboardAccess.RetryDelays.Length + 1, calls);
        Assert.InRange(waits.Sum(w => w.TotalSeconds), 2.0, 3.0);
    }

    [Fact]
    public async Task OtherClipboardErrorsStillSurface()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClipboardAccess.TryRunAsync(() => throw new InvalidOperationException("bug"), NoWait, AlwaysFree));
    }

    [Fact]
    public async Task HeldClipboardIsNotTouchedUntilItIsFree()
    {
        int checks = 0, calls = 0;
        bool ok = await ClipboardAccess.TryRunAsync(() => calls++, NoWait, () => ++checks > 3);

        Assert.True(ok);
        Assert.Equal(4, checks);
        Assert.Equal(1, calls); // never called (and never blocked inside WPF) while another program held it
    }

    [Fact]
    public async Task ClipboardHeldTooLongGivesUpWithoutCallingWpf()
    {
        int calls = 0;
        bool ok = await ClipboardAccess.TryRunAsync(() => calls++, NoWait, () => false);

        Assert.False(ok);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task QueueJobToolTipListsEveryItemAndWhatHappened()
    {
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        foreach (string name in new[] { "BG Rain.png", "BG Sofa.png", "keep.txt" })
            File.WriteAllText(Path.Combine(src, name), name);
        File.WriteAllText(Path.Combine(dst, "BG Sofa.png"), "old");
        File.WriteAllText(Path.Combine(dst, "keep.txt"), "old");

        using var queue = new FileOperationQueue(new StructuredLogger(new AppPathsService(Path.Combine(_root, "app"))))
        {
            // Sofa: keep both (renamed copy); keep.txt: skip.
            ConflictResolver = ctx => Task.FromResult(new ConflictDecision(
                Path.GetFileName(ctx.SourcePath) == "keep.txt" ? ConflictResolution.Skip : ConflictResolution.KeepBoth, false))
        };
        var finished = new TaskCompletionSource<FileOperationJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.OnJobFinishedRefresh = job => { finished.TrySetResult(job); return Task.CompletedTask; };

        string[] sources =
        [
            Path.Combine(src, "BG Rain.png"), Path.Combine(src, "BG Sofa.png"),
            Path.Combine(src, "keep.txt"), Path.Combine(src, "missing.png")
        ];
        var queued = queue.EnqueueTransfer(sources, dst, isMove: false);
        Assert.Contains("BG Rain.png  (waiting)", queued.ItemsToolTip);

        FileOperationJob done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        string[] lines = done.ItemsToolTip.Split(Environment.NewLine);

        Assert.Equal("Copy 4 items", lines[0]);
        Assert.Equal($"From: {src}", lines[1]);
        Assert.Equal($"To: {dst}", lines[2]);
        Assert.Contains("BG Rain.png", lines);
        Assert.Contains(lines, l => l.StartsWith("BG Sofa.png  →  BG Sofa", StringComparison.Ordinal) && l.EndsWith(".png", StringComparison.Ordinal));
        Assert.Contains("keep.txt  (skipped)", lines);
        Assert.Contains("missing.png  (failed)", lines);
        Assert.Equal(2, done.CreatedPaths.Count);
    }

    [Fact]
    public void QueueJobToolTipShortensLongLists()
    {
        var job = new FileOperationJob
        {
            Title = "Copy 45 items",
            SourcePaths = Enumerable.Range(0, 45).Select(i => Path.Combine(_root, "src", $"f{i:D2}.txt")).ToList(),
            DestinationDirectory = Path.Combine(_root, "dst"),
            IsMove = true
        };

        string[] lines = job.ItemsToolTip.Split(Environment.NewLine);
        Assert.Equal("Move 45 items", lines[0]);
        Assert.Equal("…and 15 more", lines[^1]);
        Assert.Equal(30, lines.Count(l => l.EndsWith("(waiting)", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { }
    }
}
