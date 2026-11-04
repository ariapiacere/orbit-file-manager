using OrbitFileManager.Models;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Settings;
using System.ComponentModel;
using System.IO;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class FolderWatchTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitFolderWatchTests_" + Guid.NewGuid().ToString("N"));

    public FolderWatchTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task LoadDirectory_FolderStampMovesWhenAnEntryIsAdded()
    {
        string dir = Path.Combine(_tempRoot, "Stamp");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_paths"));
        var browser = new FileBrowserService(new ShellIntegrationService(), new StructuredLogger(paths));

        DirectoryLoadResult first = await browser.LoadDirectoryAsync(dir, false, false, false, CancellationToken.None);
        Assert.True(first.Success);
        Assert.NotNull(first.FolderStampUtc);

        await Task.Delay(50); // stay clear of the file system's timestamp granularity
        File.WriteAllText(Path.Combine(dir, "b.txt"), "b");

        DirectoryLoadResult second = await browser.LoadDirectoryAsync(dir, false, false, false, CancellationToken.None);
        Assert.NotNull(second.FolderStampUtc);
        Assert.NotEqual(first.FolderStampUtc, second.FolderStampUtc);
        Assert.Equal(2, second.Items.Count);
    }

    [Fact]
    public void FileEntryItem_SizeAndModifiedTimeUpdateInPlace()
    {
        var item = new FileEntryItem
        {
            Name = "clip.mp4",
            FullPath = @"C:\clip.mp4",
            SizeBytes = 1024,
            FormattedSize = "1 KB",
            TypeDescription = "MP4 File",
            AttributesSummary = "A",
            ModifiedTime = new DateTime(2026, 11, 4, 10, 0, 0)
        };

        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.SizeBytes = 5_000_000;
        item.FormattedSize = "4.8 MB";
        item.ModifiedTime = new DateTime(2026, 11, 4, 10, 5, 0);

        Assert.Contains(nameof(FileEntryItem.SizeBytes), changed);
        Assert.Contains(nameof(FileEntryItem.FormattedSize), changed);
        Assert.Contains(nameof(FileEntryItem.ModifiedTime), changed);
        Assert.Contains(nameof(FileEntryItem.ModifiedText), changed);
        Assert.Equal("2026-11-04 10:05", item.ModifiedText);
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
