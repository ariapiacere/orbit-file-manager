using OrbitFileManager.Services.Settings;
using System.IO;
using System.Reflection;

namespace OrbitFileManager.Services.Updates;

/// <summary>
/// Since 1.2.0 Orbit ships as one self-contained OrbitFileManager.exe. Up to 1.1.x it was a folder of about 470
/// files, and the 1.1.x updater copies the new exe over the old one, leaving the rest behind (harmless: a
/// single-file exe loads nothing from its folder, but it is about 160 MB of clutter). On start, this tidies up:
/// <list type="bullet">
/// <item>Old folder files: removed only when this is the single-file build, its folder still holds
/// OrbitFileManager.dll (the mark of an old folder install), and only names from the embedded list of what
/// 1.0.x–1.1.x shipped (LegacyFolderFiles.txt). Data, Logs, Cache and anything else are never touched.</item>
/// <item>Native WPF libraries that older single-file versions extracted to %TEMP%\.net\OrbitFileManager\{id}:
/// removed unless another running Orbit still uses them.</item>
/// </list>
/// Everything is best effort and runs off the UI thread; failures are logged and retried on the next start.
/// </summary>
public sealed class InstallLayoutService
{
    public const string ExecutableName = "OrbitFileManager.exe";
    private const string LegacyMarkerFile = "OrbitFileManager.dll";
    private const string ManifestResource = "OrbitFileManager.Services.Updates.LegacyFolderFiles.txt";

    private readonly StructuredLogger _logger;

    public InstallLayoutService(StructuredLogger logger)
    {
        _logger = logger;
    }

    /// <summary>True for the single-file build: its assemblies are inside the exe, so they have no file location.</summary>
#pragma warning disable IL3000 // the empty Location is exactly what is being tested for
    public static bool IsSingleFileBuild => string.IsNullOrEmpty(typeof(InstallLayoutService).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>The running exe (it may have been renamed), or the expected name in the app folder.</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath is { Length: > 0 } path ? path : Path.Combine(AppContext.BaseDirectory, ExecutableName);

    public static string InstallDirectory => Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    public Task CleanUpAsync() => IsSingleFileBuild ? Task.Run(CleanUp) : Task.CompletedTask;

    private void CleanUp()
    {
        try
        {
            int removed = RemoveLegacyFolderFiles(InstallDirectory, LoadLegacyManifest(), out int failed);
            if (removed > 0 || failed > 0)
                _logger.Info("Update", "Removed files left over from the old folder install.", new { removed, failed, folder = InstallDirectory });

            int extractions = RemoveStaleNativeExtractions(CurrentExtractionDirectory());
            if (extractions > 0)
                _logger.Info("Update", "Removed native libraries extracted by older versions.", new { folders = extractions });
        }
        catch (Exception ex)
        {
            _logger.Warn("Update", "Tidying up after the update failed; it is retried on the next start.", new { ex.Message });
        }
    }

    /// <summary>Relative paths of every file a 1.0.x–1.1.x folder install contained, except OrbitFileManager.exe.</summary>
    public static IReadOnlyList<string> LoadLegacyManifest()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ManifestResource);
        if (stream is null)
            return [];

        using var reader = new StreamReader(stream);
        var names = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
                names.Add(line);
        }

        return names;
    }

    /// <summary>
    /// Deletes the listed files from <paramref name="installDir"/> when it is an old folder install (it holds
    /// OrbitFileManager.dll), then the language folders those files leave empty. Entries that are rooted, climb out
    /// of the folder, or name the exe or the Data/Logs/Cache folders are ignored.
    /// </summary>
    public static int RemoveLegacyFolderFiles(string installDir, IEnumerable<string> manifest, out int failed)
    {
        failed = 0;
        string root = Path.GetFullPath(installDir);
        if (!File.Exists(Path.Combine(root, LegacyMarkerFile)))
            return 0;

        string rootWithSlash = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var touchedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int removed = 0;

        // The marker goes last, so an interrupted clean-up is picked up again on the next start.
        foreach (string entry in manifest.OrderBy(e => string.Equals(e, LegacyMarkerFile, StringComparison.OrdinalIgnoreCase)))
        {
            if (!IsSafeEntry(entry))
                continue;

            string path = Path.GetFullPath(Path.Combine(root, entry));
            if (!path.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                continue;

            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                removed++;
                if (Path.GetDirectoryName(path) is { } folder && !string.Equals(folder.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    touchedFolders.Add(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        foreach (string folder in touchedFolders)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static bool IsSafeEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || entry.Contains("..", StringComparison.Ordinal))
            return false;

        string first = entry.Split('\\', '/')[0];
        return !string.Equals(entry, ExecutableName, StringComparison.OrdinalIgnoreCase)
            && !first.Equals("Data", StringComparison.OrdinalIgnoreCase)
            && !first.Equals("Logs", StringComparison.OrdinalIgnoreCase)
            && !first.Equals("Cache", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The folder the single-file host extracted this build's native libraries to (…\.net\OrbitFileManager\{id}),
    /// read from the runtime's native search path. Null when not found.
    /// </summary>
    public static string? CurrentExtractionDirectory()
    {
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string searchDirs)
            return null;

        foreach (string raw in searchDirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = Path.TrimEndingDirectorySeparator(raw);
            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent is not null
                && parent.Name.Equals("OrbitFileManager", StringComparison.OrdinalIgnoreCase)
                && parent.Parent?.Name.Equals(".net", StringComparison.OrdinalIgnoreCase) == true)
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// Deletes the other folders next to <paramref name="currentExtractionDir"/>. Each one is first renamed: Windows
    /// refuses that while a running Orbit has a library from it loaded, so those are left alone.
    /// </summary>
    public static int RemoveStaleNativeExtractions(string? currentExtractionDir)
    {
        if (string.IsNullOrEmpty(currentExtractionDir) || Directory.GetParent(currentExtractionDir) is not { Exists: true } parent)
            return 0;

        int removed = 0;
        foreach (DirectoryInfo sibling in parent.EnumerateDirectories())
        {
            if (string.Equals(sibling.FullName.TrimEnd('\\'), currentExtractionDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                continue;

            string doomed = Path.Combine(parent.FullName, $"~delete-{Guid.NewGuid():N}");
            try
            {
                if (!sibling.Name.StartsWith("~delete-", StringComparison.Ordinal))
                    sibling.MoveTo(doomed);
                else
                    doomed = sibling.FullName; // left over from an interrupted clean-up

                Directory.Delete(doomed, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}
