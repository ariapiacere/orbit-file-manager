namespace OrbitFileManager.Models;

public enum AppThemeMode
{
    Dark = 0,
    Light = 1
}

/// <summary>Accent colour preset; see <c>Services/AccentPalettes.cs</c>. Values are stored by name, so never rename one.</summary>
public enum AppAccent
{
    Orange = 0,
    Blue = 1,
    Teal = 2,
    Violet = 3,
    Green = 4,
    Mono = 5
}

public enum BrowserViewMode
{
    Details = 0,
    List = 1,
    LargeIcons = 2,
    SmallIcons = 3,
    Thumbnails = 4
}

public enum SortColumn
{
    Name = 0,
    Extension = 1,
    Size = 2,
    Type = 3,
    Modified = 4,
    Created = 5
}

public sealed class AppSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.Dark;
    public AppAccent Accent { get; set; } = AppAccent.Orange;
    public BrowserViewMode DefaultViewMode { get; set; } = BrowserViewMode.Details;
    public bool ShowHiddenFiles { get; set; } = false;
    public bool ShowSystemFiles { get; set; } = false;
    public bool ShowPreviewPanel { get; set; } = true;
    public bool ShowSidebar { get; set; } = true;
    public bool MiniTreeMode { get; set; } = false;
    public bool ConfirmRecycleBinDelete { get; set; } = true;
    public bool RestoreTabsOnStartup { get; set; } = true;
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    // Remembered Details column visibility
    public bool ShowExtensionColumn { get; set; } = true;
    public bool ShowSizeColumn { get; set; } = true;
    public bool ShowTypeColumn { get; set; } = true;
    public bool ShowModifiedColumn { get; set; } = true;
    public bool ShowCreatedColumn { get; set; } = true;
    public bool ShowAttributesColumn { get; set; } = true;

    // Customizable Toolbar Options
    public bool ShowToolbarTextLabels { get; set; } = true;
    public bool ShowToolbarBatchRename { get; set; } = true;
    public bool ShowToolbarSearch { get; set; } = true;
    public bool ShowToolbarDualPane { get; set; } = true;
    public bool ShowToolbarToolsMenu { get; set; } = true;

    public double WindowWidth { get; set; } = 1420;
    public double WindowHeight { get; set; } = 880;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool IsWindowMaximized { get; set; } = false;
    public double SidebarWidth { get; set; } = 260;
    public double PreviewPanelWidth { get; set; } = 310;

    public string DefaultStartPath { get; set; } = string.Empty;
    public List<string> LastOpenTabs { get; set; } = [];
    public int ActiveTabIndex { get; set; } = 0;
    public List<string> RecentFolders { get; set; } = [];
}
