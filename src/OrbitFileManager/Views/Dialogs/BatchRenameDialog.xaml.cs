using OrbitFileManager.Services;
using OrbitFileManager.Services.FileOperations;
using System.Windows;
using System.Windows.Controls;

namespace OrbitFileManager.Views.Dialogs;

public partial class BatchRenameDialog : Window
{
    private readonly BatchRenameService _renameService;
    private readonly IReadOnlyList<string> _sourcePaths;
    private BatchRenamePlan? _currentPlan;

    public int RenamedCount { get; private set; }

    public BatchRenameDialog(
        BatchRenameService renameService,
        IReadOnlyList<string> sourcePaths,
        bool useDarkTitleBar)
    {
        InitializeComponent();
        _renameService = renameService;
        _sourcePaths = sourcePaths;

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
        Loaded += (_, _) => RebuildPreview();
    }

    private void RuleChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            RebuildPreview();
    }

    private void RuleToggleChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            RebuildPreview();
    }

    private void CaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            RebuildPreview();
    }

    private void RebuildPreview()
    {
        int.TryParse(NumStartBox.Text, out int start);
        if (!int.TryParse(NumStepBox.Text, out int step) || step == 0) step = 1;
        if (!int.TryParse(NumDigitsBox.Text, out int digits)) digits = 3;

        var opts = new BatchRenameOptions
        {
            BaseNameOverride = BaseNameBox.Text,
            Prefix = PrefixBox.Text,
            Suffix = SuffixBox.Text,
            FindText = FindBox.Text,
            ReplaceText = ReplaceBox.Text,
            UseRegex = UseRegexCheck.IsChecked == true,
            CaseSensitiveFind = CaseSensitiveCheck.IsChecked == true,
            RemoveCharacters = RemoveCharsBox.Text,
            Capitalization = (BatchCaseMode)Math.Clamp(CaseCombo.SelectedIndex, 0, 4),
            EnableNumbering = NumberingCheck.IsChecked == true,
            NumberStart = start,
            NumberStep = step,
            NumberDigits = Math.Clamp(digits, 1, 10),
            NumberSeparator = NumSepBox.Text,
            PreserveExtension = PreserveExtCheck.IsChecked == true
        };

        _currentPlan = _renameService.BuildPlan(_sourcePaths, opts);
        PreviewListView.ItemsSource = _currentPlan.Items;

        if (!string.IsNullOrEmpty(_currentPlan.GlobalValidationError))
        {
            ErrorBannerText.Text = _currentPlan.GlobalValidationError;
            PlanSummaryText.Text = "Invalid pattern";
        }
        else if (_currentPlan.ErrorCount > 0)
        {
            ErrorBannerText.Text = $"{_currentPlan.ErrorCount} conflict(s) or invalid filename(s) detected.";
            PlanSummaryText.Text = $"{_currentPlan.ChangedCount} changed • {_currentPlan.ErrorCount} errors";
        }
        else
        {
            ErrorBannerText.Text = string.Empty;
            PlanSummaryText.Text = $"{_currentPlan.ChangedCount} of {_sourcePaths.Count} item(s) will be renamed";
        }

        ApplyBtn.IsEnabled = _currentPlan.CanApply;
    }

    private async void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan is null || !_currentPlan.CanApply)
            return;

        ApplyBtn.IsEnabled = false;
        var (ok, count, err) = await _renameService.ExecutePlanAsync(_currentPlan);
        if (!ok)
        {
            ErrorBannerText.Text = err ?? "Batch rename failed.";
            ApplyBtn.IsEnabled = true;
            return;
        }

        RenamedCount = count;
        DialogResult = true;
    }
}
