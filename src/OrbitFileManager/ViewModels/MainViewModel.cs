using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrbitFileManager.Models;
using OrbitFileManager.Services;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Preview;
using OrbitFileManager.Services.Productivity;
using OrbitFileManager.Services.Search;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Services.Thumbnails;
using OrbitFileManager.Services.Updates;
using OrbitFileManager.Utilities;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace OrbitFileManager.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppPathsService _pathsService;
    private readonly SettingsService _settingsService;
    private readonly ShortcutService _shortcutService;
    private readonly FavoritesService _favoritesService;
    private readonly SessionService _sessionService;
    private readonly MetadataStoreService _metadataService;
    private readonly FileBrowserService _browserService;
    private readonly ShellIntegrationService _shellService;
    private readonly FileOperationService _fileOperationService;
    private readonly FileOperationQueue _operationQueue;
    private readonly BatchRenameService _batchRenameService;
    private readonly FileSearchService _searchService;
    private readonly ThumbnailService _thumbnailService;
    private readonly PreviewService _previewService;
    private readonly ChecksumService _checksumService;
    private readonly DuplicateFinderService _duplicateFinderService;
    private readonly FolderSizeAnalyzerService _folderSizeAnalyzerService;
    private readonly UpdateService _updateService;
    private readonly IUserDialogService _dialogService;
    private readonly IThemeService _themeService;
    private readonly StructuredLogger _logger;

    private HashSet<string>? _activeCutPaths;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _thumbnailsCts;
    private BrowserTabViewModel? _subscribedActiveTab;
    private bool _isSyncingNavigation;
    private bool _disposed;

    public BrowserPaneViewModel LeftPane { get; }
    public BrowserPaneViewModel RightPane { get; }

    public ObservableCollection<FavoriteItem> QuickAccessItems { get; } = [];
    public ObservableCollection<FavoriteItem> FavoriteItems { get; } = [];
    public ObservableCollection<DriveEntryItem> Drives { get; } = [];
    public ObservableCollection<FolderTreeNodeViewModel> FolderTreeRoots { get; } = [];
    public ObservableCollection<string> RecentFolders { get; } = [];
    public ObservableCollection<WorkspacePreset> SavedWorkspaces { get; } = [];

    [ObservableProperty]
    private BrowserPaneViewModel _activePane;

    [ObservableProperty]
    private bool _isDualPane;

    [ObservableProperty]
    private SplitOrientationMode _splitOrientation = SplitOrientationMode.Vertical;

    /// <summary>Share of the dual-pane area given to the left pane in a side-by-side split.</summary>
    [ObservableProperty]
    private double _verticalSplitRatio = 0.5;

    /// <summary>Share of the dual-pane area given to the top pane in a top-and-bottom split.</summary>
    [ObservableProperty]
    private double _horizontalSplitRatio = 0.5;

    [ObservableProperty]
    private bool _isSyncNavigationEnabled;

    /// <summary>Compare mode keeps differences highlighted, re-checking whenever either pane loads a folder.</summary>
    [ObservableProperty]
    private bool _isComparingPanes;

    [ObservableProperty]
    private FilePreviewData _currentPreview = new() { Kind = PreviewKind.None };

    [ObservableProperty]
    private string _selectedItemNote = string.Empty;

    [ObservableProperty]
    private double _imageZoom = 1.0;

    [ObservableProperty]
    private bool _isFitToPanel = true;

    [ObservableProperty]
    private bool _showSidebar = true;

    [ObservableProperty]
    private bool _miniTreeMode;

    [ObservableProperty]
    private bool _showPreviewPanel = true;

    [ObservableProperty]
    private bool _showOperationQueuePanel;

    [ObservableProperty]
    private bool _canUndoBatchRename;

    public FileOperationQueue OperationQueue => _operationQueue;

    [ObservableProperty]
    private bool _showHiddenFiles;

    [ObservableProperty]
    private bool _showExtensionColumn = true;

    [ObservableProperty]
    private bool _showSizeColumn = true;

    [ObservableProperty]
    private bool _showTypeColumn = true;

    [ObservableProperty]
    private bool _showModifiedColumn = true;

    [ObservableProperty]
    private bool _showCreatedColumn = true;

    [ObservableProperty]
    private bool _showAttributesColumn = true;

    [ObservableProperty]
    private bool _showToolbarTextLabels = true;

    [ObservableProperty]
    private bool _showToolbarBatchRename = true;

    [ObservableProperty]
    private bool _showToolbarSearch = true;

    [ObservableProperty]
    private bool _showToolbarDualPane = true;

    [ObservableProperty]
    private bool _showToolbarToolsMenu = true;

    [ObservableProperty]
    private AppThemeMode _currentTheme = AppThemeMode.Dark;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isOperationBusy;

    [ObservableProperty]
    private string _driveSpaceSummary = string.Empty;

    public BrowserTabViewModel? ActiveTab => ActivePane.ActiveTab;
    public BrowserPaneViewModel InactivePane => ReferenceEquals(ActivePane, LeftPane) ? RightPane : LeftPane;

    /// <summary>Newer GitHub release found by the last update check, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable), nameof(UpdateBadgeText))]
    private UpdateInfo? _availableUpdate;

    public bool IsUpdateAvailable => AvailableUpdate is not null;
    public string UpdateBadgeText => AvailableUpdate is null ? string.Empty : $"Update to {UpdateService.Format(AvailableUpdate.Version)}";
    public string AppVersionText => $"Orbit {UpdateService.Format(UpdateService.CurrentVersion)}";

    public string StorageModeBadge => _pathsService.IsPortableWritable ? "Portable" : "AppData";
    public string StorageRootPath => _pathsService.StorageRoot;

    // Virtual hook so Phase 3 FileOperationQueue can intercept transfers cleanly
    public Func<IReadOnlyList<string>, string, bool, Task>? QueuedTransferHandler { get; set; }
    // Virtual hook so Phase 4 ThumbnailService can load async thumbnails on tab load
    public Action<BrowserTabViewModel>? TabThumbnailsLoader { get; set; }

    public event Action? RequestSelectAllInActiveList;
    public event Action? RequestFocusAddressBar;
    public event Action? RequestFocusQuickFilter;
    /// <summary>Raised after an update installer was started; the window closes so the installer can replace the files.</summary>
    public event Action? RequestCloseForUpdate;

    public MainViewModel(
        AppPathsService pathsService,
        SettingsService settingsService,
        ShortcutService shortcutService,
        FavoritesService favoritesService,
        SessionService sessionService,
        MetadataStoreService metadataService,
        FileBrowserService browserService,
        ShellIntegrationService shellService,
        FileOperationService fileOperationService,
        FileOperationQueue operationQueue,
        BatchRenameService batchRenameService,
        FileSearchService searchService,
        ThumbnailService thumbnailService,
        PreviewService previewService,
        ChecksumService checksumService,
        DuplicateFinderService duplicateFinderService,
        FolderSizeAnalyzerService folderSizeAnalyzerService,
        UpdateService updateService,
        IUserDialogService dialogService,
        IThemeService themeService,
        StructuredLogger logger)
    {
        _pathsService = pathsService;
        _settingsService = settingsService;
        _shortcutService = shortcutService;
        _favoritesService = favoritesService;
        _sessionService = sessionService;
        _metadataService = metadataService;
        _browserService = browserService;
        _shellService = shellService;
        _fileOperationService = fileOperationService;
        _operationQueue = operationQueue;
        _batchRenameService = batchRenameService;
        _searchService = searchService;
        _thumbnailService = thumbnailService;
        _previewService = previewService;
        _checksumService = checksumService;
        _duplicateFinderService = duplicateFinderService;
        _folderSizeAnalyzerService = folderSizeAnalyzerService;
        _updateService = updateService;
        _dialogService = dialogService;
        _themeService = themeService;
        _logger = logger;

        TabThumbnailsLoader = TriggerThumbnailLoadForTab;

        _operationQueue.ConflictResolver = _dialogService.ResolveConflictAsync;
        _operationQueue.OnJobFinishedRefresh = async () =>
        {
            if (Application.Current?.Dispatcher is { } disp)
                await disp.InvokeAsync(async () => await RefreshAffectedTabsAsync());
        };
        QueuedTransferHandler = (sources, dest, move) =>
        {
            var job = _operationQueue.EnqueueTransfer(sources, dest, move);
            ShowOperationQueuePanel = true;
            StatusMessage = $"Queued: {job.Title}";
            return Task.CompletedTask;
        };

        LeftPane = new BrowserPaneViewModel(
            "Left",
            _browserService,
            _settingsService,
            ActivatePane,
            OnPaneDirectoryNavigated,
            OnTabItemsLoaded);

        RightPane = new BrowserPaneViewModel(
            "Right",
            _browserService,
            _settingsService,
            ActivatePane,
            OnPaneDirectoryNavigated,
            OnTabItemsLoaded);

        LeftPane.PropertyChanged += OnPanePropertyChanged;
        RightPane.PropertyChanged += OnPanePropertyChanged;

        _activePane = LeftPane;
        LeftPane.IsActivePane = true;
    }

    public async Task InitializeAsync()
    {
        AppSettings settings = _settingsService.Current;
        ShowSidebar = settings.ShowSidebar;
        MiniTreeMode = settings.MiniTreeMode;
        ShowPreviewPanel = settings.ShowPreviewPanel;
        ShowHiddenFiles = settings.ShowHiddenFiles;
        ShowExtensionColumn = settings.ShowExtensionColumn;
        ShowSizeColumn = settings.ShowSizeColumn;
        ShowTypeColumn = settings.ShowTypeColumn;
        ShowModifiedColumn = settings.ShowModifiedColumn;
        ShowCreatedColumn = settings.ShowCreatedColumn;
        ShowAttributesColumn = settings.ShowAttributesColumn;
        ShowToolbarTextLabels = settings.ShowToolbarTextLabels;
        ShowToolbarBatchRename = settings.ShowToolbarBatchRename;
        ShowToolbarSearch = settings.ShowToolbarSearch;
        ShowToolbarDualPane = settings.ShowToolbarDualPane;
        ShowToolbarToolsMenu = settings.ShowToolbarToolsMenu;
        CurrentTheme = settings.Theme;
        _themeService.ApplyTheme(settings.Theme);

        LoadSidebarData();
        ReloadSavedWorkspaces();

        AppSessionState? session = settings.RestoreTabsOnStartup ? _sessionService.LoadSession() : null;
        if (session is not null)
        {
            await ApplySessionStateAsync(session);
        }
        else
        {
            await LeftPane.RestoreSessionAsync(null, settings.DefaultStartPath);
            await RightPane.RestoreSessionAsync(null, settings.DefaultStartPath);
            ActivatePane(LeftPane);
        }

        RebindSubscribedActiveTab();
        StatusMessage = "Ready";
        _logger.Info("App", "Orbit File Manager initialized.", new { dualPane = IsDualPane, portable = _pathsService.IsPortableWritable, version = UpdateService.Format(UpdateService.CurrentVersion) });

        ReportLastUpdateResult();
        _ = CheckForUpdatesOnStartupAsync();
    }

    partial void OnShowExtensionColumnChanged(bool value) { _settingsService.Current.ShowExtensionColumn = value; _settingsService.Save(); }
    partial void OnShowSizeColumnChanged(bool value) { _settingsService.Current.ShowSizeColumn = value; _settingsService.Save(); }
    partial void OnShowTypeColumnChanged(bool value) { _settingsService.Current.ShowTypeColumn = value; _settingsService.Save(); }
    partial void OnShowModifiedColumnChanged(bool value) { _settingsService.Current.ShowModifiedColumn = value; _settingsService.Save(); }
    partial void OnShowCreatedColumnChanged(bool value) { _settingsService.Current.ShowCreatedColumn = value; _settingsService.Save(); }
    partial void OnShowAttributesColumnChanged(bool value) { _settingsService.Current.ShowAttributesColumn = value; _settingsService.Save(); }

    public void ActivatePane(BrowserPaneViewModel pane)
    {
        if (!IsDualPane && ReferenceEquals(pane, RightPane))
            return;

        LeftPane.IsActivePane = ReferenceEquals(pane, LeftPane);
        RightPane.IsActivePane = ReferenceEquals(pane, RightPane);

        if (!ReferenceEquals(ActivePane, pane))
            ActivePane = pane;

        RebindSubscribedActiveTab();
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BrowserPaneViewModel pane || e.PropertyName != nameof(BrowserPaneViewModel.ActiveTab))
            return;

        if (ReferenceEquals(pane, ActivePane))
            RebindSubscribedActiveTab();

        if (IsComparingPanes)
            HighlightPaneDifferences();
    }

    private void RebindSubscribedActiveTab()
    {
        if (_subscribedActiveTab is not null)
            _subscribedActiveTab.PropertyChanged -= OnActiveTabPropertyChanged;

        _subscribedActiveTab = ActivePane.ActiveTab;
        if (_subscribedActiveTab is not null)
        {
            _subscribedActiveTab.PropertyChanged += OnActiveTabPropertyChanged;
            _subscribedActiveTab.MarkCutItems(_activeCutPaths);
            UpdateDriveSpaceSummary(_subscribedActiveTab.CurrentPath);
            _ = RefreshPreviewAsync(_subscribedActiveTab.SelectedItem);
        }
        else
        {
            CurrentPreview = new FilePreviewData { Kind = PreviewKind.None };
            SelectedItemNote = string.Empty;
            DriveSpaceSummary = string.Empty;
        }

        OnPropertyChanged(nameof(ActiveTab));
        NotifyNavigationCommandsChanged();
    }

    public void NotifyActiveSelectionChanged()
    {
        if (ActiveTab is not null)
            _ = RefreshPreviewAsync(ActiveTab.SelectedItem);
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BrowserTabViewModel tab)
            return;

        switch (e.PropertyName)
        {
            case nameof(BrowserTabViewModel.CanGoBack):
            case nameof(BrowserTabViewModel.CanGoForward):
            case nameof(BrowserTabViewModel.CanGoUp):
                NotifyNavigationCommandsChanged();
                break;

            case nameof(BrowserTabViewModel.SelectedItem):
                _ = RefreshPreviewAsync(tab.SelectedItem);
                break;

            case nameof(BrowserTabViewModel.CurrentPath):
                UpdateDriveSpaceSummary(tab.CurrentPath);
                SyncRecentFoldersFromSettings();
                break;
        }
    }

    private void OnTabItemsLoaded(BrowserTabViewModel tab)
    {
        _metadataService.DecorateItems(tab.GetAllLoadedItems());
        tab.MarkCutItems(_activeCutPaths);
        TabThumbnailsLoader?.Invoke(tab);

        if (IsComparingPanes && (ReferenceEquals(tab, LeftPane.ActiveTab) || ReferenceEquals(tab, RightPane.ActiveTab)))
            HighlightPaneDifferences();
    }

    private void OnPaneDirectoryNavigated(
        BrowserPaneViewModel sourcePane,
        BrowserTabViewModel sourceTab,
        string? previousPath,
        string newPath)
    {
        if (ReferenceEquals(sourcePane, ActivePane))
        {
            UpdateDriveSpaceSummary(newPath);
            SyncRecentFoldersFromSettings();
        }

        // Synchronized Dual-Pane Navigation
        if (!IsDualPane || !IsSyncNavigationEnabled || _isSyncingNavigation || string.IsNullOrEmpty(previousPath))
            return;

        var otherPane = ReferenceEquals(sourcePane, LeftPane) ? RightPane : LeftPane;
        if (otherPane.ActiveTab is not { IsSearchTab: false } otherTab || string.IsNullOrEmpty(otherTab.CurrentPath))
            return;

        string? candidateMirrorPath = null;

        // Case 1: Navigated into direct child subfolder -> open same-named child in other pane
        string? newParent = PathValidator.GetParentDirectoryPath(newPath);
        if (newParent is not null && PathValidator.PathsEqual(newParent, previousPath))
        {
            string childName = Path.GetFileName(newPath);
            string possibleChild = Path.Combine(otherTab.CurrentPath, childName);
            if (Directory.Exists(possibleChild))
                candidateMirrorPath = possibleChild;
        }
        // Case 2: Navigated Up one level -> navigate other pane Up one level
        else
        {
            string? prevParent = PathValidator.GetParentDirectoryPath(previousPath);
            if (prevParent is not null && PathValidator.PathsEqual(prevParent, newPath))
            {
                candidateMirrorPath = PathValidator.GetParentDirectoryPath(otherTab.CurrentPath);
            }
        }

        if (!string.IsNullOrEmpty(candidateMirrorPath) && Directory.Exists(candidateMirrorPath))
        {
            _isSyncingNavigation = true;
            _ = MirrorNavigateAsync(otherTab, candidateMirrorPath);
        }
    }

    private async Task MirrorNavigateAsync(BrowserTabViewModel otherTab, string mirrorPath)
    {
        try
        {
            await otherTab.NavigateAsync(mirrorPath);
        }
        finally
        {
            _isSyncingNavigation = false;
        }
    }

    private void NotifyNavigationCommandsChanged()
    {
        NavigateBackCommand.NotifyCanExecuteChanged();
        NavigateForwardCommand.NotifyCanExecuteChanged();
        NavigateUpCommand.NotifyCanExecuteChanged();
    }

    public void LoadSidebarData()
    {
        QuickAccessItems.Clear();
        foreach (var qa in _favoritesService.GetQuickAccessItems())
            QuickAccessItems.Add(qa);

        FavoriteItems.Clear();
        foreach (var fav in _favoritesService.LoadFavorites())
            FavoriteItems.Add(fav);

        RefreshDrivesAndTree();
        SyncRecentFoldersFromSettings();
    }

    [RelayCommand]
    public void RefreshDrivesAndTree()
    {
        Drives.Clear();
        FolderTreeRoots.Clear();

        bool includeHidden = _settingsService.Current.ShowHiddenFiles;
        bool includeSystem = _settingsService.Current.ShowSystemFiles;

        foreach (DriveEntryItem drive in _browserService.GetConnectedDrives())
        {
            Drives.Add(drive);
            if (drive.IsReady)
            {
                bool hasChildren = _browserService.HasAccessibleSubdirectories(drive.RootPath, includeHidden, includeSystem);
                FolderTreeRoots.Add(new FolderTreeNodeViewModel(
                    drive.DisplayLabel,
                    drive.RootPath,
                    drive.Icon,
                    isDriveRoot: true,
                    mayHaveChildren: hasChildren,
                    _browserService,
                    _shellService,
                    _settingsService));
            }
        }

        if (ActiveTab is not null)
            UpdateDriveSpaceSummary(ActiveTab.CurrentPath);
    }

    // ─── Dual-Pane Commands ────────────────────────────────────────────────────

    [RelayCommand]
    public async Task ToggleDualPaneAsync()
    {
        IsDualPane = !IsDualPane;
        if (IsDualPane && RightPane.Tabs.Count == 0)
        {
            string seedPath = ActiveTab?.CurrentPath ?? _settingsService.Current.DefaultStartPath;
            await RightPane.RestoreSessionAsync(null, seedPath);
        }
        else if (!IsDualPane && ReferenceEquals(ActivePane, RightPane))
        {
            ActivatePane(LeftPane);
        }

        StatusMessage = IsDualPane ? "Dual-Pane mode enabled (F6 to switch panes)." : "Single-Pane mode.";
    }

    partial void OnIsDualPaneChanged(bool value)
    {
        if (!value && IsComparingPanes)
            StopComparingPanes();
    }

    [RelayCommand]
    public void ToggleSplitOrientation()
    {
        if (!IsDualPane)
            IsDualPane = true;

        SplitOrientation = SplitOrientation == SplitOrientationMode.Vertical
            ? SplitOrientationMode.Horizontal
            : SplitOrientationMode.Vertical;

        StatusMessage = SplitOrientation == SplitOrientationMode.Vertical
            ? "Split layout: Side-by-Side (Vertical)."
            : "Split layout: Top-and-Bottom (Horizontal).";
    }

    [RelayCommand]
    public void SwitchActivePane()
    {
        if (!IsDualPane)
            return;

        ActivatePane(ReferenceEquals(ActivePane, LeftPane) ? RightPane : LeftPane);
        StatusMessage = $"Active pane: {ActivePane.PaneId}";
    }

    [RelayCommand]
    public async Task SwapPanesAsync()
    {
        if (!IsDualPane || LeftPane.ActiveTab is null || RightPane.ActiveTab is null)
            return;

        string leftPath = LeftPane.ActiveTab.CurrentPath;
        string rightPath = RightPane.ActiveTab.CurrentPath;
        if (string.IsNullOrEmpty(leftPath) || string.IsNullOrEmpty(rightPath))
            return;

        bool wasSync = IsSyncNavigationEnabled;
        IsSyncNavigationEnabled = false;
        try
        {
            await LeftPane.ActiveTab.NavigateAsync(rightPath);
            await RightPane.ActiveTab.NavigateAsync(leftPath);
            StatusMessage = "Swapped Left and Right pane locations.";
        }
        finally
        {
            IsSyncNavigationEnabled = wasSync;
        }
    }

    [RelayCommand]
    public void ToggleSyncNavigation()
    {
        IsSyncNavigationEnabled = !IsSyncNavigationEnabled;
        StatusMessage = IsSyncNavigationEnabled
            ? "Synchronized Dual-Pane navigation enabled."
            : "Synchronized Dual-Pane navigation disabled.";
    }

    /// <summary>Toggles compare mode: the first press highlights differences, the next one clears them.</summary>
    [RelayCommand]
    public void ComparePanes()
    {
        if (IsComparingPanes)
        {
            StopComparingPanes();
            StatusMessage = "Folder comparison turned off.";
            return;
        }

        if (!IsDualPane || LeftPane.ActiveTab is null || RightPane.ActiveTab is null)
        {
            StatusMessage = "Enable Dual-Pane mode to compare two folders.";
            return;
        }

        IsComparingPanes = true;
        int diffCount = HighlightPaneDifferences();
        StatusMessage = diffCount == 0
            ? "Folders match — no unique or modified differences detected."
            : $"Folder comparison highlighted {diffCount:N0} difference(s).";
    }

    private void StopComparingPanes()
    {
        IsComparingPanes = false;
        foreach (var tab in LeftPane.Tabs.Concat(RightPane.Tabs))
            tab.ClearComparisonHighlights();
    }

    private int HighlightPaneDifferences()
    {
        if (LeftPane.ActiveTab is not { } leftTab || RightPane.ActiveTab is not { } rightTab)
            return 0;

        // Tabs that are no longer side by side drop their old highlights.
        foreach (var tab in LeftPane.Tabs.Concat(RightPane.Tabs))
        {
            if (!ReferenceEquals(tab, leftTab) && !ReferenceEquals(tab, rightTab))
                tab.ClearComparisonHighlights();
        }

        var leftItems = leftTab.GetAllLoadedItems();
        var rightItems = rightTab.GetAllLoadedItems();

        // Search tabs can list the same name from different folders; compare against the first one.
        var leftMap = leftItems.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rightMap = rightItems.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int diffCount = 0;

        foreach (var l in leftItems)
        {
            if (!rightMap.TryGetValue(l.Name, out var match))
            {
                l.IsComparisonDifferent = true;
                l.ComparisonBadge = "Unique";
                diffCount++;
            }
            else if (!l.IsDirectory && !match.IsDirectory && l.SizeBytes != match.SizeBytes)
            {
                l.IsComparisonDifferent = true;
                l.ComparisonBadge = "Size ≠";
                diffCount++;
            }
            else if (!l.IsDirectory && !match.IsDirectory && Math.Abs((l.ModifiedTime - match.ModifiedTime).TotalSeconds) > 2)
            {
                l.IsComparisonDifferent = true;
                l.ComparisonBadge = l.ModifiedTime > match.ModifiedTime ? "Newer" : "Older";
                diffCount++;
            }
            else
            {
                l.IsComparisonDifferent = false;
                l.ComparisonBadge = null;
            }
        }

        foreach (var r in rightItems)
        {
            if (!leftMap.TryGetValue(r.Name, out var match))
            {
                r.IsComparisonDifferent = true;
                r.ComparisonBadge = "Unique";
                diffCount++;
            }
            else if (!r.IsDirectory && !match.IsDirectory && r.SizeBytes != match.SizeBytes)
            {
                r.IsComparisonDifferent = true;
                r.ComparisonBadge = "Size ≠";
            }
            else if (!r.IsDirectory && !match.IsDirectory && Math.Abs((r.ModifiedTime - match.ModifiedTime).TotalSeconds) > 2)
            {
                r.IsComparisonDifferent = true;
                r.ComparisonBadge = r.ModifiedTime > match.ModifiedTime ? "Newer" : "Older";
            }
            else
            {
                r.IsComparisonDifferent = false;
                r.ComparisonBadge = null;
            }
        }

        return diffCount;
    }

    [RelayCommand]
    public async Task CopyToOtherPaneAsync()
    {
        await TransferToOtherPaneAsync(isMove: false);
    }

    [RelayCommand]
    public async Task MoveToOtherPaneAsync()
    {
        await TransferToOtherPaneAsync(isMove: true);
    }

    private async Task TransferToOtherPaneAsync(bool isMove)
    {
        if (!IsDualPane || ActiveTab is null || ActiveTab.SelectedItems.Count == 0)
            return;

        string? destDir = InactivePane.ActiveTab?.CurrentPath;
        if (string.IsNullOrWhiteSpace(destDir) || !Directory.Exists(destDir))
        {
            StatusMessage = "Destination pane does not have a valid open folder.";
            return;
        }

        var paths = ActiveTab.SelectedItems.Select(i => i.FullPath).ToList();
        await EnqueueOrRunTransferAsync(paths, destDir, isMove);
    }

    // ─── Active Pane Tab Shortcuts ─────────────────────────────────────────────

    [RelayCommand]
    public Task NewTabAsync(string? path = null) => ActivePane.NewTabAsync(path);

    [RelayCommand]
    public Task DuplicateTabAsync() => ActivePane.DuplicateTabAsync();

    [RelayCommand]
    public void CloseTab() => ActivePane.CloseTab();

    [RelayCommand]
    public Task RestoreClosedTabAsync() => ActivePane.RestoreClosedTabAsync();

    [RelayCommand]
    public void NextTab() => ActivePane.NextTab();

    [RelayCommand]
    public void PreviousTab() => ActivePane.PreviousTab();

    public void PromptRenameTab(BrowserTabViewModel tab)
    {
        string current = tab.CustomLabel ?? tab.Title;
        string? label = _dialogService.PromptTextInput(
            "Rename Tab Label",
            "Enter a custom display label for this tab:",
            current,
            validateAsFileName: false);

        if (label is not null)
            tab.CustomLabel = label;
    }

    // ─── Navigation ────────────────────────────────────────────────────────────

    private bool CanNavigateBack() => ActiveTab?.CanGoBack == true;
    private bool CanNavigateForward() => ActiveTab?.CanGoForward == true;
    private bool CanNavigateUp() => ActiveTab?.CanGoUp == true;

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    public async Task NavigateBackAsync()
    {
        if (ActiveTab is not null)
            await ActiveTab.GoBackAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateForward))]
    public async Task NavigateForwardAsync()
    {
        if (ActiveTab is not null)
            await ActiveTab.GoForwardAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateUp))]
    public async Task NavigateUpAsync()
    {
        if (ActiveTab is not null)
            await ActiveTab.GoUpAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        RefreshDrivesAndTree();
        foreach (var tab in GetAllOpenTabs())
            await tab.RefreshAsync(preserveSelection: true);

        StatusMessage = "Refreshed workspace.";
    }

    [RelayCommand]
    public async Task NavigateToPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string normalized = PathValidator.NormalizePath(path);

        if (File.Exists(normalized))
        {
            var (ok, err) = _shellService.OpenWithDefaultApplication(normalized);
            if (!ok && err is not null)
                _dialogService.ShowError("Open File", err);
            if (ActiveTab is not null)
            {
                ActiveTab.IsAddressEditing = false;
                ActiveTab.AddressInputText = ActiveTab.CurrentPath;
            }
            return;
        }

        bool okNav = await ActivePane.NavigateActiveOrSpawnIfLockedAsync(normalized);
        if (!okNav && ActiveTab?.ErrorMessage is { } msg)
            StatusMessage = msg;
        else if (okNav && ActiveTab is not null)
            StatusMessage = $"Opened {ActiveTab.CurrentPath}";
    }

    [RelayCommand]
    public void BeginAddressEdit()
    {
        if (ActiveTab is null)
            return;

        ActiveTab.AddressInputText = ActiveTab.CurrentPath;
        ActiveTab.IsAddressEditing = true;
        RequestFocusAddressBar?.Invoke();
    }

    [RelayCommand]
    public void FocusQuickFilter()
    {
        RequestFocusQuickFilter?.Invoke();
    }

    [RelayCommand]
    public async Task OpenAdvancedSearchAsync()
    {
        var defaults = new List<string>();
        if (!string.IsNullOrWhiteSpace(ActiveTab?.CurrentPath) && Directory.Exists(ActiveTab.CurrentPath))
            defaults.Add(ActiveTab.CurrentPath);
        if (IsDualPane && !string.IsNullOrWhiteSpace(InactivePane.ActiveTab?.CurrentPath) &&
            Directory.Exists(InactivePane.ActiveTab.CurrentPath) &&
            !defaults.Any(d => PathValidator.PathsEqual(d, InactivePane.ActiveTab.CurrentPath)))
        {
            defaults.Add(InactivePane.ActiveTab.CurrentPath);
        }

        if (defaults.Count == 0)
            defaults.Add(_settingsService.Current.DefaultStartPath);

        SearchRequest? req = _dialogService.ShowAdvancedSearchDialog(
            _searchService,
            defaults,
            _settingsService.Current.ShowHiddenFiles);

        if (req is null)
            return;

        var cts = CancellationHelper.Replace(ref _searchCts);

        var searchTab = ActivePane.CreateTabInstance();
        string queryTitle = string.IsNullOrWhiteSpace(req.QueryText) ? "All Items" : req.QueryText;
        searchTab.InitializeAsSearchTab(queryTitle, req.RootLocations[0]);
        ActivePane.Tabs.Add(searchTab);
        ActivePane.ActiveTab = searchTab;

        IsOperationBusy = true;
        StatusMessage = $"Searching for '{queryTitle}'...";

        try
        {
            var (matched, err) = await _searchService.ExecuteSearchAsync(
                req,
                batch =>
                {
                    if (Application.Current?.Dispatcher is { } disp)
                    {
                        disp.Invoke(() =>
                        {
                            _metadataService.DecorateItems(batch);
                            searchTab.AppendSearchResultsBatch(batch);
                            StatusMessage = $"Searching... {searchTab.TotalItemCount:N0} match(es) found";
                        });
                    }
                    else
                    {
                        searchTab.AppendSearchResultsBatch(batch);
                    }
                },
                cts.Token);

            if (err is not null)
            {
                _dialogService.ShowError("Search Error", err);
                StatusMessage = $"Search error: {err}";
            }
            else
            {
                StatusMessage = $"Search complete — {matched:N0} match(es) for '{queryTitle}'.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Search stopped ({searchTab.TotalItemCount:N0} match(es) kept).";
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Search Aborted", ex.Message);
            StatusMessage = $"Search aborted: {ex.Message}";
        }
        finally
        {
            CancellationHelper.Release(ref _searchCts, cts);
            IsOperationBusy = false;
        }
    }

    private void TriggerThumbnailLoadForTab(BrowserTabViewModel tab)
    {
        if (tab.ViewMode != BrowserViewMode.Thumbnails)
            return;

        var cts = CancellationHelper.Replace(ref _thumbnailsCts);
        var snapshot = tab.DisplayedItems.ToList();
        _ = PopulateThumbnailsAsync(snapshot, cts);
    }

    private async Task PopulateThumbnailsAsync(List<FileEntryItem> snapshot, CancellationTokenSource cts)
    {
        try
        {
            await _thumbnailService.PopulateThumbnailsForTabAsync(snapshot, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CancellationHelper.Release(ref _thumbnailsCts, cts);
        }
    }

    // ─── Favorites & Workspaces ────────────────────────────────────────────────

    [RelayCommand]
    public void AddCurrentFolderToFavorites()
    {
        if (ActiveTab is not null && !ActiveTab.IsSearchTab && !string.IsNullOrWhiteSpace(ActiveTab.CurrentPath))
            AddPathToFavorites(ActiveTab.CurrentPath);
    }

    [RelayCommand]
    public void AddSelectedToFavorites()
    {
        if (ActiveTab?.SelectedItem is { IsDirectory: true } dir)
            AddPathToFavorites(dir.FullPath);
        else
            AddCurrentFolderToFavorites();
    }

    public void DropPathsIntoFavorites(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string p in paths)
        {
            if (Directory.Exists(p) && !FavoriteItems.Any(f => PathValidator.PathsEqual(f.Path, p)))
            {
                string label = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(label)) label = p;
                FavoriteItems.Add(new FavoriteItem { Name = label, Path = p });
                added++;
            }
        }

        if (added > 0)
        {
            _favoritesService.SaveFavorites(FavoriteItems);
            StatusMessage = $"Added {added} folder(s) to Favorites.";
        }
    }

    private void AddPathToFavorites(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return;

        if (FavoriteItems.Any(f => PathValidator.PathsEqual(f.Path, folderPath)))
        {
            StatusMessage = "Folder is already in Favorites.";
            return;
        }

        string label = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(label))
            label = folderPath;

        FavoriteItems.Add(new FavoriteItem { Name = label, Path = folderPath });
        _favoritesService.SaveFavorites(FavoriteItems);
        StatusMessage = $"Added '{label}' to Favorites.";
    }

    [RelayCommand]
    public void RenameFavorite(FavoriteItem? item)
    {
        if (item is null)
            return;

        string? newLabel = _dialogService.PromptTextInput(
            "Rename Favorite",
            "Enter display label for this favorite folder:",
            item.Name,
            validateAsFileName: false);

        if (string.IsNullOrWhiteSpace(newLabel))
            return;

        int idx = FavoriteItems.IndexOf(item);
        if (idx >= 0)
        {
            FavoriteItems[idx] = new FavoriteItem { Name = newLabel, Path = item.Path, Category = item.Category };
            _favoritesService.SaveFavorites(FavoriteItems);
            StatusMessage = $"Renamed favorite to '{newLabel}'.";
        }
    }

    [RelayCommand]
    public void RemoveFavorite(FavoriteItem? item)
    {
        if (item is null)
            return;

        if (FavoriteItems.Remove(item))
        {
            _favoritesService.SaveFavorites(FavoriteItems);
            StatusMessage = $"Removed '{item.Name}' from Favorites.";
        }
    }

    [RelayCommand]
    public void ToggleMiniTreeMode()
    {
        MiniTreeMode = !MiniTreeMode;
        _settingsService.Current.MiniTreeMode = MiniTreeMode;
        _settingsService.Save();
    }

    [RelayCommand]
    public void SaveCurrentWorkspace()
    {
        string? name = _dialogService.PromptTextInput(
            "Save Workspace Layout",
            "Enter a name for this workspace (panes + tabs):",
            $"Workspace {SavedWorkspaces.Count + 1}",
            validateAsFileName: false);

        if (string.IsNullOrWhiteSpace(name))
            return;

        var existing = SavedWorkspaces.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            SavedWorkspaces.Remove(existing);

        SavedWorkspaces.Add(new WorkspacePreset
        {
            Name = name,
            Session = BuildCurrentSessionState()
        });

        _sessionService.SaveWorkspaces(SavedWorkspaces);
        StatusMessage = $"Saved workspace '{name}'.";
    }

    [RelayCommand]
    public async Task LoadWorkspaceAsync(WorkspacePreset? preset)
    {
        if (preset is null)
            return;

        await ApplySessionStateAsync(preset.Session);
        StatusMessage = $"Loaded workspace '{preset.Name}'.";
    }

    [RelayCommand]
    public void DeleteWorkspace(WorkspacePreset? preset)
    {
        if (preset is null)
            return;

        if (SavedWorkspaces.Remove(preset))
        {
            _sessionService.SaveWorkspaces(SavedWorkspaces);
            StatusMessage = $"Deleted workspace '{preset.Name}'.";
        }
    }

    private void ReloadSavedWorkspaces()
    {
        SavedWorkspaces.Clear();
        foreach (var w in _sessionService.LoadWorkspaces())
            SavedWorkspaces.Add(w);
    }

    // ─── Core File Operations ──────────────────────────────────────────────────

    [RelayCommand]
    public async Task OpenSelectedAsync()
    {
        if (ActiveTab is null || ActiveTab.SelectedItems.Count == 0)
            return;

        if (ActiveTab.SelectedItems.Count == 1 && ActiveTab.SelectedItems[0].IsDirectory)
        {
            await ActivePane.NavigateActiveOrSpawnIfLockedAsync(ActiveTab.SelectedItems[0].FullPath);
            return;
        }

        foreach (var entry in ActiveTab.SelectedItems)
        {
            if (entry.IsDirectory)
            {
                await ActivePane.NewTabAsync(entry.FullPath);
            }
            else
            {
                var (ok, err) = _shellService.OpenWithDefaultApplication(entry.FullPath);
                if (!ok && err is not null)
                    _dialogService.ShowError("Open File", err);
            }
        }
    }

    [RelayCommand]
    public async Task OpenSelectedInNewTabAsync()
    {
        if (ActiveTab?.SelectedItem is { IsDirectory: true } folder)
            await ActivePane.NewTabAsync(folder.FullPath);
    }

    [RelayCommand]
    public async Task OpenContainingFolderAsync()
    {
        if (ActiveTab?.SelectedItem is not { } item)
            return;

        string? parent = Path.GetDirectoryName(item.FullPath);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
        {
            if (ActiveTab.IsSearchTab)
            {
                await ActivePane.NewTabAsync(parent);
                if (ActivePane.ActiveTab is not null)
                    await ActivePane.ActiveTab.RefreshAsync(preserveSelection: false, explicitSelectPath: item.FullPath);
            }
            else
            {
                await ActiveTab.NavigateAsync(parent, recordHistory: true, selectPathAfterLoad: item.FullPath);
            }
        }
    }

    [RelayCommand]
    public void RevealSelectedInExplorer()
    {
        string? target = ActiveTab?.SelectedItem?.FullPath ?? ActiveTab?.CurrentPath;
        if (string.IsNullOrWhiteSpace(target))
            return;

        var (ok, err) = _shellService.RevealInWindowsExplorer(target);
        if (!ok && err is not null)
            _dialogService.ShowError("Windows Explorer", err);
    }

    [RelayCommand]
    public void ShowProperties()
    {
        string? target = ActiveTab?.SelectedItem?.FullPath ?? ActiveTab?.CurrentPath;
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (!_shellService.ShowNativePropertiesDialog(target))
            StatusMessage = "Properties dialog unavailable for this item.";
    }

    [RelayCommand]
    public async Task CreateNewFolderAsync()
    {
        if (ActiveTab is null || ActiveTab.IsSearchTab || string.IsNullOrWhiteSpace(ActiveTab.CurrentPath))
            return;

        string? input = _dialogService.PromptTextInput(
            "New Folder",
            "Enter a name for the new folder:",
            "New folder");

        if (input is null)
            return;

        var (success, createdPath, error) = await _fileOperationService.CreateFolderAsync(ActiveTab.CurrentPath, input.Trim());
        if (!success)
        {
            _dialogService.ShowError("Create Folder Failed", error ?? "Unknown error.");
            return;
        }

        await ActiveTab.RefreshAsync(preserveSelection: false, explicitSelectPath: createdPath);
        StatusMessage = $"Created folder '{input.Trim()}'.";
    }

    [RelayCommand]
    public async Task CreateNewFileAsync()
    {
        if (ActiveTab is null || ActiveTab.IsSearchTab || string.IsNullOrWhiteSpace(ActiveTab.CurrentPath))
            return;

        const string defaultName = "New Document.txt";
        int stemLength = Path.GetFileNameWithoutExtension(defaultName).Length;

        string? input = _dialogService.PromptTextInput(
            "New Text File",
            "Enter a name for the new file:",
            defaultName,
            selectPrefixLength: stemLength);

        if (input is null)
            return;

        var (success, createdPath, error) = await _fileOperationService.CreateTextFileAsync(ActiveTab.CurrentPath, input.Trim());
        if (!success)
        {
            _dialogService.ShowError("Create File Failed", error ?? "Unknown error.");
            return;
        }

        await ActiveTab.RefreshAsync(preserveSelection: false, explicitSelectPath: createdPath);
        StatusMessage = $"Created file '{input.Trim()}'.";
    }

    [RelayCommand]
    public async Task RenameSelectedAsync()
    {
        if (ActiveTab?.SelectedItem is not { } target)
            return;

        int selectLen = target.IsDirectory
            ? target.Name.Length
            : Path.GetFileNameWithoutExtension(target.Name).Length;
        if (selectLen <= 0)
            selectLen = target.Name.Length;

        string? newName = _dialogService.PromptTextInput(
            "Rename",
            $"Rename '{target.Name}' to:",
            target.Name,
            selectPrefixLength: selectLen);

        if (newName is null || string.Equals(newName, target.Name, StringComparison.Ordinal))
            return;

        var (success, newPath, error) = await _fileOperationService.RenameAsync(target.FullPath, newName.Trim());
        if (!success)
        {
            _dialogService.ShowError("Rename Failed", error ?? "Unable to rename item.");
            return;
        }

        await RefreshAffectedTabsAsync();
        if (ActiveTab is not null && !ActiveTab.IsSearchTab)
            await ActiveTab.RefreshAsync(preserveSelection: false, explicitSelectPath: newPath);

        StatusMessage = $"Renamed to '{newName.Trim()}'.";
    }

    [RelayCommand]
    public async Task OpenBatchRenameAsync()
    {
        if (ActiveTab is null)
            return;

        var targets = ActiveTab.SelectedItems.Count > 0
            ? ActiveTab.SelectedItems.Select(i => i.FullPath).ToList()
            : ActiveTab.DisplayedItems.Select(i => i.FullPath).ToList();

        if (targets.Count == 0)
        {
            StatusMessage = "Select one or more items to batch rename.";
            return;
        }

        int? count = _dialogService.ShowBatchRenameDialog(_batchRenameService, targets);
        CanUndoBatchRename = _batchRenameService.CanUndo;
        UndoLastBatchRenameCommand.NotifyCanExecuteChanged();

        if (count.HasValue && count.Value > 0)
        {
            await RefreshAffectedTabsAsync();
            StatusMessage = $"Batch renamed {count.Value:N0} item(s). Press Ctrl+Z to undo.";
        }
    }

    private bool CanExecuteUndoBatchRename() => _batchRenameService.CanUndo;

    [RelayCommand(CanExecute = nameof(CanExecuteUndoBatchRename))]
    public async Task UndoLastBatchRenameAsync()
    {
        var (ok, restoredCount, err) = await _batchRenameService.UndoLastBatchRenameWrapperAsync();
        CanUndoBatchRename = _batchRenameService.CanUndo;
        UndoLastBatchRenameCommand.NotifyCanExecuteChanged();

        if (!ok)
        {
            _dialogService.ShowError("Undo Batch Rename", err ?? "Unable to undo batch rename.");
            return;
        }

        await RefreshAffectedTabsAsync();
        StatusMessage = $"Undid batch rename ({restoredCount:N0} item(s) restored).";
    }

    [RelayCommand]
    public void ToggleOperationQueuePanel()
    {
        ShowOperationQueuePanel = !ShowOperationQueuePanel;
    }

    [RelayCommand]
    public void ClearCompletedQueueJobs()
    {
        _operationQueue.ClearFinishedJobs();
    }

    [RelayCommand]
    public void CopySelected()
    {
        if (ActiveTab is null || ActiveTab.SelectedItems.Count == 0)
            return;

        var paths = ActiveTab.SelectedItems.Select(i => i.FullPath).ToList();
        _fileOperationService.SetClipboardFiles(paths, isCut: false);
        _activeCutPaths = null;
        UpdateCutVisualsAcrossTabs();
        StatusMessage = $"Copied {paths.Count:N0} item{(paths.Count == 1 ? "" : "s")} to clipboard.";
    }

    [RelayCommand]
    public void CutSelected()
    {
        if (ActiveTab is null || ActiveTab.SelectedItems.Count == 0)
            return;

        var paths = ActiveTab.SelectedItems.Select(i => i.FullPath).ToList();
        _fileOperationService.SetClipboardFiles(paths, isCut: true);
        _activeCutPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        UpdateCutVisualsAcrossTabs();
        StatusMessage = $"Cut {paths.Count:N0} item{(paths.Count == 1 ? "" : "s")} — press Ctrl+V in destination.";
    }

    [RelayCommand]
    public async Task PasteAsync()
    {
        if (ActiveTab is null || ActiveTab.IsSearchTab || string.IsNullOrWhiteSpace(ActiveTab.CurrentPath))
            return;

        var (paths, isCut) = _fileOperationService.GetClipboardFiles();
        if (paths.Count == 0)
        {
            StatusMessage = "Clipboard does not contain any files or folders.";
            return;
        }

        await EnqueueOrRunTransferAsync(paths, ActiveTab.CurrentPath, isMove: isCut);

        if (isCut)
        {
            Clipboard.Clear();
            _activeCutPaths = null;
            UpdateCutVisualsAcrossTabs();
        }
    }

    public async Task EnqueueOrRunTransferAsync(IReadOnlyList<string> sourcePaths, string destinationPath, bool isMove)
    {
        if (QueuedTransferHandler is not null)
        {
            await QueuedTransferHandler(sourcePaths, destinationPath, isMove);
            return;
        }

        await RunFileTransferAsync(sourcePaths, destinationPath, isMove);
    }

    public async Task RunFileTransferAsync(IReadOnlyList<string> sourcePaths, string destinationPath, bool isMove)
    {
        if (IsOperationBusy)
            return;

        var cts = CancellationHelper.Replace(ref _operationCts);

        IsOperationBusy = true;
        var progress = new Progress<string>(msg => StatusMessage = msg);

        try
        {
            FileOperationResult result = await _fileOperationService.TransferItemsAsync(
                sourcePaths,
                destinationPath,
                isMove,
                conflictPrompt: _dialogService.ResolveConflictAsync,
                statusProgress: progress,
                cancellationToken: cts.Token);

            await RefreshAffectedTabsAsync();

            if (result.IsCancelled)
            {
                StatusMessage = $"Operation cancelled ({result.SucceededCount:N0} completed).";
            }
            else if (result.HasErrors)
            {
                StatusMessage = $"Completed with {result.Errors.Count} error(s) ({result.SucceededCount:N0} succeeded).";
                _dialogService.ShowError(
                    isMove ? "Move Completed with Errors" : "Copy Completed with Errors",
                    string.Join(Environment.NewLine, result.Errors.Take(10)));
            }
            else
            {
                string verb = isMove ? "Moved" : "Copied";
                string skipNote = result.SkippedCount > 0 ? $" ({result.SkippedCount} skipped)" : string.Empty;
                StatusMessage = $"{verb} {result.SucceededCount:N0} item{(result.SucceededCount == 1 ? "" : "s")}{skipNote}.";
            }
        }
        finally
        {
            CancellationHelper.Release(ref _operationCts, cts);
            IsOperationBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteToRecycleBinAsync()
    {
        await ExecuteDeleteAsync(permanent: false);
    }

    [RelayCommand]
    public async Task DeletePermanentlyAsync()
    {
        await ExecuteDeleteAsync(permanent: true);
    }

    private async Task ExecuteDeleteAsync(bool permanent)
    {
        if (ActiveTab is null || ActiveTab.SelectedItems.Count == 0 || IsOperationBusy)
            return;

        var selected = ActiveTab.SelectedItems.ToList();
        bool isNetworkPath = selected.Any(s => s.FullPath.StartsWith(@"\\", StringComparison.Ordinal));
        bool effectivePermanent = permanent || isNetworkPath;
        bool containsSystemOrReadOnly = selected.Any(i => i.IsSystem || i.IsReadOnly);

        if (effectivePermanent || _settingsService.Current.ConfirmRecycleBinDelete || containsSystemOrReadOnly)
        {
            string subject = selected.Count == 1
                ? $"'{selected[0].Name}'"
                : $"{selected.Count:N0} selected items";

            string warning = effectivePermanent
                ? $"Are you sure you want to PERMANENTLY delete {subject}?{(isNetworkPath && !permanent ? "\n\nNote: Network paths do not use the local Recycle Bin." : "")}\n\nThis action cannot be undone."
                : $"Move {subject} to the Windows Recycle Bin?";

            if (containsSystemOrReadOnly)
                warning += "\n\nWarning: Selection includes read-only or system items.";

            bool confirmed = _dialogService.ConfirmAction(
                effectivePermanent ? "Permanent Delete" : "Move to Recycle Bin",
                warning,
                effectivePermanent ? "Delete Permanently" : "Move to Recycle Bin",
                isDanger: effectivePermanent);

            if (!confirmed)
                return;
        }

        var cts = CancellationHelper.Replace(ref _operationCts);

        IsOperationBusy = true;
        var progress = new Progress<string>(msg => StatusMessage = msg);

        try
        {
            var paths = selected.Select(s => s.FullPath).ToList();
            FileOperationResult result = await _fileOperationService.DeleteItemsAsync(
                paths,
                permanent: effectivePermanent,
                statusProgress: progress,
                cancellationToken: cts.Token);

            // Drop the deleted rows in place. Reloading every open tab and rebuilding the drive list and folder
            // tree here made each delete slower (and collapsed the tree); the folder watchers reconcile anyway.
            var removed = new HashSet<string>(result.RemovedPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var tab in GetAllOpenTabs())
                tab.RemoveEntries(removed);
            UpdateDriveSpaceSummary(ActiveTab?.CurrentPath);

            if (result.HasErrors)
            {
                StatusMessage = $"Delete finished with {result.Errors.Count} error(s).";
                _dialogService.ShowError("Delete Error", string.Join(Environment.NewLine, result.Errors.Take(10)));
            }
            else
            {
                StatusMessage = effectivePermanent
                    ? $"Permanently deleted {result.SucceededCount:N0} item{(result.SucceededCount == 1 ? "" : "s")}."
                    : $"Moved {result.SucceededCount:N0} item{(result.SucceededCount == 1 ? "" : "s")} to Recycle Bin.";
            }
        }
        finally
        {
            CancellationHelper.Release(ref _operationCts, cts);
            IsOperationBusy = false;
        }
    }

    [RelayCommand]
    public void CancelActiveOperation()
    {
        CancellationHelper.SafeCancel(_operationCts);
        CancellationHelper.SafeCancel(_searchCts);
    }

    [RelayCommand]
    public void SelectAll()
    {
        RequestSelectAllInActiveList?.Invoke();
    }

    // ─── Color Tags & File Notes ───────────────────────────────────────────────

    [RelayCommand]
    public void SetColorTag(string? colorHex)
    {
        if (ActiveTab is null || ActiveTab.SelectedItems.Count == 0)
            return;

        string? normalizedHex = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex;
        foreach (var item in ActiveTab.SelectedItems)
        {
            item.ColorTagHex = normalizedHex;
            _metadataService.SetColorTag(item.FullPath, normalizedHex);
        }

        StatusMessage = normalizedHex is null ? "Cleared color tag." : "Applied color tag.";
    }

    [RelayCommand]
    public void SaveSelectedItemNote()
    {
        if (ActiveTab?.SelectedItem is not { } item)
            return;

        item.Note = string.IsNullOrWhiteSpace(SelectedItemNote) ? null : SelectedItemNote.Trim();
        _metadataService.SetNote(item.FullPath, item.Note);
        StatusMessage = $"Saved note for '{item.Name}'.";
    }

    // ─── Path Copy Helpers ─────────────────────────────────────────────────────

    [RelayCommand]
    public void CopyFullPath()
    {
        if (ActiveTab is null)
            return;

        string text = ActiveTab.SelectedItems.Count > 0
            ? string.Join(Environment.NewLine, ActiveTab.SelectedItems.Select(i => i.FullPath))
            : ActiveTab.CurrentPath;

        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            StatusMessage = "Copied full path(s) to clipboard.";
        }
    }

    [RelayCommand]
    public void CopyFileName()
    {
        if (ActiveTab is null || ActiveTab.SelectedItems.Count == 0)
            return;

        string text = string.Join(Environment.NewLine, ActiveTab.SelectedItems.Select(i => i.Name));
        Clipboard.SetText(text);
        StatusMessage = $"Copied {ActiveTab.SelectedItems.Count:N0} filename(s) to clipboard.";
    }

    [RelayCommand]
    public void CopyRelativePath()
    {
        if (ActiveTab is null || ActiveTab.SelectedItems.Count == 0 || string.IsNullOrEmpty(ActiveTab.CurrentPath))
            return;

        string baseDir = ActiveTab.CurrentPath;
        string text = string.Join(
            Environment.NewLine,
            ActiveTab.SelectedItems.Select(i => Path.GetRelativePath(baseDir, i.FullPath)));

        Clipboard.SetText(text);
        StatusMessage = "Copied relative path(s) to clipboard.";
    }

    // ─── View Modes, Panels & Settings ─────────────────────────────────────────

    [RelayCommand]
    public void SetViewMode(BrowserViewMode mode)
    {
        _settingsService.Current.DefaultViewMode = mode;
        if (ActiveTab is not null)
            ActiveTab.ViewMode = mode;
        _settingsService.Save();
        OnPropertyChanged(nameof(ActiveTab));
    }

    [RelayCommand]
    public async Task ToggleHiddenFilesAsync()
    {
        ShowHiddenFiles = !ShowHiddenFiles;
        _settingsService.Current.ShowHiddenFiles = ShowHiddenFiles;
        _settingsService.Save();

        RefreshDrivesAndTree();
        await RefreshAffectedTabsAsync();

        StatusMessage = ShowHiddenFiles ? "Showing hidden files." : "Hidden files concealed.";
    }

    [RelayCommand]
    public void TogglePreviewPanel()
    {
        ShowPreviewPanel = !ShowPreviewPanel;
        _settingsService.Current.ShowPreviewPanel = ShowPreviewPanel;
        _settingsService.Save();

        if (ShowPreviewPanel && ActiveTab is not null)
            _ = RefreshPreviewAsync(ActiveTab.SelectedItem);
    }

    [RelayCommand]
    public void ToggleSidebar()
    {
        ShowSidebar = !ShowSidebar;
        _settingsService.Current.ShowSidebar = ShowSidebar;
        _settingsService.Save();
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark;
        _themeService.ApplyTheme(CurrentTheme);
        _settingsService.Current.Theme = CurrentTheme;
        _settingsService.Save();
        StatusMessage = $"Switched to {CurrentTheme} theme.";
    }

    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        bool previousHidden = _settingsService.Current.ShowHiddenFiles;
        bool previousSystem = _settingsService.Current.ShowSystemFiles;
        AppThemeMode previousTheme = _settingsService.Current.Theme;

        bool saved = _dialogService.ShowSettingsDialog(
            _settingsService.Current,
            _shortcutService,
            _pathsService.StorageRoot,
            _pathsService.IsPortableWritable);

        if (!saved)
            return;

        ShowHiddenFiles = _settingsService.Current.ShowHiddenFiles;
        ShowSidebar = _settingsService.Current.ShowSidebar;
        ShowPreviewPanel = _settingsService.Current.ShowPreviewPanel;
        ShowToolbarTextLabels = _settingsService.Current.ShowToolbarTextLabels;
        ShowToolbarBatchRename = _settingsService.Current.ShowToolbarBatchRename;
        ShowToolbarSearch = _settingsService.Current.ShowToolbarSearch;
        ShowToolbarDualPane = _settingsService.Current.ShowToolbarDualPane;
        ShowToolbarToolsMenu = _settingsService.Current.ShowToolbarToolsMenu;

        if (_settingsService.Current.Theme != previousTheme)
        {
            CurrentTheme = _settingsService.Current.Theme;
            _themeService.ApplyTheme(CurrentTheme);
        }

        _settingsService.Save();

        if (previousHidden != _settingsService.Current.ShowHiddenFiles ||
            previousSystem != _settingsService.Current.ShowSystemFiles)
        {
            RefreshDrivesAndTree();
            await RefreshAffectedTabsAsync();
        }

        StatusMessage = "Settings saved.";
    }

    // ─── Productivity Modules (Checksums, Duplicate Finder, Folder Size) ───────

    [RelayCommand]
    public void OpenChecksumTool()
    {
        if (ActiveTab is null)
            return;

        var selectedFiles = ActiveTab.SelectedItems
            .Where(i => !i.IsDirectory && File.Exists(i.FullPath))
            .Select(i => i.FullPath)
            .ToList();

        if (selectedFiles.Count == 0)
        {
            StatusMessage = "Select one or more files to calculate MD5 / SHA checksums.";
            _dialogService.ShowError("Checksum Calculator", "Please select at least one file (not a folder) in the browser first.");
            return;
        }

        _dialogService.ShowChecksumDialog(_checksumService, selectedFiles);
        StatusMessage = $"Inspected checksums for {selectedFiles.Count:N0} file(s).";
    }

    [RelayCommand]
    public async Task OpenDuplicateFinderAsync()
    {
        string targetFolder = ResolveTargetFolderForTool();
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
        {
            StatusMessage = "Open or select a valid folder to scan for duplicates.";
            return;
        }

        bool modified = _dialogService.ShowDuplicateFinderDialog(
            _duplicateFinderService,
            _fileOperationService,
            targetFolder,
            _settingsService.Current.ShowHiddenFiles);

        if (modified)
        {
            await RefreshAffectedTabsAsync();
            StatusMessage = "Refreshed tabs after duplicate cleanup.";
        }
    }

    [RelayCommand]
    public async Task OpenFolderSizeAnalyzerAsync()
    {
        string targetFolder = ResolveTargetFolderForTool();
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
        {
            StatusMessage = "Open or select a valid folder to analyze disk usage.";
            return;
        }

        string? requestedTabPath = _dialogService.ShowFolderSizeDialog(_folderSizeAnalyzerService, targetFolder);
        if (!string.IsNullOrWhiteSpace(requestedTabPath) && Directory.Exists(requestedTabPath))
        {
            await ActivePane.NewTabAsync(requestedTabPath);
            StatusMessage = $"Opened '{requestedTabPath}' in new tab.";
        }
    }

    private string ResolveTargetFolderForTool()
    {
        if (ActiveTab?.SelectedItem is { IsDirectory: true } selectedDir && Directory.Exists(selectedDir.FullPath))
            return selectedDir.FullPath;

        if (!string.IsNullOrWhiteSpace(ActiveTab?.CurrentPath) && Directory.Exists(ActiveTab.CurrentPath))
            return ActiveTab.CurrentPath;

        return _settingsService.Current.DefaultStartPath;
    }

    // ─── Image Preview Zoom Controls ───────────────────────────────────────────

    [RelayCommand]
    public void ZoomInPreview()
    {
        IsFitToPanel = false;
        ImageZoom = Math.Clamp(Math.Round(ImageZoom + 0.25, 2), 0.25, 6.0);
    }

    [RelayCommand]
    public void ZoomOutPreview()
    {
        IsFitToPanel = false;
        ImageZoom = Math.Clamp(Math.Round(ImageZoom - 0.25, 2), 0.25, 6.0);
    }

    [RelayCommand]
    public void FitPreviewToPanel()
    {
        IsFitToPanel = true;
        ImageZoom = 1.0;
    }

    [RelayCommand]
    public void ActualSizePreview()
    {
        IsFitToPanel = false;
        ImageZoom = 1.0;
    }

    // ─── Session Persistence & Internal Helpers ────────────────────────────────

    public AppSessionState BuildCurrentSessionState()
    {
        return new AppSessionState
        {
            IsDualPane = IsDualPane,
            SplitOrientation = SplitOrientation,
            VerticalSplitRatio = VerticalSplitRatio,
            HorizontalSplitRatio = HorizontalSplitRatio,
            ActivePaneId = ActivePane.PaneId,
            IsSyncNavigationEnabled = IsSyncNavigationEnabled,
            LeftPane = LeftPane.ToSessionState(),
            RightPane = RightPane.ToSessionState()
        };
    }

    public async Task ApplySessionStateAsync(AppSessionState session)
    {
        string fallback = _settingsService.Current.DefaultStartPath;
        IsDualPane = session.IsDualPane;
        SplitOrientation = session.SplitOrientation;
        VerticalSplitRatio = AppSessionState.ClampSplitRatio(session.VerticalSplitRatio);
        HorizontalSplitRatio = AppSessionState.ClampSplitRatio(session.HorizontalSplitRatio);
        IsSyncNavigationEnabled = session.IsSyncNavigationEnabled;

        await LeftPane.RestoreSessionAsync(session.LeftPane, fallback);
        await RightPane.RestoreSessionAsync(session.RightPane, fallback);

        if (IsDualPane && string.Equals(session.ActivePaneId, "Right", StringComparison.OrdinalIgnoreCase))
            ActivatePane(RightPane);
        else
            ActivatePane(LeftPane);
    }

    public void SaveSessionAndSettings(double width, double height, double left, double top, bool isMaximized, double sidebarWidth, double previewWidth)
    {
        AppSettings s = _settingsService.Current;
        if (!isMaximized)
        {
            s.WindowWidth = width;
            s.WindowHeight = height;
            s.WindowLeft = left;
            s.WindowTop = top;
        }
        s.IsWindowMaximized = isMaximized;
        if (sidebarWidth >= 160)
            s.SidebarWidth = sidebarWidth;
        if (previewWidth >= 200)
            s.PreviewPanelWidth = previewWidth;

        s.LastOpenTabs = LeftPane.Tabs
            .Where(t => !t.IsSearchTab && !string.IsNullOrWhiteSpace(t.CurrentPath))
            .Select(t => t.CurrentPath)
            .ToList();
        s.ActiveTabIndex = LeftPane.ActiveTab is not null ? Math.Max(0, LeftPane.Tabs.IndexOf(LeftPane.ActiveTab)) : 0;

        _settingsService.Save();
        _sessionService.SaveSession(BuildCurrentSessionState());
    }

    public async Task RefreshAffectedTabsAsync()
    {
        foreach (var tab in GetAllOpenTabs())
            await tab.RefreshAsync(preserveSelection: true);
    }

    private IEnumerable<BrowserTabViewModel> GetAllOpenTabs()
    {
        foreach (var t in LeftPane.Tabs)
            yield return t;
        foreach (var t in RightPane.Tabs)
            yield return t;
    }

    private async Task RefreshPreviewAsync(FileEntryItem? selected)
    {
        // Clear (not just dispose) the previous source: leaving a disposed instance in the field made the
        // next selection and MainViewModel.Dispose() throw ObjectDisposedException.
        CancellationHelper.CancelAndClear(ref _previewCts);

        SelectedItemNote = selected is not null
            ? (_metadataService.GetMetadata(selected.FullPath)?.Note ?? string.Empty)
            : string.Empty;

        if (!ShowPreviewPanel || selected is null)
        {
            CurrentPreview = new FilePreviewData { Kind = PreviewKind.None };
            return;
        }

        var cts = CancellationHelper.Replace(ref _previewCts);

        try
        {
            var data = await _previewService.LoadPreviewAsync(selected, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                IsFitToPanel = true;
                ImageZoom = 1.0;
                CurrentPreview = data;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("Preview", "Preview failed to load.", ex, new { path = selected.FullPath });
            if (!cts.IsCancellationRequested)
                CurrentPreview = new FilePreviewData { Kind = PreviewKind.None };
        }
        finally
        {
            CancellationHelper.Release(ref _previewCts, cts);
        }
    }

    private void UpdateCutVisualsAcrossTabs()
    {
        foreach (var tab in GetAllOpenTabs())
            tab.MarkCutItems(_activeCutPaths);
    }

    private void UpdateDriveSpaceSummary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            DriveSpaceSummary = string.Empty;
            return;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                DriveSpaceSummary = root ?? string.Empty;
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                DriveSpaceSummary = $"{root.TrimEnd('\\')} • {FileSizeFormatter.FormatBytes(drive.AvailableFreeSpace)} free of {FileSizeFormatter.FormatBytes(drive.TotalSize)}";
                return;
            }
        }
        catch
        {
        }

        DriveSpaceSummary = string.Empty;
    }

    private void SyncRecentFoldersFromSettings()
    {
        RecentFolders.Clear();
        foreach (string recent in _settingsService.Current.RecentFolders)
            RecentFolders.Add(recent);
    }

    // ─── Updates ───────────────────────────────────────────────────────────────

    private void ReportLastUpdateResult()
    {
        if (_updateService.TakeLastInstallResult() is not { } result)
            return;

        if (result.Succeeded)
        {
            StatusMessage = $"Updated to Orbit {result.Version}.";
        }
        else
        {
            _dialogService.ShowError(
                "Update Failed",
                $"Orbit {result.Version} could not be copied into this folder, so the previous version is still running.\n\nDetails: {UpdateService.InstallLogPath}");
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_settingsService.Current.CheckForUpdatesOnStartup)
            return;

        // Let the first folder load finish before going to the network.
        await Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            AvailableUpdate = await _updateService.CheckForUpdateAsync();
            if (AvailableUpdate is not null)
                StatusMessage = $"Orbit {UpdateService.Format(AvailableUpdate.Version)} is available.";
        }
        catch (Exception ex)
        {
            // Offline or GitHub unreachable: stay quiet on startup.
            _logger.Warn("Update", "Startup update check failed.", new { ex.Message });
        }
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        StatusMessage = "Checking for updates...";
        try
        {
            AvailableUpdate = await _updateService.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Update check failed.";
            _dialogService.ShowError(
                "Check for Updates",
                $"Could not reach GitHub to check for updates.\n\n{ex.Message}\n\nNew versions can also be downloaded from {UpdateService.ReleasesPageUrl}");
            return;
        }

        // A manual check always answers with a window; a status-bar line alone is easy to miss.
        if (AvailableUpdate is null)
        {
            StatusMessage = $"{AppVersionText} is up to date.";
            _dialogService.ShowMessage("Check for Updates", $"You have the latest version ({AppVersionText}).");
        }
        else
        {
            InstallUpdate();
        }
    }

    [RelayCommand]
    public void InstallUpdate()
    {
        if (AvailableUpdate is null)
            return;

        if (_dialogService.ShowUpdateDialog(_updateService, AvailableUpdate))
            RequestCloseForUpdate?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancellationHelper.CancelAndClear(ref _previewCts);
        CancellationHelper.CancelAndClear(ref _operationCts);
        CancellationHelper.CancelAndClear(ref _searchCts);
        CancellationHelper.CancelAndClear(ref _thumbnailsCts);

        LeftPane.Dispose();
        RightPane.Dispose();
        _operationQueue.Dispose();
    }
}
