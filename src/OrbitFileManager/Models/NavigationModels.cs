using System.IO;
using System.Windows.Media;

namespace OrbitFileManager.Models;

public sealed class DriveEntryItem
{
    public required string Name { get; init; }
    public required string RootPath { get; init; }
    public required string DisplayLabel { get; init; }
    public required DriveType DriveType { get; init; }
    public string DriveFormat { get; init; } = string.Empty;
    public bool IsReady { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsageRatio => TotalBytes > 0 ? Math.Clamp((double)UsedBytes / TotalBytes, 0.0, 1.0) : 0.0;
    public bool IsNearlyFull => UsageRatio >= 0.9;
    public required string CapacitySummary { get; init; }
    public ImageSource? Icon { get; init; }
}

public sealed class FavoriteItem
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public string Category { get; set; } = "Favorite";
}

public sealed class BreadcrumbSegment
{
    public required string Label { get; init; }
    public required string FullPath { get; init; }
    public bool IsLast { get; init; }
}
