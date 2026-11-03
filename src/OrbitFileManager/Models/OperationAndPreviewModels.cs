using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbitFileManager.Models;

public enum ConflictResolution
{
    KeepBoth = 0,
    Overwrite = 1,
    Skip = 2,
    Cancel = 3
}

public sealed record ConflictDecision(ConflictResolution Resolution, bool ApplyToAll);

public sealed record FileConflictContext(
    string SourcePath,
    string DestinationPath,
    bool IsDirectory,
    long? SourceSize,
    DateTime SourceModified,
    long? DestinationSize,
    DateTime DestinationModified);

public sealed class FileOperationResult
{
    public int SucceededCount { get; init; }
    public int SkippedCount { get; init; }
    public bool IsCancelled { get; init; }
    public IReadOnlyList<string> CreatedOrUpdatedPaths { get; init; } = [];
    /// <summary>Paths (as passed in) that a delete actually removed or recycled.</summary>
    public IReadOnlyList<string> RemovedPaths { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
    public bool IsFullSuccess => !IsCancelled && Errors.Count == 0;
}

public enum PreviewKind
{
    None = 0,
    Directory = 1,
    Image = 2,
    Text = 3,
    Binary = 4,
    Error = 5
}

public sealed class FilePreviewData
{
    public PreviewKind Kind { get; init; } = PreviewKind.None;
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string TypeDescription { get; init; } = string.Empty;
    public string SizeSummary { get; init; } = string.Empty;
    public string ExactBytesSummary { get; init; } = string.Empty;
    public string CreatedText { get; init; } = string.Empty;
    public string ModifiedText { get; init; } = string.Empty;
    public string AccessedText { get; init; } = string.Empty;
    public string AttributesText { get; init; } = string.Empty;
    public string? LinkTarget { get; init; }
    public ImageSource? Icon { get; init; }

    // Directory details
    public string? DirectorySummary { get; init; }

    // Image details
    public BitmapSource? ImagePreview { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public string DimensionsText => PixelWidth > 0 && PixelHeight > 0 ? $"{PixelWidth} × {PixelHeight} px" : string.Empty;

    // Text details
    public string? TextPreview { get; init; }
    public bool IsTextTruncated { get; init; }
    public string? StatusMessage { get; init; }
}
