using OrbitFileManager.Models;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Xunit;

namespace OrbitFileManager.Tests;

/// <summary>
/// Tabs must survive a crash, a forced kill or a Windows restart: sessions.json is written atomically while Orbit
/// runs, a damaged file falls back to sessions.json.bak, and restoring never drops a tab or waits on a slow folder.
/// </summary>
public sealed class SessionPersistenceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly AppPathsService _paths;
    private readonly StructuredLogger _logger;

    public SessionPersistenceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitSessionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _paths = new AppPathsService(Path.Combine(_tempRoot, "_app"));
        _logger = new StructuredLogger(_paths);
    }

    private SessionService NewService() => new(_paths, _logger);

    private static AppSessionState SessionWithLabel(string path, string label) => new()
    {
        LeftPane = new PaneSessionState { Tabs = [new TabSessionState { Path = path, CustomLabel = label }] }
    };

    private static string? FirstLabel(AppSessionState? state) => state?.LeftPane.Tabs.FirstOrDefault()?.CustomLabel;

    // ─── Save / load ──────────────────────────────────────────────────────────

    [Fact]
    public void Save_ReplacesAtomicallyAndKeepsThePreviousSessionAsBackup()
    {
        var service = NewService();

        Assert.True(service.SaveSession(SessionWithLabel(_tempRoot, "first")));
        Assert.True(File.Exists(service.SessionFilePath));
        Assert.False(File.Exists(service.BackupFilePath));

        Assert.True(service.SaveSession(SessionWithLabel(_tempRoot, "second")));

        Assert.Equal("second", FirstLabel(service.LoadSession()));
        Assert.Contains("\"first\"", File.ReadAllText(service.BackupFilePath));
        Assert.False(File.Exists(service.SessionFilePath + ".tmp"));
    }

    [Fact]
    public void Load_RoundTripsEverythingATabHolds()
    {
        var service = NewService();
        var saved = new AppSessionState
        {
            IsDualPane = true,
            SplitOrientation = SplitOrientationMode.Horizontal,
            VerticalSplitRatio = 0.35,
            ActivePaneId = "Right",
            LeftPane = new PaneSessionState
            {
                ActiveTabIndex = 1,
                Tabs =
                [
                    new TabSessionState { Path = @"C:\Alpha", IsPinned = true, CustomLabel = "Renders" },
                    new TabSessionState
                    {
                        Path = @"D:\Beta\Shots",
                        IsLocked = true,
                        LockedRootPath = @"D:\Beta",
                        ViewMode = BrowserViewMode.Thumbnails,
                        SortColumn = SortColumn.Modified,
                        SortDescending = true,
                        GroupByType = true,
                        SelectedPath = @"D:\Beta\Shots\sh010.exr",
                        BackHistory = [@"D:\", @"D:\Beta"],
                        ForwardHistory = [@"D:\Beta\Shots\sh020"]
                    }
                ]
            },
            RightPane = new PaneSessionState { Tabs = [new TabSessionState { Path = @"\\nas\projects\Job 42" }] }
        };

        Assert.True(service.SaveSession(saved));
        SessionLoadResult result = NewService().LoadSessionWithRecovery();

        Assert.Equal(SessionLoadSource.Primary, result.Source);
        AppSessionState loaded = Assert.IsType<AppSessionState>(result.Session);
        Assert.True(loaded.IsDualPane);
        Assert.Equal("Right", loaded.ActivePaneId);
        Assert.Equal(1, loaded.LeftPane.ActiveTabIndex);
        Assert.True(loaded.LeftPane.Tabs[0].IsPinned);
        TabSessionState tab = loaded.LeftPane.Tabs[1];
        Assert.True(tab.IsLocked);
        Assert.Equal(@"D:\Beta", tab.LockedRootPath);
        Assert.Equal(BrowserViewMode.Thumbnails, tab.ViewMode);
        Assert.Equal(SortColumn.Modified, tab.SortColumn);
        Assert.True(tab.SortDescending);
        Assert.True(tab.GroupByType);
        Assert.Equal(@"D:\Beta\Shots\sh010.exr", tab.SelectedPath);
        Assert.Equal(new[] { @"D:\", @"D:\Beta" }, tab.BackHistory);
        Assert.Equal(new[] { @"D:\Beta\Shots\sh020" }, tab.ForwardHistory);
        Assert.Equal(@"\\nas\projects\Job 42", loaded.RightPane.Tabs[0].Path);
    }

    [Fact]
    public void Load_SessionSavedByAnOlderVersion_StillLoads()
    {
        // 1.1.x wrote no selection or history.
        var service = NewService();
        Directory.CreateDirectory(Path.GetDirectoryName(service.SessionFilePath)!);
        File.WriteAllText(service.SessionFilePath, """
            {
              "isDualPane": false,
              "leftPane": { "tabs": [ { "path": "C:\\Work", "viewMode": "List", "sortColumn": "Size" } ], "activeTabIndex": 0 },
              "rightPane": { "tabs": [], "activeTabIndex": 0 }
            }
            """);

        AppSessionState loaded = Assert.IsType<AppSessionState>(service.LoadSession());
        Assert.Equal(@"C:\Work", loaded.LeftPane.Tabs[0].Path);
        Assert.Equal(BrowserViewMode.List, loaded.LeftPane.Tabs[0].ViewMode);
        Assert.Null(loaded.LeftPane.Tabs[0].BackHistory);
    }

    // ─── Damaged files ────────────────────────────────────────────────────────

    public static TheoryData<byte[]> DamagedContents => new()
    {
        { System.Text.Encoding.UTF8.GetBytes("{ \"leftPane\": { \"tabs\": [ { \"path\": \"C:\\\\Wo") }, // cut off mid-write
        { new byte[512] },                                    // NUL-filled: power cut before the data reached the disk
        { Array.Empty<byte>() },                              // empty
        { System.Text.Encoding.UTF8.GetBytes("null") },       // valid JSON, no session
        { System.Text.Encoding.UTF8.GetBytes("<html>oops") }  // not JSON at all
    };

    [Theory]
    [MemberData(nameof(DamagedContents))]
    public void Load_DamagedSession_FallsBackToBackupAndSetsTheDamagedFileAside(byte[] damaged)
    {
        var service = NewService();
        service.SaveSession(SessionWithLabel(_tempRoot, "older"));
        service.SaveSession(SessionWithLabel(_tempRoot, "newer")); // .bak = "older"
        File.WriteAllBytes(service.SessionFilePath, damaged);

        var restarted = NewService();
        SessionLoadResult result = restarted.LoadSessionWithRecovery();

        Assert.Equal(SessionLoadSource.Backup, result.Source);
        Assert.Equal("older", FirstLabel(result.Session));
        Assert.NotNull(result.Problem);
        Assert.False(File.Exists(restarted.SessionFilePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(restarted.SessionFilePath)!, "sessions.json.corrupt-*"));

        // The next save must not rotate the damaged file into the backup.
        Assert.True(restarted.SaveSession(SessionWithLabel(_tempRoot, "after-recovery")));
        Assert.Equal("after-recovery", FirstLabel(NewService().LoadSession()));
        Assert.Contains("\"older\"", File.ReadAllText(restarted.BackupFilePath));
    }

    [Fact]
    public void Load_MissingSessionWithBackup_UsesBackup()
    {
        // A crash between the two renames of a save leaves only the backup.
        var service = NewService();
        service.SaveSession(SessionWithLabel(_tempRoot, "older"));
        service.SaveSession(SessionWithLabel(_tempRoot, "newer"));
        File.Delete(service.SessionFilePath);

        SessionLoadResult result = NewService().LoadSessionWithRecovery();

        Assert.Equal(SessionLoadSource.Backup, result.Source);
        Assert.Equal("older", FirstLabel(result.Session));
    }

    [Fact]
    public void Load_SessionAndBackupBothDamaged_StartsFreshWithoutThrowing()
    {
        var service = NewService();
        Directory.CreateDirectory(Path.GetDirectoryName(service.SessionFilePath)!);
        File.WriteAllText(service.SessionFilePath, "{ broken");
        File.WriteAllText(service.BackupFilePath, "also broken }");

        SessionLoadResult result = service.LoadSessionWithRecovery();

        Assert.Null(result.Session);
        Assert.Equal(SessionLoadSource.None, result.Source);
        Assert.NotNull(result.Problem);
        Assert.True(service.SaveSession(SessionWithLabel(_tempRoot, "fresh")));
        Assert.Equal("fresh", FirstLabel(NewService().LoadSession()));
    }

    [Fact]
    public void Load_NoSessionYet_ReturnsNoneWithoutProblem()
    {
        SessionLoadResult result = NewService().LoadSessionWithRecovery();
        Assert.Null(result.Session);
        Assert.Equal(SessionLoadSource.None, result.Source);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Load_RepairsNullPanesBlankTabsAndOutOfRangeIndexes()
    {
        var service = NewService();
        Directory.CreateDirectory(Path.GetDirectoryName(service.SessionFilePath)!);
        File.WriteAllText(service.SessionFilePath, """
            {
              "leftPane": null,
              "activePaneId": "Somewhere",
              "rightPane": { "tabs": [ null, { "path": "" }, { "path": "C:\\Kept", "backHistory": [ null, "C:\\" ] } ], "activeTabIndex": 9 }
            }
            """);

        AppSessionState loaded = Assert.IsType<AppSessionState>(service.LoadSession());

        Assert.Empty(loaded.LeftPane.Tabs);
        Assert.Equal("Left", loaded.ActivePaneId);
        TabSessionState kept = Assert.Single(loaded.RightPane.Tabs);
        Assert.Equal(@"C:\Kept", kept.Path);
        Assert.Equal(new[] { @"C:\" }, kept.BackHistory);
        Assert.Equal(0, loaded.RightPane.ActiveTabIndex);
    }

    // ─── Concurrent autosave writes ──────────────────────────────────────────

    [Fact]
    public void Write_AnOlderSnapshotNeverOverwritesANewerOne()
    {
        var service = NewService();
        long older = service.NextSequence();
        long newer = service.NextSequence();

        Assert.True(service.WriteSession(SessionWithLabel(_tempRoot, "newer"), newer, onlyIfChanged: true));
        Assert.True(service.WriteSession(SessionWithLabel(_tempRoot, "older"), older, onlyIfChanged: true));

        Assert.Equal("newer", FirstLabel(NewService().LoadSession()));
    }

    [Fact]
    public void Write_OnlyIfChanged_SkipsAStateThatDiffersOnlyInTimestamp()
    {
        var service = NewService();
        Assert.True(service.WriteSession(SessionWithLabel(_tempRoot, "same"), service.NextSequence(), onlyIfChanged: true));
        Assert.True(service.WriteSession(SessionWithLabel(_tempRoot, "same"), service.NextSequence(), onlyIfChanged: true));

        Assert.False(File.Exists(service.BackupFilePath)); // second write never happened, so nothing was rotated

        Assert.True(service.WriteSession(SessionWithLabel(_tempRoot, "changed"), service.NextSequence(), onlyIfChanged: true));
        Assert.True(File.Exists(service.BackupFilePath));
        Assert.Equal("changed", FirstLabel(NewService().LoadSession()));
    }

    [Fact]
    public void Write_ManyThreadsAtOnce_LeavesTheNewestCompleteSession()
    {
        var service = NewService();
        var snapshots = Enumerable.Range(0, 40)
            .Select(i => (State: SessionWithLabel(_tempRoot, $"state-{i:D2}"), Sequence: service.NextSequence()))
            .ToList();

        Parallel.ForEach(snapshots, s => service.WriteSession(s.State, s.Sequence, onlyIfChanged: true));

        Assert.Equal("state-39", FirstLabel(NewService().LoadSession()));
        Assert.False(File.Exists(service.SessionFilePath + ".tmp"));
    }

    // ─── Autosave timing ──────────────────────────────────────────────────────

    [Fact]
    public void AutoSaver_SavesShortlyAfterAChangeAndOnItsTimer()
    {
        var service = NewService();
        string label = "initial";
        Exception? failure = null;

        RunOnDispatcherThread(dispatcher =>
        {
            using var saver = new SessionAutoSaver(
                service, () => SessionWithLabel(_tempRoot, label), _logger, dispatcher,
                debounce: TimeSpan.FromMilliseconds(50), interval: TimeSpan.FromHours(1));

            // Nothing is saved before Start (the tabs are still being restored then).
            saver.RequestSave();
            PumpFor(TimeSpan.FromMilliseconds(300));
            Assert.False(File.Exists(service.SessionFilePath));

            saver.Start(); // saves once right away
            PumpUntil(() => FirstLabelOnDisk(service) == "initial");

            label = "renamed";
            saver.RequestSave();
            PumpUntil(() => FirstLabelOnDisk(service) == "renamed");

            saver.Stop();
            label = "after-stop";
            saver.RequestSave();
            PumpFor(TimeSpan.FromMilliseconds(300));
            Assert.Equal("renamed", FirstLabelOnDisk(service));
        }, ex => failure = ex);

        Assert.Null(failure);

        // The interval timer catches changes that were never reported.
        label = "unreported";
        RunOnDispatcherThread(dispatcher =>
        {
            using var saver = new SessionAutoSaver(
                service, () => SessionWithLabel(_tempRoot, label), _logger, dispatcher,
                debounce: TimeSpan.FromHours(1), interval: TimeSpan.FromMilliseconds(100));
            saver.Start();
            PumpUntil(() => FirstLabelOnDisk(service) == "unreported");
        }, ex => failure = ex);

        Assert.Null(failure);
    }

    private string? FirstLabelOnDisk(SessionService service)
    {
        try
        {
            return FirstLabel(NewService().LoadSession());
        }
        catch
        {
            return null;
        }
    }

    private static void RunOnDispatcherThread(Action<Dispatcher> body, Action<Exception> onFailure)
    {
        var thread = new Thread(() =>
        {
            try
            {
                body(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                onFailure(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Dispatcher thread did not finish.");
    }

    private static void PumpFor(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        PumpWhile(() => watch.Elapsed < duration);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        PumpWhile(() => !condition() && watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(condition(), "Timed out waiting for the autosave.");
    }

    private static void PumpWhile(Func<bool> keepGoing)
    {
        var frame = new DispatcherFrame();
        var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        poll.Tick += (_, _) =>
        {
            if (!keepGoing())
                frame.Continue = false;
        };
        poll.Start();
        Dispatcher.PushFrame(frame);
        poll.Stop();
    }

    // ─── Restoring tabs ───────────────────────────────────────────────────────

    private BrowserPaneViewModel NewPane(out SettingsService settings)
    {
        settings = new SettingsService(_paths, _logger);
        settings.Load();
        var browser = new FileBrowserService(new ShellIntegrationService(), _logger);
        return new BrowserPaneViewModel("Left", browser, settings, _ => { }, (_, _, _, _) => { }, _ => { });
    }

    [Fact]
    public async Task Restore_KeepsTabsWhoseFolderIsGoneAsUnavailable()
    {
        string alpha = Directory.CreateDirectory(Path.Combine(_tempRoot, "Alpha")).FullName;
        string beta = Directory.CreateDirectory(Path.Combine(_tempRoot, "Beta")).FullName;
        string gone = Path.Combine(_tempRoot, "Deleted", "Shots");
        File.WriteAllText(Path.Combine(beta, "keep.txt"), "x");

        var state = new PaneSessionState
        {
            ActiveTabIndex = 1,
            Tabs =
            [
                new TabSessionState { Path = alpha, IsPinned = true },
                new TabSessionState { Path = gone, CustomLabel = "Shots", SelectedPath = Path.Combine(gone, "sh010.exr"), BackHistory = [alpha] },
                new TabSessionState { Path = beta, ViewMode = BrowserViewMode.List, SortColumn = SortColumn.Size, SortDescending = true }
            ]
        };

        using var pane = NewPane(out _);
        await pane.RestoreSessionAsync(state, alpha);

        Assert.Equal(3, pane.Tabs.Count);
        Assert.Same(pane.Tabs[1], pane.ActiveTab);

        BrowserTabViewModel missing = pane.Tabs[1];
        Assert.True(missing.IsUnavailable);
        Assert.Equal(gone, missing.CurrentPath);
        Assert.Equal("Shots", missing.DisplayTitle);
        Assert.NotNull(missing.ErrorMessage);
        Assert.True(missing.CanGoBack);
        Assert.True(missing.CanGoUp);

        Assert.False(pane.Tabs[0].IsUnavailable);
        Assert.True(pane.Tabs[0].IsPinned);
        Assert.Equal(BrowserViewMode.List, pane.Tabs[2].ViewMode);
        Assert.Equal(SortColumn.Size, pane.Tabs[2].SortColumn);
        Assert.Equal(1, pane.Tabs[2].TotalItemCount);

        // Saved again unchanged: the unavailable tab keeps its path, selection and history for the next start.
        PaneSessionState again = pane.ToSessionState();
        Assert.Equal(3, again.Tabs.Count);
        Assert.Equal(1, again.ActiveTabIndex);
        Assert.Equal(gone, again.Tabs[1].Path);
        Assert.Equal(Path.Combine(gone, "sh010.exr"), again.Tabs[1].SelectedPath);
        Assert.Equal(new[] { alpha }, again.Tabs[1].BackHistory);

        // The folder comes back: Retry (F5) opens it.
        Directory.CreateDirectory(gone);
        await missing.RefreshAsync();
        Assert.False(missing.IsUnavailable);
        Assert.Null(missing.ErrorMessage);
    }

    [Fact]
    public async Task Restore_OpenNearestFolder_FindsTheClosestParentThatExists()
    {
        string project = Directory.CreateDirectory(Path.Combine(_tempRoot, "Project")).FullName;
        string gone = Path.Combine(project, "Renamed", "Deeper");

        using var pane = NewPane(out _);
        await pane.RestoreSessionAsync(new PaneSessionState { Tabs = [new TabSessionState { Path = gone }] }, project);

        BrowserTabViewModel tab = pane.Tabs[0];
        Assert.True(tab.IsUnavailable);

        string? nearest = await tab.FindNearestExistingFolderAsync();
        Assert.Equal(project, nearest);

        await pane.NavigateActiveOrSpawnIfLockedAsync(nearest!);
        Assert.Equal(project, tab.CurrentPath);
        Assert.False(tab.IsUnavailable);

        // The unavailable folder stays in Back history: once it is back, Back opens it.
        Assert.True(tab.CanGoBack);
        Directory.CreateDirectory(gone);
        await tab.GoBackAsync();
        Assert.Equal(gone, tab.CurrentPath);
    }

    [Fact]
    public async Task Restore_DoesNotWaitForSlowOrOfflineNetworkFolders()
    {
        // TEST-NET-1 (RFC 5737) is never routed, so this share cannot answer.
        string offline = @"\\192.0.2.1\projects\Job 42";
        string local = Directory.CreateDirectory(Path.Combine(_tempRoot, "Local")).FullName;
        var state = new PaneSessionState
        {
            ActiveTabIndex = 0,
            Tabs = [new TabSessionState { Path = offline, CustomLabel = "NAS" }, new TabSessionState { Path = local }]
        };

        using var pane = NewPane(out _);
        var watch = Stopwatch.StartNew();
        await pane.RestoreSessionAsync(state, local, waitForLoads: false);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Restore waited {watch.Elapsed.TotalSeconds:0.0} s for the offline share.");
        Assert.Equal(2, pane.Tabs.Count);
        Assert.Equal("NAS", pane.Tabs[0].DisplayTitle);

        // Saved while the share is still being tried: nothing is lost.
        PaneSessionState saved = pane.ToSessionState();
        Assert.Equal(new[] { offline, local }, saved.Tabs.Select(t => t.Path));
    }

    [Fact]
    public async Task SaveAndRestore_TabHistoryAndActiveTab()
    {
        string a = Directory.CreateDirectory(Path.Combine(_tempRoot, "A")).FullName;
        string b = Directory.CreateDirectory(Path.Combine(_tempRoot, "B")).FullName;
        string c = Directory.CreateDirectory(Path.Combine(_tempRoot, "C")).FullName;

        PaneSessionState saved;
        using (var pane = NewPane(out _))
        {
            await pane.NewTabAsync(a);
            BrowserTabViewModel tab = pane.ActiveTab!;
            await tab.NavigateAsync(b);
            await tab.NavigateAsync(c);
            await tab.GoBackAsync(); // at B: back = [A], forward = [C]

            // A search tab sits between two folder tabs and is not saved.
            var search = pane.CreateTabInstance();
            search.InitializeAsSearchTab("*.exr", a);
            pane.Tabs.Add(search);
            await pane.NewTabAsync(c);
            pane.ActiveTab = pane.Tabs[^1];

            saved = pane.ToSessionState();
        }

        Assert.Equal(2, saved.Tabs.Count);
        Assert.Equal(1, saved.ActiveTabIndex); // the C tab, counted without the search tab
        Assert.Equal(b, saved.Tabs[0].Path);
        Assert.Equal(new[] { a }, saved.Tabs[0].BackHistory);
        Assert.Equal(new[] { c }, saved.Tabs[0].ForwardHistory);

        using var restored = NewPane(out _);
        await restored.RestoreSessionAsync(saved, a);

        Assert.Same(restored.Tabs[1], restored.ActiveTab);
        BrowserTabViewModel first = restored.Tabs[0];
        Assert.Equal(b, first.CurrentPath);
        Assert.True(first.CanGoBack);
        Assert.True(first.CanGoForward);
        await first.GoForwardAsync();
        Assert.Equal(c, first.CurrentPath);
        await first.GoBackAsync();
        await first.GoBackAsync();
        Assert.Equal(a, first.CurrentPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
        }
    }
}
