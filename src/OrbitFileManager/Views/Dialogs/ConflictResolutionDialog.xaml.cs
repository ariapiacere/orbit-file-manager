using OrbitFileManager.Models;
using OrbitFileManager.Services;
using OrbitFileManager.Utilities;
using System.IO;
using System.Windows;

namespace OrbitFileManager.Views.Dialogs;

public partial class ConflictResolutionDialog : Window
{
    public ConflictDecision Decision { get; private set; } = new(ConflictResolution.Cancel, false);

    public ConflictResolutionDialog(FileConflictContext context, bool useDarkTitleBar)
    {
        InitializeComponent();

        ItemNameText.Text = Path.GetFileName(context.DestinationPath);
        SourceDetailsText.Text = FormatDetails(context.IsDirectory, context.SourceSize, context.SourceModified, context.SourcePath);
        DestDetailsText.Text = FormatDetails(context.IsDirectory, context.DestinationSize, context.DestinationModified, context.DestinationPath);

        SourceInitialized += (_, _) => ThemeService.ApplyWindowTitleBarTheme(this, useDarkTitleBar);
    }

    private static string FormatDetails(bool isDir, long? size, DateTime modified, string fullPath)
    {
        string sizeLine = isDir
            ? "Folder"
            : (size.HasValue ? FileSizeFormatter.FormatBytes(size.Value) : "Unknown size");
        string modLine = modified == DateTime.MinValue ? "—" : modified.ToString("yyyy-MM-dd HH:mm:ss");
        string folder = Path.GetDirectoryName(fullPath) ?? fullPath;
        return $"Size: {sizeLine}\nModified: {modLine}\nIn: {folder}";
    }

    private void Choose(ConflictResolution resolution)
    {
        Decision = new ConflictDecision(resolution, ApplyToAllCheck.IsChecked == true);
        DialogResult = resolution != ConflictResolution.Cancel;
        Close();
    }

    private void KeepBoth_Click(object sender, RoutedEventArgs e) => Choose(ConflictResolution.KeepBoth);
    private void Overwrite_Click(object sender, RoutedEventArgs e) => Choose(ConflictResolution.Overwrite);
    private void Skip_Click(object sender, RoutedEventArgs e) => Choose(ConflictResolution.Skip);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Choose(ConflictResolution.Cancel);
}
