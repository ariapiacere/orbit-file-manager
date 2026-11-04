using OrbitFileManager.Models;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.ViewModels;
using System.IO;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class Phase2NavigationAndSessionTests : IDisposable
{
    private readonly string _tempRoot;

    public Phase2NavigationAndSessionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitPhase2Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task PaneViewModel_SupportsPinLockReorderAndRestoreClosedTab()
    {
        string dirA = Path.Combine(_tempRoot, "Alpha");
        string dirB = Path.Combine(_tempRoot, "Beta");
        string dirC = Path.Combine(_tempRoot, "Gamma");
        string subA = Path.Combine(dirA, "Child");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(dirB);
        Directory.CreateDirectory(dirC);

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_app"));
        var logger = new StructuredLogger(paths);
        var settings = new SettingsService(paths, logger);
        settings.Load();
        var browser = new FileBrowserService(new ShellIntegrationService(), logger);

        using var pane = new BrowserPaneViewModel(
            "Left",
            browser,
            settings,
            _ => { },
            (_, _, _, _) => { },
            _ => { });

        await pane.NewTabAsync(dirA);
        await pane.NewTabAsync(dirB);
        await pane.NewTabAsync(dirC);
        Assert.Equal(3, pane.Tabs.Count);

        // Lock first tab (dirA): attempting to close must be blocked
        var tabA = pane.Tabs[0];
        pane.ToggleLockTab(tabA);
        Assert.True(tabA.IsLocked);
        pane.CloseTab(tabA);
        Assert.Equal(3, pane.Tabs.Count);

        // Navigating locked tabA to external dirB spawns a new tab instead of overwriting locked root
        pane.ActiveTab = tabA;
        await pane.NavigateActiveOrSpawnIfLockedAsync(dirB);
        Assert.Equal(4, pane.Tabs.Count);
        Assert.Equal(dirA, tabA.CurrentPath);

        // Navigating locked tabA into its own child subA navigates in-place
        pane.ActiveTab = tabA;
        await pane.NavigateActiveOrSpawnIfLockedAsync(subA);
        Assert.Equal(subA, tabA.CurrentPath);

        // Close unlocked tabC and restore via RestoreClosedTabAsync
        var tabC = pane.Tabs.First(t => t.CurrentPath == dirC);
        pane.CloseTab(tabC);
        Assert.Equal(3, pane.Tabs.Count);
        Assert.True(pane.CanRestoreClosedTab);

        await pane.RestoreClosedTabAsync();
        Assert.Equal(4, pane.Tabs.Count);
        Assert.Equal(dirC, pane.ActiveTab?.CurrentPath);
    }

    [Fact]
    public void SessionAndMetadataServices_RoundTripDualPaneAndItemTags()
    {
        var paths = new AppPathsService(Path.Combine(_tempRoot, "_portable"));
        var logger = new StructuredLogger(paths);
        var sessionSvc = new SessionService(paths, logger);

        var session = new AppSessionState
        {
            IsDualPane = true,
            SplitOrientation = SplitOrientationMode.Horizontal,
            VerticalSplitRatio = 0.3,
            HorizontalSplitRatio = 0.65,
            ActivePaneId = "Right",
            IsSyncNavigationEnabled = true,
            LeftPane = new PaneSessionState
            {
                ActiveTabIndex = 0,
                Tabs = [new TabSessionState { Path = _tempRoot, CustomLabel = "Renders", IsPinned = true }]
            },
            RightPane = new PaneSessionState
            {
                ActiveTabIndex = 0,
                Tabs = [new TabSessionState { Path = _tempRoot, IsLocked = true, LockedRootPath = _tempRoot }]
            }
        };

        Assert.True(sessionSvc.SaveSession(session));
        var loaded = sessionSvc.LoadSession();
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsDualPane);
        Assert.Equal(SplitOrientationMode.Horizontal, loaded.SplitOrientation);
        Assert.Equal(0.3, loaded.VerticalSplitRatio);
        Assert.Equal(0.65, loaded.HorizontalSplitRatio);
        Assert.Equal("Renders", loaded.LeftPane.Tabs[0].CustomLabel);
        Assert.True(loaded.RightPane.Tabs[0].IsLocked);

        string sampleFile = Path.Combine(_tempRoot, "shot.exr");
        File.WriteAllText(sampleFile, "exr");

        var metaSvc = new MetadataStoreService(paths, logger);
        metaSvc.SetColorTag(sampleFile, "#FF3B82F6");
        metaSvc.SetNote(sampleFile, "Approved final take");

        var reloadedMetaSvc = new MetadataStoreService(paths, logger);
        var meta = reloadedMetaSvc.GetMetadata(sampleFile);
        Assert.NotNull(meta);
        Assert.Equal("#FF3B82F6", meta!.ColorTagHex);
        Assert.Equal("Approved final take", meta.Note);
    }

    [Fact]
    public async Task TabViewModel_RaisesFolderNavigatedOnDirectoryChangeButNotOnSameFolderRefresh()
    {
        string dir1 = Path.Combine(_tempRoot, "NavOne");
        string dir2 = Path.Combine(_tempRoot, "NavTwo");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_navEvent"));
        var logger = new StructuredLogger(paths);
        var settings = new SettingsService(paths, logger);
        settings.Load();
        var browser = new FileBrowserService(new ShellIntegrationService(), logger);

        using var tab = new BrowserTabViewModel(browser, settings, BrowserViewMode.Details);
        int folderNavigatedCount = 0;
        tab.FolderNavigated += _ => folderNavigatedCount++;

        await tab.NavigateAsync(dir1);
        Assert.Equal(1, folderNavigatedCount);

        // Same-folder refresh should not reset scroll via FolderNavigated
        await tab.RefreshAsync(preserveSelection: true);
        Assert.Equal(1, folderNavigatedCount);

        // Changing folder must raise FolderNavigated so horizontal scroll resets to left
        await tab.NavigateAsync(dir2);
        Assert.Equal(2, folderNavigatedCount);
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
