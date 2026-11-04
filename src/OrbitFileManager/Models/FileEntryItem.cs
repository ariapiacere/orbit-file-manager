using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace OrbitFileManager.Models;

public partial class FileEntryItem : ObservableObject
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public string Extension { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public bool IsHidden { get; init; }
    public bool IsSystem { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsSymbolicLink { get; init; }
    public string? LinkTarget { get; init; }
    public required string TypeDescription { get; init; }
    public DateTime CreatedTime { get; init; }
    public required string AttributesSummary { get; init; }

    // Size and modified time change in place while a file is still being written (see BrowserTabViewModel's watcher).
    private long? _sizeBytes;
    public long? SizeBytes
    {
        get => _sizeBytes;
        set => SetProperty(ref _sizeBytes, value);
    }

    private string _formattedSize = string.Empty;
    public required string FormattedSize
    {
        get => _formattedSize;
        set => SetProperty(ref _formattedSize, value);
    }

    private DateTime _modifiedTime;
    public DateTime ModifiedTime
    {
        get => _modifiedTime;
        set
        {
            if (SetProperty(ref _modifiedTime, value))
                OnPropertyChanged(nameof(ModifiedText));
        }
    }

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isCutPending;

    [ObservableProperty]
    private bool _isComparisonDifferent;

    [ObservableProperty]
    private string? _comparisonBadge;

    [ObservableProperty]
    private string? _colorTagHex;

    [ObservableProperty]
    private string? _note;

    public ImageSource? BestVisual => Thumbnail ?? Icon;

    partial void OnIconChanged(ImageSource? value) => OnPropertyChanged(nameof(BestVisual));
    partial void OnThumbnailChanged(ImageSource? value) => OnPropertyChanged(nameof(BestVisual));

    public string GroupCategory => IsDirectory ? "Folders" : TypeDescription;

    public string DisplayExtension => IsDirectory
        ? string.Empty
        : (string.IsNullOrEmpty(Extension) ? "—" : Extension.TrimStart('.').ToUpperInvariant());

    public string ModifiedText => ModifiedTime == DateTime.MinValue
        ? "—"
        : ModifiedTime.ToString("yyyy-MM-dd HH:mm");

    public string CreatedText => CreatedTime == DateTime.MinValue
        ? "—"
        : CreatedTime.ToString("yyyy-MM-dd HH:mm");
}
