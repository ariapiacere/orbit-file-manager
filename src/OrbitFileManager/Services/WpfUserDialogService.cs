using OrbitFileManager.Models;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.Productivity;
using OrbitFileManager.Services.Search;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Services.Updates;
using OrbitFileManager.Views.Dialogs;
using System.Windows;

namespace OrbitFileManager.Services;

public sealed class WpfUserDialogService : IUserDialogService
{
    private readonly IThemeService _themeService;

    public WpfUserDialogService(IThemeService themeService)
    {
        _themeService = themeService;
    }

    public string? PromptTextInput(
        string title,
        string prompt,
        string initialValue,
        int selectPrefixLength = -1,
        bool validateAsFileName = true)
    {
        var dialog = new TextPromptDialog(title, prompt, initialValue, selectPrefixLength, _themeService.CurrentTheme == AppThemeMode.Dark, validateAsFileName)
        {
            Owner = GetActiveOwnerWindow()
        };

        return dialog.ShowDialog() == true ? dialog.InputResult : null;
    }

    public bool ConfirmAction(
        string title,
        string message,
        string confirmButtonLabel,
        bool isDanger = false)
    {
        var dialog = new ConfirmActionDialog(title, message, confirmButtonLabel, isDanger, _themeService.CurrentTheme == AppThemeMode.Dark)
        {
            Owner = GetActiveOwnerWindow()
        };

        return dialog.ShowDialog() == true;
    }

    public Task<ConflictDecision> ResolveConflictAsync(FileConflictContext context)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            return dispatcher.InvokeAsync(() =>
            {
                var dialog = new ConflictResolutionDialog(context, _themeService.CurrentTheme == AppThemeMode.Dark)
                {
                    Owner = GetActiveOwnerWindow()
                };
                dialog.ShowDialog();
                return dialog.Decision;
            }).Task;
        }

        return Task.FromResult(new ConflictDecision(ConflictResolution.Skip, false));
    }

    public void ShowError(string title, string message)
    {
        void Show()
        {
            var dialog = new ConfirmActionDialog(title, message, "OK", isDanger: false, _themeService.CurrentTheme == AppThemeMode.Dark, hideCancel: true)
            {
                Owner = GetActiveOwnerWindow()
            };
            dialog.ShowDialog();
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(Show);
        else
            Show();
    }

    public bool ShowSettingsDialog(AppSettings currentSettings, ShortcutService? shortcutService, string storagePath, bool isPortable)
    {
        var dialog = new SettingsDialog(currentSettings, shortcutService, storagePath, isPortable, _themeService.CurrentTheme == AppThemeMode.Dark)
        {
            Owner = GetActiveOwnerWindow()
        };
        return dialog.ShowDialog() == true;
    }

    public int? ShowBatchRenameDialog(BatchRenameService renameService, IReadOnlyList<string> sourcePaths)
    {
        var dialog = new BatchRenameDialog(renameService, sourcePaths, _themeService.CurrentTheme == AppThemeMode.Dark)
        {
            Owner = GetActiveOwnerWindow()
        };
        return dialog.ShowDialog() == true ? dialog.RenamedCount : null;
    }

    public SearchRequest? ShowAdvancedSearchDialog(FileSearchService searchService, IReadOnlyList<string> defaultLocations, bool defaultHidden)
    {
        var dialog = new AdvancedSearchDialog(searchService, defaultLocations, defaultHidden, _themeService.CurrentTheme == AppThemeMode.Dark)
        {
            Owner = GetActiveOwnerWindow()
        };
        return dialog.ShowDialog() == true ? dialog.BuiltRequest : null;
    }

    public void ShowChecksumDialog(ChecksumService checksumService, IReadOnlyList<string> filePaths)
    {
        var dialog = new ChecksumDialog(checksumService, filePaths, _themeService.CurrentTheme == AppThemeMode.Dark)
        {
            Owner = GetActiveOwnerWindow()
        };
        dialog.ShowDialog();
    }

    public bool ShowDuplicateFinderDialog(
        DuplicateFinderService duplicateService,
        FileOperationService fileOperationService,
        string initialFolder,
        bool defaultHidden)
    {
        var dialog = new DuplicateFinderDialog(duplicateService, fileOperationService, initialFolder, defaultHidden, _themeService.CurrentTheme == AppThemeMode.Dark)
        {
            Owner = GetActiveOwnerWindow()
        };
        dialog.ShowDialog();
        return dialog.FilesModified;
    }

    public string? ShowFolderSizeDialog(FolderSizeAnalyzerService analyzerService, string initialFolder)
    {
        var dialog = new FolderSizeDialog(analyzerService, initialFolder, _themeService.CurrentTheme == AppThemeMode.Dark)
        {
            Owner = GetActiveOwnerWindow()
        };
        return dialog.ShowDialog() == true ? dialog.RequestedTabPath : null;
    }

    public bool ShowUpdateDialog(UpdateService updateService, UpdateInfo update)
    {
        var dialog = new UpdateDialog(updateService, update, _themeService.CurrentTheme == AppThemeMode.Dark)
        {
            Owner = GetActiveOwnerWindow()
        };
        return dialog.ShowDialog() == true;
    }

    private static Window? GetActiveOwnerWindow()
    {
        if (Application.Current is null)
            return null;

        foreach (Window w in Application.Current.Windows)
        {
            if (w.IsActive)
                return w;
        }

        return Application.Current.MainWindow;
    }
}
