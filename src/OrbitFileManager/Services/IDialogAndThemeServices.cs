using OrbitFileManager.Models;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.Productivity;
using OrbitFileManager.Services.Search;
using OrbitFileManager.Services.Settings;

namespace OrbitFileManager.Services;

public interface IUserDialogService
{
    string? PromptTextInput(
        string title,
        string prompt,
        string initialValue,
        int selectPrefixLength = -1,
        bool validateAsFileName = true);

    bool ConfirmAction(
        string title,
        string message,
        string confirmButtonLabel,
        bool isDanger = false);

    Task<ConflictDecision> ResolveConflictAsync(FileConflictContext context);

    void ShowError(string title, string message);

    bool ShowSettingsDialog(AppSettings currentSettings, ShortcutService? shortcutService, string storagePath, bool isPortable);

    int? ShowBatchRenameDialog(BatchRenameService renameService, IReadOnlyList<string> sourcePaths);

    SearchRequest? ShowAdvancedSearchDialog(FileSearchService searchService, IReadOnlyList<string> defaultLocations, bool defaultHidden);

    void ShowChecksumDialog(ChecksumService checksumService, IReadOnlyList<string> filePaths);

    bool ShowDuplicateFinderDialog(
        DuplicateFinderService duplicateService,
        FileOperationService fileOperationService,
        string initialFolder,
        bool defaultHidden);

    string? ShowFolderSizeDialog(FolderSizeAnalyzerService analyzerService, string initialFolder);
}

public interface IThemeService
{
    AppThemeMode CurrentTheme { get; }
    void ApplyTheme(AppThemeMode mode);
}
