using OrbitFileManager.Services;
using OrbitFileManager.Services.Updates;
using OrbitFileManager.Utilities;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace OrbitFileManager.Views.Dialogs;

public partial class UpdateDialog : Window
{
    private readonly UpdateService _updateService;
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _cts;

    public UpdateDialog(UpdateService updateService, UpdateInfo update, bool useDarkTitleBar)
    {
        InitializeComponent();
        _updateService = updateService;
        _update = update;

        string newVersion = UpdateService.Format(update.Version);
        HeaderText.Text = $"Orbit {newVersion} is available";
        SubheaderText.Text = $"You have {UpdateService.Format(UpdateService.CurrentVersion)}. Your settings, tabs and logs are kept.";
        NotesBox.Text = string.IsNullOrWhiteSpace(update.Notes) ? "No release notes." : update.Notes;

        if (!updateService.CanInstallInPlace)
        {
            ReadOnlyInstallText.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = false;
        }

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var cts = CancellationHelper.Replace(ref _cts);
        InstallButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");

        double sizeMb = _update.PackageSize / 1024d / 1024d;
        var progress = new Progress<double>(fraction =>
        {
            DownloadProgress.Value = fraction;
            ProgressText.Text = sizeMb > 0
                ? $"Downloading… {fraction * sizeMb:0} of {sizeMb:0} MB"
                : $"Downloading… {fraction:P0}";
        });

        try
        {
            ProgressText.Text = "Downloading…";
            string stagedDir = await _updateService.DownloadAndStageAsync(_update, progress, cts.Token);
            ProgressText.Text = "Installing. Orbit will close and reopen in a few seconds.";
            _updateService.StartInstallAfterExit(stagedDir, _update.Version);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"Update failed: {ex.Message}";
            ProgressText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            InstallButton.Content = "Try again";
            InstallButton.IsEnabled = true;
        }
        finally
        {
            CancellationHelper.Release(ref _cts, cts);
        }
    }

    private void ReleasePage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_update.ReleasePageUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CancellationHelper.CancelAndClear(ref _cts);
    }
}
