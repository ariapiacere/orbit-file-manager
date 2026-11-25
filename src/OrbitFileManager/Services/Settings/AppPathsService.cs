using System.IO;

namespace OrbitFileManager.Services.Settings;

/// <summary>
/// Where Orbit keeps its own files. Since 1.2.8 the data (favorites, settings, tabs) lives in
/// %LOCALAPPDATA%\OrbitFileManager, so moving, deleting or re-downloading the exe never loses it.
/// A file named <see cref="PortableMarkerFileName"/> next to the exe keeps everything next to the exe instead.
/// </summary>
public sealed class AppPathsService
{
    /// <summary>An empty file with this name next to the exe turns on portable mode.</summary>
    public const string PortableMarkerFileName = "portable.txt";

    public static string DefaultAppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrbitFileManager");

    private readonly Lazy<bool> _installFolderWritable;

    /// <summary>The folder that holds the exe.</summary>
    public string BaseDirectory { get; }
    public string StorageRoot { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string CacheDirectory { get; }

    /// <summary>True when the data is kept next to the exe rather than in AppData.</summary>
    public bool IsPortable { get; }

    /// <summary>True when Orbit can replace its own exe (the updater needs this, wherever the data lives).</summary>
    public bool CanWriteInstallFolder => _installFolderWritable.Value;

    /// <summary>What was brought over from an older portable Data folder at this start, or null.</summary>
    public PortableDataMigration? Migration { get; }

    public string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
    public string FavoritesFilePath => Path.Combine(DataDirectory, "favorites.json");
    public string SessionsFilePath => Path.Combine(DataDirectory, "sessions.json");

    /// <summary>
    /// With no argument: the normal layout (exe folder + AppData). With a folder: everything stays inside that
    /// folder, which tests use to stay away from the real AppData.
    /// </summary>
    public AppPathsService(string? customRootDirectory = null)
        : this(
            string.IsNullOrWhiteSpace(customRootDirectory) ? AppContext.BaseDirectory : customRootDirectory,
            string.IsNullOrWhiteSpace(customRootDirectory) ? DefaultAppDataRoot : customRootDirectory)
    {
    }

    public AppPathsService(string installDirectory, string appDataRoot)
    {
        BaseDirectory = Path.GetFullPath(installDirectory);
        string appData = Path.GetFullPath(appDataRoot);
        _installFolderWritable = new Lazy<bool>(() => CanWriteTo(BaseDirectory));

        bool portableRequested = SamePath(BaseDirectory, appData)
            || File.Exists(Path.Combine(BaseDirectory, PortableMarkerFileName));

        if (portableRequested && TryCreateLayout(BaseDirectory))
        {
            StorageRoot = BaseDirectory;
        }
        else if (TryCreateLayout(appData))
        {
            StorageRoot = appData;
        }
        else if (TryCreateLayout(BaseDirectory))
        {
            StorageRoot = BaseDirectory; // AppData is not usable on this PC; keep working next to the exe
        }
        else
        {
            StorageRoot = appData; // nothing is writable; the services log their failures
        }

        IsPortable = SamePath(StorageRoot, BaseDirectory);
        DataDirectory = Path.Combine(StorageRoot, "Data");
        LogsDirectory = Path.Combine(StorageRoot, "Logs");
        CacheDirectory = Path.Combine(StorageRoot, "Cache");

        // Versions up to 1.2.7 kept the data next to the exe. Bring it over once so favorites are not lost.
        if (!IsPortable)
            Migration = PortableDataMigration.Run(Path.Combine(BaseDirectory, "Data"), DataDirectory);
    }

    private static bool TryCreateLayout(string root)
    {
        try
        {
            string dataDir = Path.Combine(root, "Data");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(Path.Combine(root, "Logs"));
            Directory.CreateDirectory(Path.Combine(root, "Cache"));
            return CanWriteTo(dataDir);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanWriteTo(string directory)
    {
        try
        {
            string probePath = Path.Combine(directory, $".write_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
