using OrbitFileManager.Services.Updates;
using System.Diagnostics;
using System.IO;

namespace OrbitFileManager.Services.Settings;

public readonly record struct TempUsage(long Bytes, int Files);

/// <summary>
/// Temporary files Orbit leaves behind, for Settings → Clear Temp Files:
/// <list type="bullet">
/// <item>downloaded updates (%TEMP%\OrbitFileManager-update),</item>
/// <item>everything in the Cache folder,</item>
/// <item>logs from earlier days (today's log is kept),</item>
/// <item>half-written saves (*.tmp in Data, older than a minute),</item>
/// <item>the native libraries older Orbit builds unpacked to %TEMP%\.net (the running build's folder is kept).</item>
/// </list>
/// Favorites, settings, tabs and every other file in Data are never touched. Files in use are skipped.
/// </summary>
public sealed class TempFilesService
{
    private static readonly TimeSpan UnfinishedSaveAge = TimeSpan.FromMinutes(1);

    private readonly string _cacheDirectory;
    private readonly string _logsDirectory;
    private readonly string _dataDirectory;
    private readonly string _updateRoot;
    private readonly Lazy<string?> _currentExtractionDirectory;
    private readonly Func<DateTime> _now;

    public TempFilesService(AppPathsService paths)
        : this(paths.CacheDirectory, paths.LogsDirectory, paths.DataDirectory, UpdateService.UpdateRoot, FindExtractionDirectory, () => DateTime.Now)
    {
    }

    /// <param name="currentExtractionDirectory">
    /// The folder the running exe unpacked its native libraries to (%TEMP%\.net\&lt;app&gt;\&lt;build&gt;), or null when it
    /// did not unpack any; its sibling folders are older builds.
    /// </param>
    public TempFilesService(
        string cacheDirectory,
        string logsDirectory,
        string dataDirectory,
        string updateRoot,
        Func<string?> currentExtractionDirectory,
        Func<DateTime> now)
    {
        _cacheDirectory = cacheDirectory;
        _logsDirectory = logsDirectory;
        _dataDirectory = dataDirectory;
        _updateRoot = updateRoot;
        _currentExtractionDirectory = new Lazy<string?>(currentExtractionDirectory);
        _now = now;
    }

    public TempUsage Measure()
    {
        long bytes = 0;
        int files = 0;
        foreach (FileInfo file in EnumerateFiles())
        {
            try
            {
                bytes += file.Length;
                files++;
            }
            catch
            {
                // gone already
            }
        }

        return new TempUsage(bytes, files);
    }

    /// <returns>What was deleted.</returns>
    public TempUsage Clear()
    {
        long bytes = 0;
        int files = 0;
        foreach (FileInfo file in EnumerateFiles().ToList())
        {
            try
            {
                long length = file.Length;
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
                bytes += length;
                files++;
            }
            catch
            {
                // in use or no access: leave it
            }
        }

        RemoveEmptyFolders(_cacheDirectory, keepRoot: true);
        RemoveEmptyFolders(_updateRoot, keepRoot: false);
        foreach (string folder in OldExtractionFolders())
            RemoveEmptyFolders(folder, keepRoot: false);

        return new TempUsage(bytes, files);
    }

    private IEnumerable<FileInfo> EnumerateFiles()
    {
        foreach (FileInfo file in AllFiles(_cacheDirectory))
            yield return file;

        foreach (FileInfo file in AllFiles(_updateRoot))
            yield return file;

        string todaysLog = $"orbit-{_now():yyyyMMdd}.jsonl";
        foreach (FileInfo file in TopFiles(_logsDirectory, "orbit-*.jsonl"))
        {
            if (!file.Name.Equals(todaysLog, StringComparison.OrdinalIgnoreCase))
                yield return file;
        }

        DateTime unfinishedBefore = _now() - UnfinishedSaveAge;
        foreach (FileInfo file in TopFiles(_dataDirectory, "*.tmp"))
        {
            if (file.LastWriteTime < unfinishedBefore)
                yield return file;
        }

        foreach (string folder in OldExtractionFolders())
        {
            foreach (FileInfo file in AllFiles(folder))
                yield return file;
        }
    }

    private IEnumerable<string> OldExtractionFolders()
    {
        string? current = _currentExtractionDirectory.Value;
        if (string.IsNullOrEmpty(current))
            return [];

        try
        {
            string? appFolder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (appFolder is null || !Directory.Exists(appFolder))
                return [];

            return Directory.EnumerateDirectories(appFolder)
                .Where(folder => !AppPathsService.SamePath(folder, current))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<FileInfo> AllFiles(string folder) => Files(folder, "*", SearchOption.AllDirectories);

    private static IEnumerable<FileInfo> TopFiles(string folder, string pattern) => Files(folder, pattern, SearchOption.TopDirectoryOnly);

    private static IEnumerable<FileInfo> Files(string folder, string pattern, SearchOption option)
    {
        if (!Directory.Exists(folder))
            return [];

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, // never follow a link out of the temp folder
                MatchType = MatchType.Win32
            };
            return new DirectoryInfo(folder).EnumerateFiles(pattern, options).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void RemoveEmptyFolders(string root, bool keepRoot)
    {
        if (!Directory.Exists(root))
            return;

        try
        {
            var subfolders = new DirectoryInfo(root)
                .EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .OrderByDescending(d => d.FullName.Length)
                .ToList();
            foreach (DirectoryInfo folder in subfolders)
                TryDeleteEmpty(folder.FullName);

            if (!keepRoot)
                TryDeleteEmpty(root);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteEmpty(string folder)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch
        {
            // not empty or in use
        }
    }

    /// <summary>
    /// The folder the single-file exe unpacked WPF's native libraries to, found from a loaded native library.
    /// Null for a normal (not single-file) build, whose libraries come from the .NET install.
    /// </summary>
    private static string? FindExtractionDirectory()
    {
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                if (!module.ModuleName.Equals("wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase)
                    && !module.ModuleName.Equals("PresentationNative_cor3.dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? folder = Path.GetDirectoryName(module.FileName);
                if (folder is not null && Directory.GetParent(folder)?.Parent?.Name.Equals(".net", StringComparison.OrdinalIgnoreCase) == true)
                    return folder;
            }
        }
        catch
        {
            // no module list: skip this part
        }

        return null;
    }
}
