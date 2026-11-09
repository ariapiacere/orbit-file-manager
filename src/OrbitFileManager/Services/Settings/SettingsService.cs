using OrbitFileManager.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrbitFileManager.Services.Settings;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPathsService _paths;
    private readonly StructuredLogger _logger;

    public AppSettings Current { get; private set; } = new();

    public SettingsService(AppPathsService paths, StructuredLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_paths.SettingsFilePath))
            {
                string json = File.ReadAllText(_paths.SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    Sanitize(loaded);
                    Current = loaded;
                    return Current;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Settings", "Failed to load settings.json; reverting to defaults.", ex);
        }

        Current = CreateDefaultSettings();
        Save();
        return Current;
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            Sanitize(Current);

            string tempPath = _paths.SettingsFilePath + ".tmp";
            string json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _paths.SettingsFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Settings", "Unable to persist settings.json.", ex);
            return false;
        }
    }

    public void RecordRecentFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        Current.RecentFolders.RemoveAll(existing =>
            string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        Current.RecentFolders.Insert(0, path);

        if (Current.RecentFolders.Count > 15)
            Current.RecentFolders.RemoveRange(15, Current.RecentFolders.Count - 15);
    }

    private static AppSettings CreateDefaultSettings()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile) || !Directory.Exists(userProfile))
            userProfile = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        return new AppSettings
        {
            DefaultStartPath = userProfile
        };
    }

    private static void Sanitize(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Accent))
            settings.Accent = AppAccent.Orange;
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 860, 5120);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 560, 2880);
        settings.SidebarWidth = Math.Clamp(settings.SidebarWidth, 180, 520);
        settings.PreviewPanelWidth = Math.Clamp(settings.PreviewPanelWidth, 220, 620);

        settings.LastOpenTabs ??= [];
        settings.RecentFolders ??= [];

        if (string.IsNullOrWhiteSpace(settings.DefaultStartPath) || !Directory.Exists(settings.DefaultStartPath))
        {
            string fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            settings.DefaultStartPath = Directory.Exists(fallback)
                ? fallback
                : (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
        }
    }
}
