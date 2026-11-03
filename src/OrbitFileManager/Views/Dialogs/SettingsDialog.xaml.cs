using OrbitFileManager.Models;
using OrbitFileManager.Services;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Services.Updates;
using OrbitFileManager.Utilities;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OrbitFileManager.Views.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly ShortcutService? _shortcutService;
    private readonly string _storagePath;

    public SettingsDialog(
        AppSettings settings,
        ShortcutService? shortcutService,
        string storagePath,
        bool isPortable,
        bool useDarkTitleBar)
    {
        InitializeComponent();
        _settings = settings;
        _shortcutService = shortcutService;
        _storagePath = storagePath;

        DarkThemeRadio.IsChecked = settings.Theme == AppThemeMode.Dark;
        LightThemeRadio.IsChecked = settings.Theme == AppThemeMode.Light;
        ShowHiddenCheck.IsChecked = settings.ShowHiddenFiles;
        ShowSystemCheck.IsChecked = settings.ShowSystemFiles;
        ConfirmRecycleCheck.IsChecked = settings.ConfirmRecycleBinDelete;
        RestoreTabsCheck.IsChecked = settings.RestoreTabsOnStartup;
        CheckUpdatesCheck.IsChecked = settings.CheckForUpdatesOnStartup;
        VersionText.Text = $"Installed version {UpdateService.Format(UpdateService.CurrentVersion)}";

        ToolbarLabelsCheck.IsChecked = settings.ShowToolbarTextLabels;
        ToolbarBatchRenameCheck.IsChecked = settings.ShowToolbarBatchRename;
        ToolbarSearchCheck.IsChecked = settings.ShowToolbarSearch;
        ToolbarDualPaneCheck.IsChecked = settings.ShowToolbarDualPane;
        ToolbarToolsMenuCheck.IsChecked = settings.ShowToolbarToolsMenu;

        DefaultPathBox.Text = settings.DefaultStartPath;

        StorageModeHeader.Text = isPortable
            ? "Portable Storage Active (Data / Logs / Cache)"
            : "Read-Only Install Folder — Using Local AppData Fallback";
        StoragePathText.Text = storagePath;
        StoragePathText.ToolTip = storagePath;

        RefreshShortcutsList();
        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
    }

    private void RefreshShortcutsList()
    {
        if (_shortcutService is null)
            return;

        int previousIdx = ShortcutsListView.SelectedIndex;
        ShortcutsListView.ItemsSource = _shortcutService.GetAllBindings();
        if (previousIdx >= 0 && previousIdx < ShortcutsListView.Items.Count)
            ShortcutsListView.SelectedIndex = previousIdx;
    }

    private void ShortcutsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShortcutsListView.SelectedItem is ShortcutBindingEntry entry)
        {
            ShortcutCaptureBox.Text = $"Selected '{entry.DisplayName}' ({entry.FormattedGesture}) — click here and press new keys";
        }
    }

    private void ShortcutCaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shortcutService is null || ShortcutsListView.SelectedItem is not ShortcutBindingEntry selected)
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Escape)
            return;

        e.Handled = true;
        ModifierKeys mods = Keyboard.Modifiers;

        var (ok, err) = _shortcutService.TryUpdateBinding(selected.Action, mods, key);
        if (!ok)
        {
            ShortcutStatusText.Text = err ?? "Invalid key combination.";
            return;
        }

        string formatted = ShortcutService.FormatGesture(mods, key);
        ShortcutCaptureBox.Text = $"{selected.DisplayName} → {formatted}";
        ShortcutStatusText.Text = $"Updated '{selected.DisplayName}' to {formatted}.";
        RefreshShortcutsList();
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcutService is null)
            return;

        _shortcutService.ResetToDefaults();
        RefreshShortcutsList();
        ShortcutCaptureBox.Text = "All shortcuts reset to defaults.";
        ShortcutStatusText.Text = "Default keyboard bindings restored.";
    }

    private void OpenStorageFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(_storagePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{_storagePath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string normalizedPath = PathValidator.NormalizePath(DefaultPathBox.Text);
        if (string.IsNullOrWhiteSpace(normalizedPath) || !Directory.Exists(normalizedPath))
        {
            PathErrorText.Text = "Default start folder must be an existing directory.";
            PathErrorText.Visibility = Visibility.Visible;
            return;
        }

        _settings.Theme = LightThemeRadio.IsChecked == true ? AppThemeMode.Light : AppThemeMode.Dark;
        _settings.ShowHiddenFiles = ShowHiddenCheck.IsChecked == true;
        _settings.ShowSystemFiles = ShowSystemCheck.IsChecked == true;
        _settings.ConfirmRecycleBinDelete = ConfirmRecycleCheck.IsChecked == true;
        _settings.RestoreTabsOnStartup = RestoreTabsCheck.IsChecked == true;
        _settings.CheckForUpdatesOnStartup = CheckUpdatesCheck.IsChecked == true;

        _settings.ShowToolbarTextLabels = ToolbarLabelsCheck.IsChecked == true;
        _settings.ShowToolbarBatchRename = ToolbarBatchRenameCheck.IsChecked == true;
        _settings.ShowToolbarSearch = ToolbarSearchCheck.IsChecked == true;
        _settings.ShowToolbarDualPane = ToolbarDualPaneCheck.IsChecked == true;
        _settings.ShowToolbarToolsMenu = ToolbarToolsMenuCheck.IsChecked == true;

        _settings.DefaultStartPath = normalizedPath;

        DialogResult = true;
    }
}
