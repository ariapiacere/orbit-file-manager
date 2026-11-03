using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrbitFileManager.Models;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.Collections.ObjectModel;

namespace OrbitFileManager.ViewModels;

public partial class BrowserPaneViewModel : ObservableObject, IDisposable
{
    private readonly FileBrowserService _browserService;
    private readonly SettingsService _settingsService;
    private readonly Action<BrowserPaneViewModel> _onActivated;
    private readonly Action<BrowserPaneViewModel, BrowserTabViewModel, string?, string> _onPaneDirectoryChanged;
    private readonly Action<BrowserTabViewModel> _onTabItemsLoaded;
    private readonly Stack<TabSessionState> _closedTabs = new();
    private bool _disposed;

    public string PaneId { get; }

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private BrowserTabViewModel? _activeTab;

    [ObservableProperty]
    private bool _isActivePane;

    public bool CanRestoreClosedTab => _closedTabs.Count > 0;

    public BrowserPaneViewModel(
        string paneId,
        FileBrowserService browserService,
        SettingsService settingsService,
        Action<BrowserPaneViewModel> onActivated,
        Action<BrowserPaneViewModel, BrowserTabViewModel, string?, string> onPaneDirectoryChanged,
        Action<BrowserTabViewModel> onTabItemsLoaded)
    {
        PaneId = paneId;
        _browserService = browserService;
        _settingsService = settingsService;
        _onActivated = onActivated;
        _onPaneDirectoryChanged = onPaneDirectoryChanged;
        _onTabItemsLoaded = onTabItemsLoaded;
    }

    [RelayCommand]
    public void Activate()
    {
        _onActivated(this);
    }

    public BrowserTabViewModel CreateTabInstance()
    {
        return new BrowserTabViewModel(
            _browserService,
            _settingsService,
            _settingsService.Current.DefaultViewMode,
            onDirectoryChanged: (tab, prevPath, newPath) => _onPaneDirectoryChanged(this, tab, prevPath, newPath),
            onItemsLoaded: _onTabItemsLoaded);
    }

    [RelayCommand]
    public async Task NewTabAsync(string? path = null)
    {
        Activate();
        string targetPath = !string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path)
            ? path
            : (!string.IsNullOrWhiteSpace(ActiveTab?.CurrentPath) && System.IO.Directory.Exists(ActiveTab.CurrentPath)
                ? ActiveTab.CurrentPath
                : _settingsService.Current.DefaultStartPath);

