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
    public long? SizeBytes { get; init; }
    public required string FormattedSize { get; init; }
    public required string TypeDescription { get; init; }
    public DateTime CreatedTime { get; init; }
    public DateTime ModifiedTime { get; init; }
    public required string AttributesSummary { get; init; }

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
