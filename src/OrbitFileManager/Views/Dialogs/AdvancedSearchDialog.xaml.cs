using OrbitFileManager.Services;
using OrbitFileManager.Services.Search;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace OrbitFileManager.Views.Dialogs;

public partial class AdvancedSearchDialog : Window
{
    private readonly FileSearchService _searchService;
    private readonly List<SavedSearchPreset> _presets;

    public SearchRequest? BuiltRequest { get; private set; }

    public AdvancedSearchDialog(
        FileSearchService searchService,
        IReadOnlyList<string> defaultLocations,
        bool defaultHidden,
        bool useDarkTitleBar)
    {
        InitializeComponent();
        _searchService = searchService;
        _presets = _searchService.LoadPresets();
        PresetsCombo.ItemsSource = _presets;

        LocationsTextBox.Text = string.Join("; ", defaultLocations.Where(l => !string.IsNullOrWhiteSpace(l)));
        HiddenFilesCheck.IsChecked = defaultHidden;

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
        Loaded += (_, _) => QueryTextBox.Focus();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string sample })
        {
            QueryTextBox.Text = sample;
            QueryTextBox.CaretIndex = sample.Length;
            QueryTextBox.Focus();
        }
    }

    private void PresetsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetsCombo.SelectedItem is SavedSearchPreset p)
        {
            QueryTextBox.Text = p.QueryText;
            RecursiveCheck.IsChecked = p.Recursive;
            CaseSensitiveCheck.IsChecked = p.CaseSensitive;
            HiddenFilesCheck.IsChecked = p.IncludeHidden;
        }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        string q = QueryTextBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
            return;

        var (ast, err) = SearchQueryParser.TryParse(q);
        if (ast is null)
        {
            ValidationText.Text = err;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        string name = q.Length > 32 ? q[..32] + "…" : q;
        _presets.RemoveAll(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
        _presets.Insert(0, new SavedSearchPreset
        {
            Name = name,
            QueryText = q,
            Recursive = RecursiveCheck.IsChecked == true,
            CaseSensitive = CaseSensitiveCheck.IsChecked == true,
            IncludeHidden = HiddenFilesCheck.IsChecked == true
        });

        _searchService.SavePresets(_presets);
        PresetsCombo.ItemsSource = null;
        PresetsCombo.ItemsSource = _presets;
        PresetsCombo.SelectedIndex = 0;
    }

    private void QueryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        var (ast, err) = SearchQueryParser.TryParse(QueryTextBox.Text);
        if (ast is null)
        {
            ValidationText.Text = err;
            ValidationText.Visibility = Visibility.Visible;
            StartSearchBtn.IsEnabled = false;
        }
        else
        {
            ValidationText.Visibility = Visibility.Collapsed;
            StartSearchBtn.IsEnabled = true;
        }
    }

    private void StartSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        var (ast, err) = SearchQueryParser.TryParse(QueryTextBox.Text);
        if (ast is null)
        {
            ValidationText.Text = err;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        var roots = LocationsTextBox.Text
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists)
            .ToList();

        if (roots.Count == 0)
        {
            ValidationText.Text = "Specify at least one existing folder path to search.";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        BuiltRequest = new SearchRequest
        {
            QueryText = QueryTextBox.Text.Trim(),
            RootLocations = roots,
            Recursive = RecursiveCheck.IsChecked == true,
            CaseSensitive = CaseSensitiveCheck.IsChecked == true,
            IncludeHidden = HiddenFilesCheck.IsChecked == true
        };

        DialogResult = true;
    }
}
