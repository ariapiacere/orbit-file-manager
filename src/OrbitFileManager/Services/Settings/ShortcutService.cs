using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace OrbitFileManager.Services.Settings;

public enum OrbitShortcutAction
{
    NewTab,
    CloseTab,
    RestoreClosedTab,
    ToggleDualPane,
    SwitchActivePane,
    BatchRename,
    AdvancedSearch,
    QuickFilter,
    ToggleHiddenFiles,
    TogglePreviewPanel,
    ToggleSidebar,
    DuplicateFinder,
    FolderSizeAnalyzer,
    CalculateChecksums
}

public sealed record ShortcutBindingDto
{
    public required string Action { get; init; }
    public required string Gesture { get; init; }
}

public sealed class ShortcutBindingEntry
{
    public required OrbitShortcutAction Action { get; init; }
    public required string DisplayName { get; init; }
    public required ModifierKeys Modifiers { get; set; }
    public required Key Key { get; set; }
    public string FormattedGesture => ShortcutService.FormatGesture(Modifiers, Key);
}

public sealed class ShortcutService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly AppPathsService _pathsService;
    private readonly StructuredLogger _logger;
    private readonly Dictionary<OrbitShortcutAction, ShortcutBindingEntry> _bindings = [];

    public string ShortcutsFilePath => Path.Combine(_pathsService.DataDirectory, "shortcuts.json");

    public ShortcutService(AppPathsService pathsService, StructuredLogger logger)
    {
        _pathsService = pathsService;
        _logger = logger;
        ResetToDefaultsInMemory();
        Load();
    }

    public IReadOnlyList<ShortcutBindingEntry> GetAllBindings() =>
        _bindings.Values.OrderBy(b => (int)b.Action).ToList();

    public string GetGestureText(OrbitShortcutAction action) =>
        _bindings.TryGetValue(action, out var entry) ? entry.FormattedGesture : string.Empty;

    public OrbitShortcutAction? MatchShortcut(ModifierKeys modifiers, Key key)
    {
        foreach (var entry in _bindings.Values)
        {
            if (entry.Modifiers == modifiers && entry.Key == key)
                return entry.Action;
        }
        return null;
    }

    public (bool Success, string? ErrorMessage) TryUpdateBinding(OrbitShortcutAction action, ModifierKeys modifiers, Key key)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return (false, "Press a non-modifier key to complete the shortcut.");

        foreach (var existing in _bindings.Values)
        {
            if (existing.Action != action && existing.Modifiers == modifiers && existing.Key == key)
            {
                return (false, $"'{FormatGesture(modifiers, key)}' is already assigned to '{existing.DisplayName}'.");
            }
        }

        if (_bindings.TryGetValue(action, out var target))
        {
            target.Modifiers = modifiers;
            target.Key = key;
            Save();
            return (true, null);
        }

        return (false, "Unknown shortcut action.");
    }

    public void ResetToDefaults()
    {
        ResetToDefaultsInMemory();
        Save();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(ShortcutsFilePath))
            {
                Save();
                return;
            }

            string json = File.ReadAllText(ShortcutsFilePath);
            var list = JsonSerializer.Deserialize<List<ShortcutBindingDto>>(json, JsonOptions);
            if (list is null)
                return;

            foreach (var dto in list)
            {
                if (Enum.TryParse<OrbitShortcutAction>(dto.Action, ignoreCase: true, out var act) &&
                    TryParseGesture(dto.Gesture, out var mods, out var key) &&
                    _bindings.TryGetValue(act, out var entry))
                {
                    entry.Modifiers = mods;
                    entry.Key = key;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("ShortcutService", $"Failed to load shortcuts.json; keeping defaults. {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var dtos = _bindings.Values
                .OrderBy(b => (int)b.Action)
                .Select(b => new ShortcutBindingDto
                {
                    Action = b.Action.ToString(),
                    Gesture = b.FormattedGesture
                })
                .ToList();

            string tempPath = ShortcutsFilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(dtos, JsonOptions));
            File.Move(tempPath, ShortcutsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error("ShortcutService", "Failed to save shortcuts.json.", ex);
        }
    }

    public static string FormatGesture(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParseGesture(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                default:
                    return false;
            }
        }

        return Enum.TryParse(parts[^1], ignoreCase: true, out key) && key != Key.None;
    }

    private void ResetToDefaultsInMemory()
    {
        _bindings.Clear();
        RegisterDefault(OrbitShortcutAction.NewTab, "New Folder Tab", ModifierKeys.Control, Key.T);
        RegisterDefault(OrbitShortcutAction.CloseTab, "Close Active Tab", ModifierKeys.Control, Key.W);
        RegisterDefault(OrbitShortcutAction.RestoreClosedTab, "Reopen Closed Tab", ModifierKeys.Control | ModifierKeys.Shift, Key.T);
        RegisterDefault(OrbitShortcutAction.ToggleDualPane, "Toggle Dual-Pane Layout", ModifierKeys.Control | ModifierKeys.Shift, Key.D);
        RegisterDefault(OrbitShortcutAction.SwitchActivePane, "Switch Active Pane", ModifierKeys.None, Key.F6);
        RegisterDefault(OrbitShortcutAction.BatchRename, "Open Batch Rename Tool", ModifierKeys.Control, Key.M);
        RegisterDefault(OrbitShortcutAction.AdvancedSearch, "Open Advanced Search", ModifierKeys.Control | ModifierKeys.Shift, Key.F);
        RegisterDefault(OrbitShortcutAction.QuickFilter, "Focus Quick Filter Box", ModifierKeys.Control, Key.F);
        RegisterDefault(OrbitShortcutAction.ToggleHiddenFiles, "Toggle Hidden Files", ModifierKeys.Control, Key.H);
        RegisterDefault(OrbitShortcutAction.TogglePreviewPanel, "Toggle Preview Panel", ModifierKeys.Control, Key.P);
        RegisterDefault(OrbitShortcutAction.ToggleSidebar, "Toggle Navigation Sidebar", ModifierKeys.Control, Key.B);
        RegisterDefault(OrbitShortcutAction.DuplicateFinder, "Duplicate File Finder", ModifierKeys.Control | ModifierKeys.Shift, Key.U);
        RegisterDefault(OrbitShortcutAction.FolderSizeAnalyzer, "Folder Size Analyzer", ModifierKeys.Control | ModifierKeys.Shift, Key.Z);
        RegisterDefault(OrbitShortcutAction.CalculateChecksums, "File Checksum Calculator", ModifierKeys.Control | ModifierKeys.Shift, Key.K);
    }

    private void RegisterDefault(OrbitShortcutAction action, string displayName, ModifierKeys mods, Key key)
    {
        _bindings[action] = new ShortcutBindingEntry
        {
            Action = action,
            DisplayName = displayName,
            Modifiers = mods,
            Key = key
        };
    }
}
