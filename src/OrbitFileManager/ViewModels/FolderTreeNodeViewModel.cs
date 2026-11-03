using CommunityToolkit.Mvvm.ComponentModel;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Settings;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace OrbitFileManager.ViewModels;

public partial class FolderTreeNodeViewModel : ObservableObject
{
    private readonly FileBrowserService? _browserService;
    private readonly ShellIntegrationService? _shellService;
    private readonly SettingsService? _settingsService;
    private readonly bool _isDummyPlaceholder;
    private bool _childrenLoaded;

    public string Name { get; }
    public string FullPath { get; }
    public ImageSource? Icon { get; }
    public bool IsDriveRoot { get; }

    public ObservableCollection<FolderTreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    private FolderTreeNodeViewModel()
    {
        _isDummyPlaceholder = true;
        Name = "Loading...";
        FullPath = string.Empty;
    }

    public FolderTreeNodeViewModel(
        string name,
        string fullPath,
        ImageSource? icon,
        bool isDriveRoot,
        bool mayHaveChildren,
        FileBrowserService browserService,
        ShellIntegrationService shellService,
        SettingsService settingsService)
    {
        Name = name;
        FullPath = fullPath;
        Icon = icon;
        IsDriveRoot = isDriveRoot;
        _browserService = browserService;
        _shellService = shellService;
        _settingsService = settingsService;

        if (mayHaveChildren)
        {
            Children.Add(new FolderTreeNodeViewModel());
        }
        else
        {
            _childrenLoaded = true;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded && !_isDummyPlaceholder)
        {
            _ = LoadChildrenAsync();
        }
    }

    public async Task LoadChildrenAsync()
    {
        if (_childrenLoaded || _isDummyPlaceholder || _browserService is null || _shellService is null || _settingsService is null)
            return;

        _childrenLoaded = true;
        bool includeHidden = _settingsService.Current.ShowHiddenFiles;
        bool includeSystem = _settingsService.Current.ShowSystemFiles;

        var subDirs = await _browserService.GetSubdirectoriesAsync(
            FullPath,
            includeHidden,
            includeSystem,
            CancellationToken.None);

        var viewModels = new List<FolderTreeNodeViewModel>(subDirs.Count);
        var (folderIcon, _) = _shellService.GetEntryMetadata("Directory", string.Empty, isDirectory: true, largeIcon: false);

        foreach (DirectoryInfo sub in subDirs)
        {
            bool hasGrandChildren = _browserService.HasAccessibleSubdirectories(sub.FullName, includeHidden, includeSystem);
            viewModels.Add(new FolderTreeNodeViewModel(
                sub.Name,
                sub.FullName,
                folderIcon,
                isDriveRoot: false,
                mayHaveChildren: hasGrandChildren,
                _browserService,
                _shellService,
                _settingsService));
        }

        void UpdateCollection()
        {
            Children.Clear();
            foreach (var vm in viewModels)
                Children.Add(vm);
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            await dispatcher.InvokeAsync(UpdateCollection);
        else
            UpdateCollection();
    }
}
