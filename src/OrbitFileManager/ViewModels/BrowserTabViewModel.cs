using CommunityToolkit.Mvvm.ComponentModel;
using OrbitFileManager.Models;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Utilities;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace OrbitFileManager.ViewModels;

public partial class BrowserTabViewModel : ObservableObject, IDisposable
{
    private readonly FileBrowserService _browserService;
    private readonly SettingsService _settingsService;
    private readonly Action<BrowserTabViewModel, string?, string>? _onDirectoryChanged;
    private readonly Action<BrowserTabViewModel>? _onItemsLoaded;
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();

    private List<FileEntryItem> _allItems = [];
    private CancellationTokenSource? _loadCts;
    private string? _loadingPath;
    private bool _disposed;

    // Folder watching. Fields without a note are only touched on the UI thread.
    private static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(350);
    private const long RefreshMaxDelayMs = 1500;
    private static readonly TimeSpan FileUpdateDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NetworkCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WatcherRetryDelay = TimeSpan.FromSeconds(5);

    private FileSystemWatcher? _watcher;
    private string? _watcherPath;
    private CancellationTokenSource? _watchCts;
    private DateTime? _loadedFolderStamp;
    private bool _refreshAfterLoad;
    private readonly object _watchSync = new();
    private CancellationTokenSource? _refreshDebounceCts;                                  // _watchSync
    private long _refreshBurstStartedAt;                                                   // _watchSync
    private readonly HashSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase); // _watchSync
    private bool _fileUpdateScheduled;                                                     // _watchSync

    [ObservableProperty]
    private string _title = "Folder";

    [ObservableProperty]
    private string? _customLabel;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private string? _lockedRootPath;

    [ObservableProperty]
    private bool _isSearchTab;

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private string _addressInputText = string.Empty;

    [ObservableProperty]
    private bool _isAddressEditing;

    [ObservableProperty]
    private string _quickFilterText = string.Empty;

    [ObservableProperty]
    private BrowserViewMode _viewMode;

    [ObservableProperty]
    private SortColumn _sortColumn = SortColumn.Name;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private bool _groupByType;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private FileEntryItem? _selectedItem;

    [ObservableProperty]
    private int _totalItemCount;

    [ObservableProperty]
    private int _displayedItemCount;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private long _selectedBytes;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _canGoUp;

    public ObservableCollection<FileEntryItem> DisplayedItems { get; } = [];
    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

    public IReadOnlyList<FileEntryItem> SelectedItems { get; private set; } = [];

    /// <summary>
    /// Raised whenever this tab navigates to a different folder or initializes search results.
    /// Argument indicates whether a specific child item was explicitly pre-selected.
    /// </summary>
    public event Action<bool>? FolderNavigated;

    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomLabel) ? Title : CustomLabel;
    public bool CanCloseTab => !IsPinned && !IsLocked;

    public string ItemCountSummary => string.IsNullOrWhiteSpace(QuickFilterText)
        ? $"{TotalItemCount:N0} item{(TotalItemCount == 1 ? "" : "s")}"
        : $"{DisplayedItemCount:N0} of {TotalItemCount:N0} items";

    public string SelectionSummary
    {
        get
        {
            if (SelectedCount == 0)
                return "No selection";

            return SelectedBytes > 0
                ? $"{SelectedCount:N0} selected ({FileSizeFormatter.FormatBytes(SelectedBytes)})"
                : $"{SelectedCount:N0} selected";
        }
    }

    public BrowserTabViewModel(
        FileBrowserService browserService,
        SettingsService settingsService,
        BrowserViewMode initialViewMode,
        Action<BrowserTabViewModel, string?, string>? onDirectoryChanged = null,
        Action<BrowserTabViewModel>? onItemsLoaded = null)
    {
        _browserService = browserService;
        _settingsService = settingsService;
        _viewMode = initialViewMode;
        _onDirectoryChanged = onDirectoryChanged;
        _onItemsLoaded = onItemsLoaded;
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnCustomLabelChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnIsPinnedChanged(bool value) => OnPropertyChanged(nameof(CanCloseTab));
    partial void OnIsLockedChanged(bool value) => OnPropertyChanged(nameof(CanCloseTab));

    partial void OnQuickFilterTextChanged(string value)
    {
        ApplyFilterAndSort();
    }

    partial void OnGroupByTypeChanged(bool value)
    {
        ApplyCollectionGrouping();
    }

    partial void OnViewModeChanged(BrowserViewMode value)
    {
        if (!IsSearchTab && !string.IsNullOrEmpty(CurrentPath))
            _ = RefreshAsync(preserveSelection: true);
        else
            _onItemsLoaded?.Invoke(this);
    }

    public void InitializeAsSearchTab(string queryLabel, string scopePath)
    {
        DetachWatcher();
        IsSearchTab = true;
        Title = $"Search: {queryLabel}";
        CurrentPath = scopePath;
        AddressInputText = $"search://{queryLabel}";
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbSegment { Label = $"Search: {queryLabel}", FullPath = scopePath, IsLast = true });

        _allItems.Clear();
        DisplayedItems.Clear();
        TotalItemCount = 0;
        DisplayedItemCount = 0;
        OnPropertyChanged(nameof(ItemCountSummary));
        UpdateNavigationCapabilities();
        FolderNavigated?.Invoke(false);
    }

    public void AppendSearchResultsBatch(IReadOnlyList<FileEntryItem> batch)
    {
        if (batch.Count == 0)
            return;

        _allItems.AddRange(batch);
        TotalItemCount = _allItems.Count;

        string filter = QuickFilterText.Trim();
        foreach (var item in batch)
        {
            if (string.IsNullOrEmpty(filter) ||
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                DisplayedItems.Add(item);
            }
        }

        DisplayedItemCount = DisplayedItems.Count;
        OnPropertyChanged(nameof(ItemCountSummary));
        _onItemsLoaded?.Invoke(this);
    }

    public async Task<bool> NavigateAsync(string targetPath, bool recordHistory = true, string? selectPathAfterLoad = null)
    {
        string normalized = PathValidator.NormalizePath(targetPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        IsSearchTab = false;
        string? previousPath = string.IsNullOrEmpty(CurrentPath) ? null : CurrentPath;
        bool isSamePath = previousPath is not null && PathValidator.PathsEqual(previousPath, normalized);

        var cts = CancellationHelper.Replace(ref _loadCts);

        IsLoading = true;
        ErrorMessage = null;
        _loadingPath = normalized;
        // This load lists the folder after every change seen so far; only changes from here on need another pass.
        _refreshAfterLoad = false;

        try
        {
            bool useLargeIcons = ViewMode is BrowserViewMode.LargeIcons or BrowserViewMode.Thumbnails;
            DirectoryLoadResult result = await _browserService.LoadDirectoryAsync(
                normalized,
                _settingsService.Current.ShowHiddenFiles,
                _settingsService.Current.ShowSystemFiles,
                useLargeIcons,
                cts.Token);

            if (cts.IsCancellationRequested)
                return false;

            if (!result.Success)
            {
                ErrorMessage = result.ErrorMessage;
                if (string.IsNullOrEmpty(CurrentPath))
                {
                    CurrentPath = normalized;
                    AddressInputText = normalized;
                    UpdateTitleAndBreadcrumbs(normalized);
                }
                return false;
            }

            if (recordHistory && !string.IsNullOrEmpty(previousPath) && !isSamePath)
            {
                _backHistory.Push(previousPath);
                _forwardHistory.Clear();
            }

            CurrentPath = result.NormalizedPath;
            AddressInputText = result.NormalizedPath;
            IsAddressEditing = false;

            UpdateNavigationCapabilities();
            UpdateTitleAndBreadcrumbs(result.NormalizedPath);

            _allItems = result.Items.ToList();
            TotalItemCount = _allItems.Count;

            ApplyFilterAndSort(selectPathAfterLoad);
            ApplyCollectionGrouping();
            _loadedFolderStamp = result.FolderStampUtc;
            EnsureWatcher(result.NormalizedPath);

            _settingsService.RecordRecentFolder(result.NormalizedPath);
            _onItemsLoaded?.Invoke(this);

            if (!isSamePath)
            {
                _onDirectoryChanged?.Invoke(this, previousPath, result.NormalizedPath);
                FolderNavigated?.Invoke(!string.IsNullOrEmpty(selectPathAfterLoad));
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (CancellationHelper.Release(ref _loadCts, cts))
            {
                IsLoading = false;
                _loadingPath = null;
                if (_refreshAfterLoad && !_disposed)
                {
                    // Something changed while this load was listing the folder, so the list may already be stale.
                    _refreshAfterLoad = false;
                    ScheduleDebouncedRefresh();
                }
            }
        }
    }

    public async Task RefreshAsync(bool preserveSelection = true, string? explicitSelectPath = null)
    {
        if (IsSearchTab || string.IsNullOrWhiteSpace(CurrentPath))
            return;

        string? pathToReselect = explicitSelectPath ?? (preserveSelection ? SelectedItem?.FullPath : null);
        await NavigateAsync(CurrentPath, recordHistory: false, selectPathAfterLoad: pathToReselect);
    }

    public async Task GoBackAsync()
    {
        if (_backHistory.Count == 0)
            return;

        string target = _backHistory.Pop();
        if (!string.IsNullOrEmpty(CurrentPath))
            _forwardHistory.Push(CurrentPath);

        UpdateNavigationCapabilities();
        await NavigateAsync(target, recordHistory: false);
    }

    public async Task GoForwardAsync()
    {
        if (_forwardHistory.Count == 0)
            return;

        string target = _forwardHistory.Pop();
        if (!string.IsNullOrEmpty(CurrentPath))
            _backHistory.Push(CurrentPath);

        UpdateNavigationCapabilities();
        await NavigateAsync(target, recordHistory: false);
    }

    public async Task GoUpAsync()
    {
        string? parent = PathValidator.GetParentDirectoryPath(CurrentPath);
        if (!string.IsNullOrEmpty(parent))
        {
            await NavigateAsync(parent, recordHistory: true, selectPathAfterLoad: CurrentPath);
        }
    }

    public void ToggleSort(SortColumn column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column is SortColumn.Size or SortColumn.Modified or SortColumn.Created;
        }

        ApplyFilterAndSort(SelectedItem?.FullPath);
    }

    public void UpdateSelection(IReadOnlyList<FileEntryItem> items)
    {
        SelectedItems = items;
        SelectedCount = items.Count;
        SelectedItem = items.Count > 0 ? items[0] : null;

        long bytes = 0;
        foreach (var item in items)
        {
            if (!item.IsDirectory && item.SizeBytes.HasValue)
                bytes += item.SizeBytes.Value;
        }
        SelectedBytes = bytes;

        OnPropertyChanged(nameof(SelectionSummary));
    }

    /// <summary>
    /// Drops entries that were just deleted, without re-reading the folder. The file watcher still reconciles
    /// the folder afterwards; this only makes the rows disappear immediately.
    /// </summary>
    public void RemoveEntries(IReadOnlySet<string> fullPaths)
    {
        if (fullPaths.Count == 0)
            return;

        int removedCount = _allItems.RemoveAll(i => fullPaths.Contains(i.FullPath));
        if (removedCount == 0)
            return;

        TotalItemCount = _allItems.Count;

        // Removing most of a large list row by row is slower than rebuilding it.
        if (removedCount * 2 > DisplayedItems.Count)
        {
            ApplyFilterAndSort();
            return;
        }

        for (int i = DisplayedItems.Count - 1; i >= 0; i--)
        {
            if (fullPaths.Contains(DisplayedItems[i].FullPath))
                DisplayedItems.RemoveAt(i);
        }

        DisplayedItemCount = DisplayedItems.Count;
        OnPropertyChanged(nameof(ItemCountSummary));
        UpdateSelection(SelectedItems.Where(i => !fullPaths.Contains(i.FullPath)).ToList());
        _onItemsLoaded?.Invoke(this);
    }

    public void MarkCutItems(HashSet<string>? cutPaths)
    {
        foreach (var entry in _allItems)
        {
            entry.IsCutPending = cutPaths is not null && cutPaths.Contains(entry.FullPath);
        }
    }

    public void ClearComparisonHighlights()
    {
        foreach (var entry in _allItems)
        {
            entry.IsComparisonDifferent = false;
            entry.ComparisonBadge = null;
        }
    }

    public IReadOnlyList<FileEntryItem> GetAllLoadedItems() => _allItems;

    public TabSessionState ToSessionState()
    {
        return new TabSessionState
        {
            Path = CurrentPath,
            CustomLabel = CustomLabel,
            IsPinned = IsPinned,
            IsLocked = IsLocked,
            LockedRootPath = LockedRootPath,
            ViewMode = ViewMode,
            SortColumn = SortColumn,
            SortDescending = SortDescending,
            GroupByType = GroupByType
        };
    }

    public void ApplySessionMetadata(TabSessionState state)
    {
        CustomLabel = state.CustomLabel;
        IsPinned = state.IsPinned;
        IsLocked = state.IsLocked;
        LockedRootPath = state.LockedRootPath;
        ViewMode = state.ViewMode;
        SortColumn = state.SortColumn;
        SortDescending = state.SortDescending;
        GroupByType = state.GroupByType;
    }

    private void ApplyCollectionGrouping()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
            return;

        void ApplyOnUi()
        {
            var view = CollectionViewSource.GetDefaultView(DisplayedItems);
            if (view is null)
                return;

            view.GroupDescriptions.Clear();
            if (GroupByType)
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileEntryItem.GroupCategory)));
        }

        try
        {
            if (dispatcher.CheckAccess())
                ApplyOnUi();
            else
                dispatcher.Invoke(ApplyOnUi);
        }
        catch { }
    }

    private void ApplyFilterAndSort(string? pathToSelect = null)
    {
        IEnumerable<FileEntryItem> query = _allItems;

        string filter = QuickFilterText.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(item =>
                item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.TypeDescription.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var sorted = SortEntries(query, SortColumn, SortDescending);

        DisplayedItems.Clear();
        FileEntryItem? matchToSelect = null;

        foreach (var item in sorted)
        {
            DisplayedItems.Add(item);
            if (pathToSelect is not null && PathValidator.PathsEqual(item.FullPath, pathToSelect))
                matchToSelect = item;
        }

        DisplayedItemCount = DisplayedItems.Count;
        OnPropertyChanged(nameof(ItemCountSummary));

        if (matchToSelect is not null)
        {
            SelectedItem = matchToSelect;
            UpdateSelection([matchToSelect]);
        }
        else
        {
            UpdateSelection([]);
        }
    }

    public static List<FileEntryItem> SortEntries(
        IEnumerable<FileEntryItem> source,
        SortColumn column,
        bool descending)
    {
        var list = source.ToList();
        list.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;

            int cmp = column switch
            {
                SortColumn.Extension => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
                SortColumn.Size => Nullable.Compare(a.SizeBytes, b.SizeBytes),
                SortColumn.Type => string.Compare(a.TypeDescription, b.TypeDescription, StringComparison.OrdinalIgnoreCase),
                SortColumn.Modified => DateTime.Compare(a.ModifiedTime, b.ModifiedTime),
                SortColumn.Created => DateTime.Compare(a.CreatedTime, b.CreatedTime),
                _ => NaturalFileNameCompare(a.Name, b.Name)
            };

            if (cmp == 0)
                cmp = NaturalFileNameCompare(a.Name, b.Name);

            return descending ? -cmp : cmp;
        });

        return list;
    }

    private static int NaturalFileNameCompare(string a, string b)
    {
        int ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            if (char.IsDigit(a[ia]) && char.IsDigit(b[ib]))
            {
                int startA = ia;
                while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                int startB = ib;
                while (ib < b.Length && char.IsDigit(b[ib])) ib++;

                ReadOnlySpan<char> spanA = a.AsSpan(startA, ia - startA).TrimStart('0');
                ReadOnlySpan<char> spanB = b.AsSpan(startB, ib - startB).TrimStart('0');

                if (spanA.Length != spanB.Length)
                    return spanA.Length.CompareTo(spanB.Length);

                int digitCmp = spanA.CompareTo(spanB, StringComparison.Ordinal);
                if (digitCmp != 0)
                    return digitCmp;
            }
            else
            {
                int charCmp = char.ToUpperInvariant(a[ia]).CompareTo(char.ToUpperInvariant(b[ib]));
                if (charCmp != 0)
                    return charCmp;
                ia++;
                ib++;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private void UpdateNavigationCapabilities()
    {
        CanGoBack = _backHistory.Count > 0;
        CanGoForward = _forwardHistory.Count > 0;
        CanGoUp = !IsSearchTab && PathValidator.GetParentDirectoryPath(CurrentPath) is not null;
    }

    private void UpdateTitleAndBreadcrumbs(string normalizedPath)
    {
        string folderName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar));
        Title = string.IsNullOrWhiteSpace(folderName) ? normalizedPath : folderName;

        Breadcrumbs.Clear();
        foreach (var seg in PathValidator.BuildBreadcrumbs(normalizedPath))
            Breadcrumbs.Add(seg);
    }

    // ── Folder watching ──
    // Created / deleted / renamed entries reload the list; a file that changes size (a copy finishing) is updated in place.
    // A watcher that reports an error is replaced. Network folders are also checked every few seconds, because a share
    // can stop sending change notices without an error, or never send them for changes made on the server itself.

    /// <summary>
    /// Watches <paramref name="directoryPath"/>. A watcher already on that folder is kept, so reloading the same folder
    /// never leaves a moment in which changes go unseen.
    /// </summary>
    private void EnsureWatcher(string directoryPath)
    {
        if (_watcher is not null && _watcherPath is not null && PathValidator.PathsEqual(_watcherPath, directoryPath))
            return;

        DetachWatcher();
        _watcherPath = directoryPath;
        _watchCts = new CancellationTokenSource();
        _watcher = TryCreateWatcher(directoryPath);

        // Watcher-driven reloads run on the UI dispatcher; without one (unit tests) there is nothing to check for.
        if (Application.Current is null)
            return;

        // The first check runs at once and catches changes made between listing the folder and the watcher starting.
        bool keepChecking = _watcher is null || IsNetworkPath(directoryPath);
        _ = CheckFolderStampAsync(directoryPath, keepChecking, _watchCts.Token);
    }

    private FileSystemWatcher? TryCreateWatcher(string directoryPath)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directoryPath)
            {
                IncludeSubdirectories = false,
                // The default 8 KB overflows when many files land at once; 64 KB is the most a network share accepts.
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };

            watcher.Created += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Renamed += OnFileSystemRenamed;
            watcher.Changed += OnFileContentChanged;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            DisposeWatcher(watcher);
            return null;
        }
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e) => ScheduleDebouncedRefresh();
    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) => ScheduleDebouncedRefresh();

    /// <summary>
    /// Reloads the list shortly after a burst of changes. While changes keep coming (a long copy) the reload is not
    /// postponed for more than <see cref="RefreshMaxDelayMs"/>, so files show up as they arrive.
    /// </summary>
    private void ScheduleDebouncedRefresh()
    {
        if (_disposed || IsSearchTab)
            return;

        // FileSystemWatcher raises events on thread-pool threads, so several calls can race here.
        CancellationTokenSource cts;
        lock (_watchSync)
        {
            long now = Environment.TickCount64;
            if (_refreshDebounceCts is null)
                _refreshBurstStartedAt = now;
            else if (now - _refreshBurstStartedAt >= RefreshMaxDelayMs)
                return; // a reload is already due; let it run instead of pushing it back again

            CancellationHelper.SafeCancel(_refreshDebounceCts);
            cts = new CancellationTokenSource();
            _refreshDebounceCts = cts;
        }

        _ = RunDebouncedRefreshAsync(cts);
    }

    private async Task RunDebouncedRefreshAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(RefreshDebounce, cts.Token).ConfigureAwait(false);
            lock (_watchSync)
            {
                if (!ReferenceEquals(_refreshDebounceCts, cts))
                    return;
                _refreshDebounceCts = null; // the next change starts a new burst
            }

            Application.Current?.Dispatcher.InvokeAsync(RequestRefresh);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Superseded sources are only cancelled by the code above, never disposed there.
            cts.Dispose();
        }
    }

    /// <summary>Reloads the list now, or right after the load in progress when one is running. UI thread only.</summary>
    private void RequestRefresh()
    {
        if (_disposed || IsSearchTab || string.IsNullOrEmpty(CurrentPath))
            return;

        if (IsLoading)
        {
            // The running load may have listed the folder before this change; never drop it.
            // Changes in the folder being left behind do not matter for a load of another folder.
            if (_watcherPath is null || _loadingPath is null || PathValidator.PathsEqual(_watcherPath, _loadingPath))
                _refreshAfterLoad = true;
            return;
        }

        _ = RefreshAsync(preserveSelection: true);
    }

    private void OnFileContentChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed || IsSearchTab)
            return;

        lock (_watchSync)
        {
            _changedFiles.Add(e.FullPath);
            if (_fileUpdateScheduled)
                return;
            _fileUpdateScheduled = true;
        }

        _ = UpdateChangedFilesAsync((FileSystemWatcher)sender);
    }

    /// <summary>
    /// Refreshes size and modified time of files that changed, at most once per <see cref="FileUpdateDelay"/>,
    /// without reloading the folder. A file that keeps growing is therefore updated about once a second.
    /// </summary>
    private async Task UpdateChangedFilesAsync(FileSystemWatcher source)
    {
        await Task.Delay(FileUpdateDelay).ConfigureAwait(false);

        string[] paths;
        lock (_watchSync)
        {
            paths = [.. _changedFiles];
            _changedFiles.Clear();
            _fileUpdateScheduled = false;
        }

        if (paths.Length == 0 || _disposed)
            return;

        var updates = new List<(string Path, long Size, DateTime Modified)>(paths.Length);
        foreach (string path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) // folders and files deleted since are skipped
                    updates.Add((path, info.Length, info.LastWriteTime));
            }
            catch
            {
            }
        }

        if (updates.Count > 0)
            Application.Current?.Dispatcher.InvokeAsync(() => ApplyFileUpdates(updates, source));
    }

    private void ApplyFileUpdates(List<(string Path, long Size, DateTime Modified)> updates, FileSystemWatcher source)
    {
        if (_disposed || !ReferenceEquals(source, _watcher))
            return; // the tab moved to another folder (or the watcher was replaced and the list reloaded)

        var byPath = new Dictionary<string, FileEntryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allItems)
        {
            if (!item.IsDirectory)
                byPath.TryAdd(item.FullPath, item);
        }

        bool selectionChanged = false;
        foreach (var (path, size, modified) in updates)
        {
            if (!byPath.TryGetValue(path, out var item))
                continue; // not listed yet; the reload for its creation brings it in

            item.SizeBytes = size;
            item.FormattedSize = FileSizeFormatter.FormatBytes(size);
            item.ModifiedTime = modified;
            selectionChanged |= SelectedItems.Contains(item);
        }

        if (selectionChanged)
            UpdateSelection(SelectedItems);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_disposed)
            return;

        // Buffer overflow or a dropped network connection: changes were lost and the watcher may report nothing more.
        var broken = (FileSystemWatcher)sender;
        Application.Current?.Dispatcher.InvokeAsync(() => RecoverWatcher(broken));
    }

    private void RecoverWatcher(FileSystemWatcher broken)
    {
        if (_disposed || !ReferenceEquals(_watcher, broken) || _watcherPath is null || _watchCts is null)
            return;

        DisposeWatcher(broken);
        _watcher = null;
        _ = RestartWatcherAsync(_watcherPath, _watchCts.Token);
    }

    private async Task RestartWatcherAsync(string directoryPath, CancellationToken token)
    {
        TimeSpan delay = TimeSpan.FromMilliseconds(500);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Creating a watcher touches the share, which can hang while the network is down; keep it off the UI thread.
            FileSystemWatcher? watcher = await Task.Run(() => TryCreateWatcher(directoryPath));
            if (token.IsCancellationRequested || _disposed)
            {
                DisposeWatcher(watcher);
                return;
            }

            if (watcher is not null)
            {
                _watcher = watcher;
                RequestRefresh(); // pick up whatever changed while no watcher was running
                return;
            }

            delay = WatcherRetryDelay;
        }
    }

    /// <summary>
    /// Compares the folder's last-write time with the one read when the list was loaded and reloads when it moved.
    /// Runs once, or every <see cref="NetworkCheckInterval"/> when <paramref name="keepChecking"/> is set.
    /// </summary>
    private async Task CheckFolderStampAsync(string directoryPath, bool keepChecking, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            DateTime? stamp = await Task.Run(() => TryGetFolderStamp(directoryPath));
            if (token.IsCancellationRequested || _disposed)
                return;

            if (stamp is not null && !IsLoading)
            {
                if (_loadedFolderStamp is null)
                    _loadedFolderStamp = stamp;
                else if (stamp != _loadedFolderStamp)
                    RequestRefresh();
            }

            if (!keepChecking)
                return;

            try
            {
                await Task.Delay(NetworkCheckInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static DateTime? TryGetFolderStamp(string directoryPath)
    {
        try
        {
            var info = new DirectoryInfo(directoryPath);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private void DetachWatcher()
    {
        CancellationHelper.CancelAndClear(ref _watchCts);
        lock (_watchSync)
        {
            CancellationHelper.SafeCancel(_refreshDebounceCts);
            _refreshDebounceCts = null;
            _changedFiles.Clear();
        }

        DisposeWatcher(_watcher);
        _watcher = null;
        _watcherPath = null;
    }

    private void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
            return;

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFileSystemChanged;
            watcher.Deleted -= OnFileSystemChanged;
            watcher.Renamed -= OnFileSystemRenamed;
            watcher.Changed -= OnFileContentChanged;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DetachWatcher();
        CancellationHelper.CancelAndClear(ref _loadCts);
    }
}
