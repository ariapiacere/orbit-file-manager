using OrbitFileManager.Models;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.ViewModels;
using System.IO;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class SelectionAfterTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OrbitSelectionTests_" + Guid.NewGuid().ToString("N"));

    public SelectionAfterTransferTests() => Directory.CreateDirectory(_root);

    private (BrowserTabViewModel Tab, StructuredLogger Logger) NewTab()
    {
        var paths = new AppPathsService(Path.Combine(_root, "_app"));
        var logger = new StructuredLogger(paths);
        var settings = new SettingsService(paths, logger);
        settings.Load();
        var browser = new FileBrowserService(new ShellIntegrationService(), logger);
        return (new BrowserTabViewModel(browser, settings, BrowserViewMode.Details), logger);
    }

    [Fact]
    public async Task Queue_ReportsWhatEachJobCreated()
    {
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(Path.Combine(src, "folder"));
        File.WriteAllText(Path.Combine(src, "folder", "inner.txt"), "inner");
        File.WriteAllText(Path.Combine(src, "clip.mp4"), "new");
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(dst, "clip.mp4"), "old");

        var (_, logger) = NewTab();
        using var queue = new FileOperationQueue(logger)
        {
            ConflictResolver = _ => Task.FromResult(new ConflictDecision(ConflictResolution.KeepBoth, true))
        };
        var finished = new TaskCompletionSource<FileOperationJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.OnJobFinishedRefresh = job => { finished.TrySetResult(job); return Task.CompletedTask; };

        queue.EnqueueTransfer([Path.Combine(src, "folder"), Path.Combine(src, "clip.mp4")], dst, isMove: false);
        FileOperationJob done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, done.CreatedPaths.Count);
        Assert.Equal(Path.Combine(dst, "folder"), done.CreatedPaths[0], ignoreCase: true);
        // "Keep both" wrote a renamed copy; that one is what gets selected, not the file that was already there.
        Assert.NotEqual(Path.Combine(dst, "clip.mp4"), done.CreatedPaths[1], StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(done.CreatedPaths[1]));
    }

    [Fact]
    public async Task Refresh_KeepsEverySelectedItem()
    {
        string dir = Path.Combine(_root, "keep");
        Directory.CreateDirectory(dir);
        foreach (string name in new[] { "a.txt", "b.txt", "c.txt", "d.txt" })
            File.WriteAllText(Path.Combine(dir, name), name);

        var (tab, _) = NewTab();
        using (tab)
        {
            await tab.NavigateAsync(dir);
            var pick = tab.DisplayedItems.Where(i => i.Name is "d.txt" or "b.txt").OrderByDescending(i => i.Name).ToList();
            tab.UpdateSelection(pick); // d.txt focused, b.txt also selected

            await tab.RefreshAsync(preserveSelection: true);

            Assert.Equal(["b.txt", "d.txt"], tab.SelectedItems.Select(i => i.Name).OrderBy(n => n));
            Assert.Equal("d.txt", tab.SelectedItem?.Name);
            Assert.All(tab.SelectedItems, item => Assert.Contains(item, tab.DisplayedItems)); // new instances, not stale ones
        }
    }

    [Fact]
    public async Task Refresh_SelectsTheGivenPathsAndSkipsMissingOnes()
    {
        string dir = Path.Combine(_root, "pick");
        Directory.CreateDirectory(dir);
        foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(dir, name), name);

        var (tab, _) = NewTab();
        using (tab)
        {
            await tab.NavigateAsync(dir);
            tab.UpdateSelection([tab.DisplayedItems.First(i => i.Name == "a.txt")]);

            await tab.RefreshAsync(preserveSelection: false, explicitSelectPaths:
                [Path.Combine(dir, "c.txt"), Path.Combine(dir, "gone.txt"), Path.Combine(dir, "B.TXT")]);

            Assert.Equal(["b.txt", "c.txt"], tab.SelectedItems.Select(i => i.Name).OrderBy(n => n));
            Assert.Equal("c.txt", tab.SelectedItem?.Name); // first requested path that exists
            Assert.Equal(2, tab.SelectedCount);
        }
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
