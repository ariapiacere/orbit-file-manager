using OrbitFileManager.Services;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.Productivity;
using OrbitFileManager.Utilities;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace OrbitFileManager.Views.Dialogs;

public partial class DuplicateFinderDialog : Window
{
    private readonly DuplicateFinderService _duplicateService;
    private readonly FileOperationService _fileOperationService;
    private readonly bool _useDarkTitleBar;
    private readonly ObservableCollection<DuplicateFileEntry> _flatEntries = [];
    private IReadOnlyList<DuplicateFileGroup> _groups = [];
    private CancellationTokenSource? _cts;

    public bool FilesModified { get; private set; }

    public DuplicateFinderDialog(
        DuplicateFinderService duplicateService,
        FileOperationService fileOperationService,
        string initialFolder,
        bool defaultHidden,
        bool useDarkTitleBar)
    {
        InitializeComponent();
        _duplicateService = duplicateService;
        _fileOperationService = fileOperationService;
        _useDarkTitleBar = useDarkTitleBar;

        TargetFolderBox.Text = initialFolder;
        IncludeHiddenCheck.IsChecked = defaultHidden;

        ICollectionView view = CollectionViewSource.GetDefaultView(_flatEntries);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DuplicateFileEntry.GroupHeader)));
        DuplicatesListView.ItemsSource = view;

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
        Loaded += async (_, _) => await RunScanAsync();
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        await RunScanAsync();
    }

    private async Task RunScanAsync()
    {
        string folder = PathValidator.NormalizePath(TargetFolderBox.Text);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusFooterText.Text = "Please enter an existing directory path to scan.";
            return;
        }

        var cts = CancellationHelper.Replace(ref _cts);

        StartScanButton.IsEnabled = false;
        StopScanButton.Visibility = Visibility.Visible;
        RecycleSelectedButton.IsEnabled = false;
        _flatEntries.Clear();
        _groups = [];

        bool recursive = RecursiveCheck.IsChecked == true;
        bool includeHidden = IncludeHiddenCheck.IsChecked == true;
        long minSize = IncludeEmptyCheck.IsChecked == true ? 0L : 1L;

        var progress = new Progress<string>(msg => StatusFooterText.Text = msg);

        try
        {
            DuplicateScanResult res = await _duplicateService.FindDuplicatesAsync(
                [folder],
                recursive,
                minSize,
                includeHidden,
                progress,
                cts.Token);

            _groups = res.Groups;
            foreach (var group in _groups)
            {
                foreach (var entry in group.Files)
                    _flatEntries.Add(entry);
            }

            SummaryBadgeText.Text = _groups.Count == 0
                ? "0 duplicate groups"
                : $"{_groups.Count:N0} group(s) • {FileSizeFormatter.FormatBytes(res.TotalReclaimableBytes)} reclaimable";

            StatusFooterText.Text = _groups.Count == 0
                ? $"Scanned {res.ScannedFileCount:N0} file(s) — no exact duplicates found."
                : $"Scanned {res.ScannedFileCount:N0} file(s). Found {_groups.Count:N0} duplicate group(s).";

            UpdateCheckedButtonState();
        }
        catch (OperationCanceledException)
        {
            StatusFooterText.Text = "Duplicate scan stopped.";
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = $"Scan error: {ex.Message}";
        }
        finally
        {
            CancellationHelper.Release(ref _cts, cts);
            StartScanButton.IsEnabled = true;
            StopScanButton.Visibility = Visibility.Collapsed;
        }
    }

    private void KeepOldest_Click(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups)
        {
            var ordered = g.Files.OrderBy(f => f.ModifiedTime).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].IsMarkedForRemoval = i > 0;
        }
        UpdateCheckedButtonState();
    }

    private void KeepNewest_Click(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups)
        {
            var ordered = g.Files.OrderByDescending(f => f.ModifiedTime).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].IsMarkedForRemoval = i > 0;
        }
        UpdateCheckedButtonState();
    }

    private void UncheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _flatEntries)
            entry.IsMarkedForRemoval = false;
        UpdateCheckedButtonState();
    }

    private void EntryCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCheckedButtonState();
    }

    private void UpdateCheckedButtonState()
    {
        var marked = _flatEntries.Where(f => f.IsMarkedForRemoval).ToList();
        RecycleSelectedButton.IsEnabled = marked.Count > 0;
        if (marked.Count > 0)
        {
            long bytes = marked.Sum(m => m.SizeBytes);
            RecycleSelectedButton.Content = $"Move {marked.Count:N0} Checked ({FileSizeFormatter.FormatBytes(bytes)}) to Recycle Bin";
        }
        else
        {
            RecycleSelectedButton.Content = "Move Checked to Recycle Bin";
        }
    }

    private async void RecycleSelected_Click(object sender, RoutedEventArgs e)
    {
        var marked = _flatEntries.Where(f => f.IsMarkedForRemoval).ToList();
        if (marked.Count == 0)
            return;

        // Safety check: warn if an entire group has ALL copies checked!
        bool anyGroupWipedOut = _groups.Any(g => g.Files.All(f => f.IsMarkedForRemoval));
        string warning = anyGroupWipedOut
            ? $"WARNING: At least one duplicate group has EVERY copy checked (zero copies kept)!\n\nMove {marked.Count:N0} checked file(s) to the Windows Recycle Bin?"
            : $"Move {marked.Count:N0} checked duplicate file(s) ({FileSizeFormatter.FormatBytes(marked.Sum(m => m.SizeBytes))}) to the Windows Recycle Bin?";

        var confirm = new ConfirmActionDialog(
            "Recycle Checked Duplicates",
            warning,
            "Move to Recycle Bin",
            isDanger: anyGroupWipedOut,
            useDarkTitleBar: _useDarkTitleBar)
        {
            Owner = this
        };

        if (confirm.ShowDialog() != true)
            return;

        RecycleSelectedButton.IsEnabled = false;
        var paths = marked.Select(m => m.FullPath).ToList();
        var progress = new Progress<string>(msg => StatusFooterText.Text = msg);

        var result = await _fileOperationService.DeleteItemsAsync(
            paths,
            permanent: false,
            statusProgress: progress,
            cancellationToken: CancellationToken.None);

        if (result.SucceededCount > 0)
            FilesModified = true;

        await RunScanAsync();
        StatusFooterText.Text = result.HasErrors
            ? $"Moved {result.SucceededCount:N0} file(s) to Recycle Bin ({result.Errors.Count} error(s))."
            : $"Moved {result.SucceededCount:N0} duplicate file(s) to the Recycle Bin.";
    }

    private void StopScan_Click(object sender, RoutedEventArgs e)
    {
        CancellationHelper.SafeCancel(_cts);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CancellationHelper.CancelAndClear(ref _cts);
    }
}
