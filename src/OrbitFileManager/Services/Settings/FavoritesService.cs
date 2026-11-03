using OrbitFileManager.Models;
using OrbitFileManager.Utilities;
using System.IO;
using System.Text.Json;

namespace OrbitFileManager.Services.Settings;

public sealed class FavoritesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppPathsService _paths;
    private readonly StructuredLogger _logger;

    public FavoritesService(AppPathsService paths, StructuredLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<FavoriteItem> GetQuickAccessItems()
    {
        var candidates = new (string Label, Environment.SpecialFolder Folder)[]
        {
            ("Desktop", Environment.SpecialFolder.DesktopDirectory),
            ("Documents", Environment.SpecialFolder.MyDocuments),
            ("Pictures", Environment.SpecialFolder.MyPictures),
            ("Music", Environment.SpecialFolder.MyMusic),
            ("Videos", Environment.SpecialFolder.MyVideos)
        };

        var results = new List<FavoriteItem>();

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
        {
            results.Add(new FavoriteItem { Name = "Home", Path = userProfile, Category = "QuickAccess" });

            string downloads = Path.Combine(userProfile, "Downloads");
            if (Directory.Exists(downloads))
            {
                results.Add(new FavoriteItem { Name = "Downloads", Path = downloads, Category = "QuickAccess" });
            }
        }

        foreach (var (label, specialFolder) in candidates)
        {
            string folderPath = Environment.GetFolderPath(specialFolder);
            if (!string.IsNullOrWhiteSpace(folderPath) &&
                Directory.Exists(folderPath) &&
                !results.Any(r => PathValidator.PathsEqual(r.Path, folderPath)))
            {
                results.Add(new FavoriteItem { Name = label, Path = folderPath, Category = "QuickAccess" });
            }
        }

        return results;
    }

    public List<FavoriteItem> LoadFavorites()
    {
        try
        {
            if (File.Exists(_paths.FavoritesFilePath))
            {
                string json = File.ReadAllText(_paths.FavoritesFilePath);
                var items = JsonSerializer.Deserialize<List<FavoriteItem>>(json, JsonOptions);
                if (items is not null)
                    return items;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Favorites", "Unable to load favorites.json.", ex);
        }

        return [];
    }

    public bool SaveFavorites(IEnumerable<FavoriteItem> favorites)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            string tempPath = _paths.FavoritesFilePath + ".tmp";
            string json = JsonSerializer.Serialize(favorites.ToList(), JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _paths.FavoritesFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Favorites", "Unable to save favorites.json.", ex);
            return false;
        }
    }
}
