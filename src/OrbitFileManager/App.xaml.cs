using Microsoft.Extensions.DependencyInjection;
using OrbitFileManager.Services;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Preview;
using OrbitFileManager.Services.Productivity;
using OrbitFileManager.Services.Search;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.Services.Thumbnails;
using OrbitFileManager.Services.Updates;
using OrbitFileManager.ViewModels;
using System.Windows;

namespace OrbitFileManager;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var logger = _serviceProvider.GetRequiredService<StructuredLogger>();
        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error("App", "Unhandled UI exception.", args.Exception);
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}",
                "Orbit File Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        settingsService.Load();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        // After an update from the old folder install, remove the files it left behind (background, best effort).
        _ = _serviceProvider.GetRequiredService<InstallLayoutService>().CleanUpAsync();
    }

    /// <summary>
    /// Windows is signing out, restarting or shutting down. WPF skips Window.Closing then, so save the tabs here; the
    /// autosave already keeps them current, this catches the last second.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try
        {
            (MainWindow as MainWindow)?.SaveWindowAndSession();
        }
        catch (Exception ex)
        {
            _serviceProvider?.GetService<StructuredLogger>()?.Error("App", "Could not save the session while Windows was ending it.", ex);
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<AppPathsService>();
        services.AddSingleton<StructuredLogger>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ShortcutService>();
        services.AddSingleton<FavoritesService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<MetadataStoreService>();
        services.AddSingleton<ShellIntegrationService>();
        services.AddSingleton<FileBrowserService>();
        services.AddSingleton<FileOperationService>();
        services.AddSingleton<FileOperationQueue>();
        services.AddSingleton<BatchRenameService>();
        services.AddSingleton<FileSearchService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<PreviewService>();
        services.AddSingleton<ChecksumService>();
        services.AddSingleton<DuplicateFinderService>();
        services.AddSingleton<FolderSizeAnalyzerService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<InstallLayoutService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IUserDialogService, WpfUserDialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
