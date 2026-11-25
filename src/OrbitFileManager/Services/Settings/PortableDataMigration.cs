using OrbitFileManager.Models;
using OrbitFileManager.Utilities;
using System.IO;
using System.Text.Json;

namespace OrbitFileManager.Services.Settings;

/// <summary>
/// Brings the Data folder of an older portable copy (up to 1.2.7 the data lived next to the exe) into the AppData
/// Data folder. Each source folder is read once; it is never changed or deleted.
/// <list type="bullet">
/// <item>A file AppData does not have yet is copied as it is.</item>
/// <item>favorites.json, when both have one, gets the favorites AppData is missing (same folder = same favorite).</item>
/// <item>Any other file AppData already has stays as it is.</item>
/// </list>
/// </summary>
public sealed record PortableDataMigration(string SourceDirectory, IReadOnlyList<string> CopiedFiles, int FavoritesAdded)
{
    /// <summary>Lists the source folders already brought over, one per line, so a favorite removed later does not come back.</summary>
    public const string MarkerFileName = "migrated-from.txt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public bool ChangedAnything => CopiedFiles.Count > 0 || FavoritesAdded > 0;

    /// <returns>What was brought over, or null when there was nothing to do (or it failed; it is tried again next start).</returns>
    public static PortableDataMigration? Run(string sourceDataDirectory, string targetDataDirectory)
    {
        try
        {
            string source = Path.GetFullPath(sourceDataDirectory);
            string target = Path.GetFullPath(targetDataDirectory);
            if (!Directory.Exists(source) || AppPathsService.SamePath(source, target))
                return null;

            string marker = Path.Combine(target, MarkerFileName);
            if (AlreadyDone(marker, source))
                return null;

            Directory.CreateDirectory(target);
            var copied = new List<string>();
            int favoritesAdded = 0;

            foreach (string file in Directory.EnumerateFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string destination = Path.Combine(target, name);
                if (!File.Exists(destination))
                {
                    File.Copy(file, destination);
                    copied.Add(name);
                }
                else if (name.Equals("favorites.json", StringComparison.OrdinalIgnoreCase))
                {
                    favoritesAdded = MergeFavorites(file, destination);
                }
            }

            File.AppendAllLines(marker, [source]);
            return new PortableDataMigration(source, copied, favoritesAdded);
        }
        catch
        {
            return null;
        }
    }

    private static bool AlreadyDone(string marker, string source)
    {
        if (!File.Exists(marker))
            return false;

        return File.ReadAllLines(marker).Any(line => !string.IsNullOrWhiteSpace(line) && AppPathsService.SamePath(line.Trim(), source));
    }

    private static int MergeFavorites(string sourceFile, string targetFile)
    {
        List<FavoriteItem>? incoming = Read(sourceFile);
        List<FavoriteItem>? current = Read(targetFile);
        if (incoming is null || current is null)
            return 0; // a damaged file is left alone

        var added = incoming
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) && !current.Any(c => PathValidator.PathsEqual(c.Path, item.Path)))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (added.Count == 0)
            return 0;

        string tempPath = targetFile + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(current.Concat(added).ToList(), JsonOptions));
        File.Move(tempPath, targetFile, overwrite: true);
        return added.Count;
    }

    private static List<FavoriteItem>? Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FavoriteItem>>(File.ReadAllText(file), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
