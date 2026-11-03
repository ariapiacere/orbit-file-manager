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
    public string ActivePaneId { get; set; } = "Left";
    public bool IsSyncNavigationEnabled { get; set; }
    public PaneSessionState LeftPane { get; set; } = new();
    public PaneSessionState RightPane { get; set; } = new();
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class WorkspacePreset
{
    public required string Name { get; set; }
    public required AppSessionState Session { get; set; }
}
