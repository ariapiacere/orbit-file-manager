using OrbitFileManager.Services;
using OrbitFileManager.Services.Productivity;
using OrbitFileManager.Utilities;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrbitFileManager.Views.Dialogs;

public partial class ChecksumDialog : Window
{
    private sealed record TargetFileOption(string FilePath, string FileName);

    private readonly ChecksumService _checksumService;
    private readonly List<TargetFileOption> _targets;
    private readonly Dictionary<string, FileChecksumResult> _computedCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private FileChecksumResult? _activeResult;

    public ChecksumDialog(ChecksumService checksumService, IReadOnlyList<string> filePaths, bool useDarkTitleBar)
    {
        InitializeComponent();
        _checksumService = checksumService;
        _targets = filePaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new TargetFileOption(p, Path.GetFileName(p)))
            .ToList();

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
        Loaded += ChecksumDialog_Loaded;
    }

    private async void ChecksumDialog_Loaded(object sender, RoutedEventArgs e)
    {
        FileCombo.ItemsSource = _targets;
        if (_targets.Count > 0)
        {
            FileCombo.SelectedIndex = 0;
        }
        else
        {
            StatusBadgeText.Text = "No valid files selected";
        }
        await Task.CompletedTask;
    }

    private async void FileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileCombo.SelectedItem is not TargetFileOption opt)
            return;

        if (_computedCache.TryGetValue(opt.FilePath, out var cached))
        {
            DisplayResult(cached);
            return;
        }

        await ComputeForFileAsync(opt.FilePath);
    }

    private async Task ComputeForFileAsync(string filePath)
    {
        var cts = CancellationHelper.Replace(ref _cts);

        Md5Box.Text = "Calculating...";
        Sha1Box.Text = "Calculating...";
        Sha256Box.Text = "Calculating...";
        Sha512Box.Text = "Calculating...";
        FileMetaText.Text = filePath;
        HashProgressBar.Value = 0;
        HashProgressBar.Visibility = Visibility.Visible;
        CancelCalcButton.Visibility = Visibility.Visible;
        StatusBadgeText.Text = "Streaming file...";

        var progress = new Progress<double>(pct =>
        {
            HashProgressBar.Value = pct;
            StatusBadgeText.Text = $"Computing... {pct:0}%";
        });

        try
        {
            FileChecksumResult result = await _checksumService.ComputeChecksumsAsync(filePath, progress, cts.Token);
            _computedCache[filePath] = result;
            if (ReferenceEquals(_cts, cts))
                DisplayResult(result);
        }
        // A run superseded by a newer file selection leaves the hash boxes to that run.
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_cts, cts))
                return;

            StatusBadgeText.Text = "Calculation cancelled";
            Md5Box.Text = string.Empty;
            Sha1Box.Text = string.Empty;
            Sha256Box.Text = string.Empty;
            Sha512Box.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusBadgeText.Text = "Error";
            FileMetaText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            if (CancellationHelper.Release(ref _cts, cts))
            {
                HashProgressBar.Visibility = Visibility.Collapsed;
                CancelCalcButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void DisplayResult(FileChecksumResult result)
    {
        _activeResult = result;
        FileMetaText.Text = $"{result.FilePath}  •  {result.FormattedSize} ({result.SizeBytes:N0} bytes)";
        Md5Box.Text = result.Md5;
        Sha1Box.Text = result.Sha1;
        Sha256Box.Text = result.Sha256;
        Sha512Box.Text = result.Sha512;
        StatusBadgeText.Text = $"Done in {result.Elapsed.TotalMilliseconds:0} ms";
        UpdateVerificationState();
    }

    private void ExpectedHashBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateVerificationState();
    }

    private void UpdateVerificationState()
    {
        string expected = ExpectedHashBox.Text;
        if (string.IsNullOrWhiteSpace(expected) || _activeResult is null)
        {
            VerifyResultText.Text = string.Empty;
            return;
        }

        var (isMatch, algo) = ChecksumService.VerifyExpectedHash(_activeResult, expected);
        if (isMatch)
        {
            VerifyResultText.Text = $"✓ Verified Match ({algo})";
            VerifyResultText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        else
        {
            VerifyResultText.Text = "✕ Does Not Match";
            VerifyResultText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (_activeResult is null || sender is not Button btn || btn.Tag is not string kind)
            return;

        string val = kind switch
        {
            "MD5" => _activeResult.Md5,
            "SHA1" => _activeResult.Sha1,
            "SHA256" => _activeResult.Sha256,
            "SHA512" => _activeResult.Sha512,
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(val))
        {
            Clipboard.SetText(val);
            StatusBadgeText.Text = $"Copied {kind} to clipboard";
        }
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_activeResult is null)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"File:    {_activeResult.FileName}");
        sb.AppendLine($"Path:    {_activeResult.FilePath}");
        sb.AppendLine($"Size:    {_activeResult.FormattedSize} ({_activeResult.SizeBytes:N0} bytes)");
        sb.AppendLine($"MD5:     {_activeResult.Md5}");
        sb.AppendLine($"SHA-1:   {_activeResult.Sha1}");
        sb.AppendLine($"SHA-256: {_activeResult.Sha256}");
        sb.AppendLine($"SHA-512: {_activeResult.Sha512}");

        Clipboard.SetText(sb.ToString());
        StatusBadgeText.Text = "Copied full checksum report";
    }

    private void CancelCalc_Click(object sender, RoutedEventArgs e)
    {
        CancellationHelper.SafeCancel(_cts);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CancellationHelper.CancelAndClear(ref _cts);
    }
}
