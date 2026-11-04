namespace OrbitFileManager.Models;

public enum SplitOrientationMode
{
    Vertical = 0,   // Left | Right
    Horizontal = 1  // Top / Bottom
}

public sealed class TabSessionState
{
    public string Path { get; set; } = string.Empty;
    public string? CustomLabel { get; set; }
    public bool IsPinned { get; set; }
    public bool IsLocked { get; set; }
    public string? LockedRootPath { get; set; }
    public BrowserViewMode ViewMode { get; set; } = BrowserViewMode.Details;
    public SortColumn SortColumn { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }
    public bool GroupByType { get; set; }
}

public sealed class PaneSessionState
{
    public List<TabSessionState> Tabs { get; set; } = [];
    public int ActiveTabIndex { get; set; }
}

public sealed class AppSessionState
{
    public bool IsDualPane { get; set; }
    public SplitOrientationMode SplitOrientation { get; set; } = SplitOrientationMode.Vertical;
    public double VerticalSplitRatio { get; set; } = 0.5;   // left pane's share of a Left | Right split
    public double HorizontalSplitRatio { get; set; } = 0.5; // top pane's share of a Top / Bottom split
    public string ActivePaneId { get; set; } = "Left";
    public bool IsSyncNavigationEnabled { get; set; }
    public PaneSessionState LeftPane { get; set; } = new();
    public PaneSessionState RightPane { get; set; } = new();
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Keeps a saved split usable: older sessions (no value) and corrupt values fall back to an even split.</summary>
    public static double ClampSplitRatio(double ratio) =>
        double.IsFinite(ratio) && ratio > 0 && ratio < 1 ? Math.Clamp(ratio, 0.1, 0.9) : 0.5;
}

public sealed class WorkspacePreset
{
    public required string Name { get; set; }
    public required AppSessionState Session { get; set; }
}
