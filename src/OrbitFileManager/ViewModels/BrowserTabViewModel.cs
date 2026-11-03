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
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watcherDebounceCts;
    private bool _disposed;

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
            AttachWatcher(result.NormalizedPath);

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
                IsLoading = false;
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

    private void AttachWatcher(string directoryPath)
    {
        DetachWatcher();

        try
        {
            var watcher = new FileSystemWatcher(directoryPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };

            watcher.Created += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Renamed += OnFileSystemRenamed;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
        }
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e) => ScheduleDebouncedRefresh();
    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) => ScheduleDebouncedRefresh();

    private void ScheduleDebouncedRefresh()
    {
        if (_disposed || IsSearchTab)
            return;

        // FileSystemWatcher raises events on thread-pool threads, so several calls can race here.
        // Capture the token synchronously; the debounce task disposes its own source when it ends.
        var cts = CancellationHelper.Replace(ref _watcherDebounceCts);
        CancellationToken token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);
                if (token.IsCancellationRequested || _disposed)
                    return;

                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(async () =>
                    {
                        if (!_disposed && !IsLoading && !IsSearchTab)
                            await RefreshAsync(preserveSelection: true);
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                CancellationHelper.Release(ref _watcherDebounceCts, cts);
            }
        });
    }

    private void DetachWatcher()
    {
        CancellationHelper.CancelAndClear(ref _watcherDebounceCts);

        if (_watcher is not null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileSystemChanged;
                _watcher.Deleted -= OnFileSystemChanged;
                _watcher.Renamed -= OnFileSystemRenamed;
                _watcher.Dispose();
            }
            catch { }
            _watcher = null;
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
