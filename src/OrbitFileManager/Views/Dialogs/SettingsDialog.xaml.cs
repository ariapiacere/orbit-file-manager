using OrbitFileManager.Models;
using OrbitFileManager.Services;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Services.Updates;
using OrbitFileManager.Utilities;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OrbitFileManager.Views.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly ShortcutService? _shortcutService;
    private readonly string _storagePath;
    private readonly Action<AppThemeMode, AppAccent>? _previewTheme;
    private AppAccent _selectedAccent;
    private bool _initializing = true;

    /// <param name="previewTheme">Applies a theme and accent to the whole app while the user tries them out.</param>
    public SettingsDialog(
        AppSettings settings,
        ShortcutService? shortcutService,
        string storagePath,
        bool isPortable,
        bool useDarkTitleBar,
        Action<AppThemeMode, AppAccent>? previewTheme = null)
    {
        InitializeComponent();
        _settings = settings;
        _shortcutService = shortcutService;
        _storagePath = storagePath;
        _previewTheme = previewTheme;

        DarkThemeRadio.IsChecked = settings.Theme == AppThemeMode.Dark;
        LightThemeRadio.IsChecked = settings.Theme == AppThemeMode.Light;
        BuildAccentSwatches(settings.Accent);
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
        _initializing = false;
    }

    private AppThemeMode SelectedMode => LightThemeRadio.IsChecked == true ? AppThemeMode.Light : AppThemeMode.Dark;

    private void BuildAccentSwatches(AppAccent current)
    {
        var style = (Style)FindResource("AccentSwatchStyle");
        foreach (AccentPalette palette in AccentPalettes.All)
        {
            var swatch = new RadioButton { Style = style, Tag = palette, ToolTip = palette.Name };
            AutomationProperties.SetName(swatch, $"{palette.Name} accent");
            swatch.Checked += AccentSwatch_Checked;
            AccentSwatchPanel.Children.Add(swatch);
        }

        UpdateSwatchColors();
        foreach (RadioButton swatch in AccentSwatchPanel.Children)
            swatch.IsChecked = ((AccentPalette)swatch.Tag).Accent == current;
    }

    // Each dot shows the accent as it looks in the selected mode.
    private void UpdateSwatchColors()
    {
        bool dark = SelectedMode == AppThemeMode.Dark;
        foreach (RadioButton swatch in AccentSwatchPanel.Children)
        {
            var brush = new SolidColorBrush(AccentPalettes.AccentFor(((AccentPalette)swatch.Tag).Accent, dark));
            brush.Freeze();
            swatch.Background = brush;
        }
    }

    private void AccentSwatch_Checked(object sender, RoutedEventArgs e)
    {
        var palette = (AccentPalette)((RadioButton)sender).Tag;
        _selectedAccent = palette.Accent;
        AccentNameText.Text = palette.Name;
        PreviewTheme();
    }

    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (AccentSwatchPanel is null)
            return; // still building the dialog

        UpdateSwatchColors();
        PreviewTheme();
    }

    private void PreviewTheme()
    {
        if (!_initializing)
            _previewTheme?.Invoke(SelectedMode, _selectedAccent);
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

        _settings.Theme = SelectedMode;
        _settings.Accent = _selectedAccent;
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
