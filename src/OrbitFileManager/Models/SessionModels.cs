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

    /// <summary>Item that was selected, reselected after the folder loads. Null in sessions saved before 1.2.0.</summary>
    public string? SelectedPath { get; set; }

    /// <summary>Back history, oldest first (the last entry is what Back opens). Null in sessions saved before 1.2.0.</summary>
    public List<string>? BackHistory { get; set; }

    /// <summary>Forward history, farthest first (the last entry is what Forward opens).</summary>
    public List<string>? ForwardHistory { get; set; }
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

    /// <summary>
    /// Repairs what a hand-edited or partly written file can leave behind (null panes, null or blank tabs,
    /// out-of-range indexes) so restoring never throws. Returns the same instance.
    /// </summary>
    public AppSessionState Sanitize()
    {
        LeftPane = SanitizePane(LeftPane);
        RightPane = SanitizePane(RightPane);
        ActivePaneId = string.Equals(ActivePaneId, "Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Left";
        if (!Enum.IsDefined(SplitOrientation))
            SplitOrientation = SplitOrientationMode.Vertical;
        return this;
    }

    private static PaneSessionState SanitizePane(PaneSessionState? pane)
    {
        pane ??= new PaneSessionState();
        pane.Tabs = (pane.Tabs ?? [])
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Path))
            .ToList();

        foreach (TabSessionState tab in pane.Tabs)
        {
            if (!Enum.IsDefined(tab.ViewMode))
                tab.ViewMode = BrowserViewMode.Details;
            if (!Enum.IsDefined(tab.SortColumn))
                tab.SortColumn = SortColumn.Name;
            tab.BackHistory = tab.BackHistory?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            tab.ForwardHistory = tab.ForwardHistory?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        }

        pane.ActiveTabIndex = pane.Tabs.Count == 0 ? 0 : Math.Clamp(pane.ActiveTabIndex, 0, pane.Tabs.Count - 1);
        return pane;
    }
}

public sealed class WorkspacePreset
{
    public required string Name { get; set; }
    public required AppSessionState Session { get; set; }
}
