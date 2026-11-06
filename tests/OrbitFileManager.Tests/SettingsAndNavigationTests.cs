using OrbitFileManager.Models;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Preview;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.ViewModels;
using System.IO;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class SettingsAndNavigationTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsAndNavigationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitNavTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void SettingsAndFavorites_PersistInPortableDataFolder()
    {
        string portableAppRoot = Path.Combine(_tempRoot, "PortableInstall");
        var paths = new AppPathsService(portableAppRoot);

        Assert.True(paths.IsPortableWritable);
        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));

        var logger = new StructuredLogger(paths);
        var settingsSvc = new SettingsService(paths, logger);
        settingsSvc.Load();

        settingsSvc.Current.Theme = AppThemeMode.Light;
        settingsSvc.Current.ShowHiddenFiles = true;
        settingsSvc.Current.LastOpenTabs = [_tempRoot];
        settingsSvc.RecordRecentFolder(_tempRoot);
        Assert.True(settingsSvc.Save());

        var reloadedSvc = new SettingsService(paths, logger);
        var reloaded = reloadedSvc.Load();
        Assert.Equal(AppThemeMode.Light, reloaded.Theme);
        Assert.True(reloaded.ShowHiddenFiles);
        Assert.Contains(_tempRoot, reloaded.LastOpenTabs);
        Assert.Contains(_tempRoot, reloaded.RecentFolders);

        var favSvc = new FavoritesService(paths, logger);
        Assert.True(favSvc.SaveFavorites([new FavoriteItem { Name = "Work", Path = _tempRoot }]));
        var favs = favSvc.LoadFavorites();
        Assert.Single(favs);
        Assert.Equal("Work", favs[0].Name);
    }

    [Fact]
    public void BrowserTab_SortsUnderscorePrefixesBeforeNumbersAndLetters()
    {
        string[] names = ["sc_10", "0X", "_xxxx", "sc_2", "__first", "_2", "_10"];
        var items = names.Select(name => new FileEntryItem
        {
            Name = name,
            FullPath = Path.Combine(_tempRoot, name),
            IsDirectory = true,
            TypeDescription = "Folder",
            FormattedSize = string.Empty,
            AttributesSummary = string.Empty
        }).ToList();

        string[] expected = ["__first", "_2", "_10", "_xxxx", "0X", "sc_2", "sc_10"];
        Assert.Equal(expected, BrowserTabViewModel.SortEntries(items, SortColumn.Name, false).Select(i => i.Name));
        Assert.Equal(expected.Reverse(), BrowserTabViewModel.SortEntries(items, SortColumn.Name, true).Select(i => i.Name));
    }

    [Fact]
    public async Task BrowserTab_SupportsHistoryNavigationSortingAndQuickFilter()
    {
        string dirA = Path.Combine(_tempRoot, "FolderA");
        string dirB = Path.Combine(_tempRoot, "FolderB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        Directory.CreateDirectory(Path.Combine(dirA, "SubFolder"));

        File.WriteAllText(Path.Combine(dirA, "shot_10.png"), "10");
        File.WriteAllText(Path.Combine(dirA, "shot_2.png"), "2");
        File.WriteAllText(Path.Combine(dirA, "notes.txt"), "hello");

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_app"));
        var logger = new StructuredLogger(paths);
        var settings = new SettingsService(paths, logger);
        settings.Load();
        var shell = new ShellIntegrationService();
        var browser = new FileBrowserService(shell, logger);

        using var tab = new BrowserTabViewModel(browser, settings, BrowserViewMode.Details);

        Assert.True(await tab.NavigateAsync(dirA));
        Assert.Equal(4, tab.TotalItemCount);

        // Directories first, then natural numeric filename order (shot_2 before shot_10)
        Assert.Equal("SubFolder", tab.DisplayedItems[0].Name);
        Assert.Equal("notes.txt", tab.DisplayedItems[1].Name);
        Assert.Equal("shot_2.png", tab.DisplayedItems[2].Name);
        Assert.Equal("shot_10.png", tab.DisplayedItems[3].Name);

        // Quick filter narrows displayed items without mutating TotalItemCount
        tab.QuickFilterText = "shot";
        Assert.Equal(4, tab.TotalItemCount);
        Assert.Equal(2, tab.DisplayedItemCount);
        tab.QuickFilterText = string.Empty;

        // History navigation: dirA -> dirB -> Back (dirA) -> Forward (dirB) -> Up (_tempRoot)
        Assert.True(await tab.NavigateAsync(dirB));
        Assert.True(tab.CanGoBack);

        await tab.GoBackAsync();
        Assert.Equal(dirA, tab.CurrentPath);
        Assert.True(tab.CanGoForward);

        await tab.GoForwardAsync();
        Assert.Equal(dirB, tab.CurrentPath);

        await tab.GoUpAsync();
        Assert.Equal(_tempRoot, tab.CurrentPath);
    }

    [Fact]
    public async Task PreviewService_TruncatesLargeTextAndNeverLocksFile()
    {
        string bigTextPath = Path.Combine(_tempRoot, "large.log");
        File.WriteAllText(bigTextPath, new string('X', 120 * 1024)); // 120 KB (> 64 KB cap)

        var shell = new ShellIntegrationService();
        var previewSvc = new PreviewService(shell);

        var entry = new FileEntryItem
        {
            Name = "large.log",
            FullPath = bigTextPath,
            Extension = ".log",
            IsDirectory = false,
            SizeBytes = 120 * 1024,
            FormattedSize = "120 KB",
            TypeDescription = "LOG File",
            AttributesSummary = "—"
        };

        var preview = await previewSvc.LoadPreviewAsync(entry, CancellationToken.None);
        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.True(preview.IsTextTruncated);
        Assert.NotNull(preview.TextPreview);

        // Ensure preview left zero open file handles so immediate delete/rename succeeds
        string renamedPath = Path.Combine(_tempRoot, "large_renamed.log");
        File.Move(bigTextPath, renamedPath);
        Assert.True(File.Exists(renamedPath));
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
