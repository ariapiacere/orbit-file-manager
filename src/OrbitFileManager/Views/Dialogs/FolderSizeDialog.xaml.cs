using OrbitFileManager.Services;
using OrbitFileManager.Services.Productivity;
using OrbitFileManager.Utilities;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace OrbitFileManager.Views.Dialogs;

public partial class FolderSizeDialog : Window
{
    private readonly FolderSizeAnalyzerService _analyzerService;
    private CancellationTokenSource? _cts;
    private string _currentAnalyzedPath;

    public string? RequestedTabPath { get; private set; }

    public FolderSizeDialog(FolderSizeAnalyzerService analyzerService, string initialPath, bool useDarkTitleBar)
    {
        InitializeComponent();
        _analyzerService = analyzerService;
        _currentAnalyzedPath = PathValidator.NormalizePath(initialPath);
        FolderPathBox.Text = _currentAnalyzedPath;

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
        Loaded += async (_, _) => await AnalyzeTargetAsync(_currentAnalyzedPath);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        await AnalyzeTargetAsync(FolderPathBox.Text);
    }

    private async void FolderPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AnalyzeTargetAsync(FolderPathBox.Text);
            e.Handled = true;
        }
    }

    private async void UpParent_Click(object sender, RoutedEventArgs e)
    {
        string? parent = PathValidator.GetParentDirectoryPath(_currentAnalyzedPath);
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            await AnalyzeTargetAsync(parent);
        }
    }

    private async void BreakdownListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BreakdownListView.SelectedItem is FolderSizeItem { IsDirectory: true } item)
        {
            await AnalyzeTargetAsync(item.FullPath);
        }
    }

    private async Task AnalyzeTargetAsync(string rawPath)
    {
        string path = PathValidator.NormalizePath(rawPath);
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusText.Text = "Directory does not exist.";
            return;
        }

        var cts = CancellationHelper.Replace(ref _cts);

        _currentAnalyzedPath = path;
        FolderPathBox.Text = path;
        UpParentButton.IsEnabled = PathValidator.GetParentDirectoryPath(path) is not null;

        AnalyzeButton.IsEnabled = false;
        StopAnalyzeButton.Visibility = Visibility.Visible;
        BreakdownListView.ItemsSource = null;

        var progress = new Progress<string>(msg => StatusText.Text = msg);

        try
        {
            FolderSizeReport report = await _analyzerService.AnalyzeFolderAsync(path, progress, cts.Token);
            if (!ReferenceEquals(_cts, cts))
                return;

            BreakdownListView.ItemsSource = report.Items;
            TotalsBadgeText.Text = $"{report.FormattedTotalSize} • {report.TotalFiles:N0} files • {report.TotalSubfolders:N0} folders";
            StatusText.Text = report.SkippedInaccessibleCount > 0
                ? $"Analysis complete ({report.SkippedInaccessibleCount:N0} restricted folder(s) skipped)."
                : $"Analyzed {report.Items.Count:N0} top-level entries.";
        }
        // A run superseded by a newer analysis leaves the status and buttons to that run.
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts))
                StatusText.Text = "Folder size analysis cancelled.";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_cts, cts))
                StatusText.Text = $"Analysis error: {ex.Message}";
        }
        finally
        {
            if (CancellationHelper.Release(ref _cts, cts))
            {
                AnalyzeButton.IsEnabled = true;
                StopAnalyzeButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OpenInTab_Click(object sender, RoutedEventArgs e)
    {
        if (BreakdownListView.SelectedItem is FolderSizeItem item)
        {
            RequestedTabPath = item.IsDirectory
                ? item.FullPath
                : Path.GetDirectoryName(item.FullPath) ?? _currentAnalyzedPath;
        }
        else if (Directory.Exists(_currentAnalyzedPath))
        {
            RequestedTabPath = _currentAnalyzedPath;
        }

        if (!string.IsNullOrWhiteSpace(RequestedTabPath))
            DialogResult = true;
    }

    private void StopAnalyze_Click(object sender, RoutedEventArgs e)
    {
        CancellationHelper.SafeCancel(_cts);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CancellationHelper.CancelAndClear(ref _cts);
    }
}