        var tab = CreateTabInstance();
        Tabs.Add(tab);
        ActiveTab = tab;
        await tab.NavigateAsync(targetPath, recordHistory: false);
    }

    [RelayCommand]
    public async Task DuplicateTabAsync(BrowserTabViewModel? sourceTab = null)
    {
        Activate();
        var src = sourceTab ?? ActiveTab;
        if (src is null)
            return;

        var copy = CreateTabInstance();
        copy.CustomLabel = src.CustomLabel;
        copy.ViewMode = src.ViewMode;
        copy.SortColumn = src.SortColumn;
        copy.SortDescending = src.SortDescending;
        copy.GroupByType = src.GroupByType;

        int insertIdx = Math.Clamp(Tabs.IndexOf(src) + 1, 0, Tabs.Count);
        Tabs.Insert(insertIdx, copy);
        ActiveTab = copy;
        await copy.NavigateAsync(src.CurrentPath, recordHistory: false);
    }

    [RelayCommand]
    public void CloseTab(BrowserTabViewModel? tab = null)
    {
        var target = tab ?? ActiveTab;
        if (target is null)
            return;

        // Pinned or Locked tabs are protected from accidental closing
        if (target.IsPinned || target.IsLocked)
            return;

        if (!target.IsSearchTab && !string.IsNullOrWhiteSpace(target.CurrentPath))
        {
            _closedTabs.Push(target.ToSessionState());
            OnPropertyChanged(nameof(CanRestoreClosedTab));
        }

        if (Tabs.Count <= 1)
        {
            _ = target.NavigateAsync(_settingsService.Current.DefaultStartPath);
            return;
        }

        int idx = Tabs.IndexOf(target);
        bool wasActive = ReferenceEquals(target, ActiveTab);

        Tabs.Remove(target);
        target.Dispose();

        if (wasActive && Tabs.Count > 0)
        {
            int nextIdx = Math.Clamp(idx, 0, Tabs.Count - 1);
            ActiveTab = Tabs[nextIdx];
        }
    }

    [RelayCommand]
    public void CloseOtherTabs(BrowserTabViewModel? keepTab = null)
    {
        var keep = keepTab ?? ActiveTab;
        if (keep is null)
            return;

        var removable = Tabs.Where(t => !ReferenceEquals(t, keep) && !t.IsPinned && !t.IsLocked).ToList();
        foreach (var r in removable)
        {
            if (!r.IsSearchTab && !string.IsNullOrWhiteSpace(r.CurrentPath))
                _closedTabs.Push(r.ToSessionState());
            Tabs.Remove(r);
            r.Dispose();
        }

        ActiveTab = keep;
        OnPropertyChanged(nameof(CanRestoreClosedTab));
    }

    [RelayCommand]
    public async Task RestoreClosedTabAsync()
    {
        if (_closedTabs.Count == 0)
            return;

        var state = _closedTabs.Pop();
        OnPropertyChanged(nameof(CanRestoreClosedTab));

        var tab = CreateTabInstance();
        tab.ApplySessionMetadata(state);
        Tabs.Add(tab);
        ActiveTab = tab;
        await tab.NavigateAsync(state.Path, recordHistory: false);
    }

    [RelayCommand]
    public void TogglePinTab(BrowserTabViewModel? tab = null)
    {
        var target = tab ?? ActiveTab;
        if (target is null)
            return;

        target.IsPinned = !target.IsPinned;
        if (target.IsPinned)
        {
            // Move pinned tabs toward the left alongside existing pinned tabs
            int currentIdx = Tabs.IndexOf(target);
            int targetIdx = 0;
            while (targetIdx < Tabs.Count && Tabs[targetIdx].IsPinned && !ReferenceEquals(Tabs[targetIdx], target))
                targetIdx++;

            if (currentIdx >= 0 && targetIdx < currentIdx)
                Tabs.Move(currentIdx, targetIdx);
        }
    }

    [RelayCommand]
    public void ToggleLockTab(BrowserTabViewModel? tab = null)
    {
        var target = tab ?? ActiveTab;
        if (target is null)
            return;

        target.IsLocked = !target.IsLocked;
        target.LockedRootPath = target.IsLocked ? target.CurrentPath : null;
    }

    public void MoveTab(BrowserTabViewModel draggedTab, BrowserTabViewModel targetTab)
    {
        if (ReferenceEquals(draggedTab, targetTab))
            return;

        int oldIndex = Tabs.IndexOf(draggedTab);
        int newIndex = Tabs.IndexOf(targetTab);
        if (oldIndex < 0 || newIndex < 0)
            return;

        // Keep pinned tabs grouped with pinned tabs
        if (draggedTab.IsPinned != targetTab.IsPinned)
            return;

        Tabs.Move(oldIndex, newIndex);
    }

    public async Task<bool> NavigateActiveOrSpawnIfLockedAsync(string targetPath)
    {
        if (ActiveTab is null)
        {
            await NewTabAsync(targetPath);
            return true;
        }

        // If tab is locked to a root path and user navigates outside that subtree, open in a new tab
        if (ActiveTab.IsLocked &&
            !string.IsNullOrWhiteSpace(ActiveTab.LockedRootPath) &&
            !PathValidator.IsSameOrSubPath(ActiveTab.LockedRootPath, targetPath))
        {
            await NewTabAsync(targetPath);
            return true;
        }

        return await ActiveTab.NavigateAsync(targetPath);
    }

    public void NextTab()
    {
        if (Tabs.Count <= 1 || ActiveTab is null)
            return;

        int next = (Tabs.IndexOf(ActiveTab) + 1) % Tabs.Count;
        ActiveTab = Tabs[next];
    }

    public void PreviousTab()
    {
        if (Tabs.Count <= 1 || ActiveTab is null)
            return;

        int current = Tabs.IndexOf(ActiveTab);
        int prev = (current - 1 + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[prev];
    }

    public PaneSessionState ToSessionState()
    {
        var tabs = Tabs
            .Where(t => !t.IsSearchTab && !string.IsNullOrWhiteSpace(t.CurrentPath))
            .Select(t => t.ToSessionState())
            .ToList();

        int activeIdx = ActiveTab is not null ? Math.Max(0, Tabs.IndexOf(ActiveTab)) : 0;
        return new PaneSessionState
        {
            Tabs = tabs,
            ActiveTabIndex = Math.Clamp(activeIdx, 0, Math.Max(0, tabs.Count - 1))
        };
    }

    public async Task RestoreSessionAsync(PaneSessionState? state, string fallbackPath)
    {
        foreach (var existing in Tabs.ToList())
            existing.Dispose();
        Tabs.Clear();

        if (state is not null && state.Tabs.Count > 0)
        {
            foreach (var tabState in state.Tabs)
            {
                if (string.IsNullOrWhiteSpace(tabState.Path) || !System.IO.Directory.Exists(tabState.Path))
                    continue;

                var tab = CreateTabInstance();
                tab.ApplySessionMetadata(tabState);
                Tabs.Add(tab);
                await tab.NavigateAsync(tabState.Path, recordHistory: false);
            }
        }

        if (Tabs.Count == 0)
        {
            var defaultTab = CreateTabInstance();
            Tabs.Add(defaultTab);
            await defaultTab.NavigateAsync(fallbackPath, recordHistory: false);
        }

        int idx = state is not null ? Math.Clamp(state.ActiveTabIndex, 0, Tabs.Count - 1) : 0;
        ActiveTab = Tabs[idx];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var t in Tabs)
            t.Dispose();
    }
}
