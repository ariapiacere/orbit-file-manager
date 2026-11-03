using System.IO;

namespace OrbitFileManager.Services.Settings;

public sealed class AppPathsService
{
    public string BaseDirectory { get; }
    public string StorageRoot { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string CacheDirectory { get; }
    public bool IsPortableWritable { get; }

    public string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
    public string FavoritesFilePath => Path.Combine(DataDirectory, "favorites.json");
    public string SessionsFilePath => Path.Combine(DataDirectory, "sessions.json");

    public AppPathsService(string? customRootDirectory = null)
    {
        BaseDirectory = string.IsNullOrWhiteSpace(customRootDirectory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(customRootDirectory);

        if (TryInitializePortableDirectories(BaseDirectory))
        {
            StorageRoot = BaseDirectory;
            IsPortableWritable = true;
        }
        else
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbitFileManager");
            TryInitializePortableDirectories(fallback);
            StorageRoot = fallback;
            IsPortableWritable = false;
        }

        DataDirectory = Path.Combine(StorageRoot, "Data");
        LogsDirectory = Path.Combine(StorageRoot, "Logs");
        CacheDirectory = Path.Combine(StorageRoot, "Cache");
    }

    private static bool TryInitializePortableDirectories(string root)
    {
        try
        {
            string dataDir = Path.Combine(root, "Data");
            string logsDir = Path.Combine(root, "Logs");
            string cacheDir = Path.Combine(root, "Cache");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(cacheDir);

            // Probe actual write permission in Data directory
            string probePath = Path.Combine(dataDir, $".write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
