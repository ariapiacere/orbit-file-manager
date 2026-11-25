using OrbitFileManager.Models;
using OrbitFileManager.Services.Settings;
using System.Text.Json;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class StorageLocationTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly string _root;
    private readonly string _install;
    private readonly string _appData;

    public StorageLocationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"OrbitStorageTests_{Guid.NewGuid():N}");
        _install = Path.Combine(_root, "Downloads");
        _appData = Path.Combine(_root, "LocalAppData", "OrbitFileManager");
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DataLivesInAppDataAndNothingIsCreatedNextToTheExe()
    {
        var paths = new AppPathsService(_install, _appData);

        Assert.False(paths.IsPortable);
        Assert.Equal(_appData, paths.StorageRoot);
        Assert.Equal(Path.Combine(_appData, "Data", "favorites.json"), paths.FavoritesFilePath);
        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_install));
        Assert.True(paths.CanWriteInstallFolder); // the updater still replaces the exe in place
        Assert.Null(paths.Migration);
    }

    [Fact]
    public void FavoritesSurviveDeletingTheExeFolder()
    {
        var first = new AppPathsService(_install, _appData);
        var favorites = new FavoritesService(first, new StructuredLogger(first));
        Assert.True(favorites.SaveFavorites([new FavoriteItem { Name = "Renders", Path = @"D:\Renders" }]));

        Directory.Delete(_install, recursive: true);
        string newDownload = Path.Combine(_root, "Desktop");
        Directory.CreateDirectory(newDownload);

        var second = new AppPathsService(newDownload, _appData);
        var loaded = new FavoritesService(second, new StructuredLogger(second)).LoadFavorites();
        Assert.Equal(@"D:\Renders", Assert.Single(loaded).Path);
    }

    [Fact]
    public void APortableTxtNextToTheExeKeepsTheDataThere()
    {
        File.WriteAllText(Path.Combine(_install, AppPathsService.PortableMarkerFileName), string.Empty);
        WriteFavorites(Path.Combine(_install, "Data", "favorites.json"), (@"D:\Portable", "Portable"));

        var paths = new AppPathsService(_install, _appData);

        Assert.True(paths.IsPortable);
        Assert.Equal(_install, paths.StorageRoot);
        Assert.Null(paths.Migration);
        Assert.False(Directory.Exists(_appData));
    }

    [Fact]
    public void WhenAppDataCannotBeUsedTheDataStaysNextToTheExe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_appData)!);
        File.WriteAllText(_appData, "a file where the folder should be");

        var paths = new AppPathsService(_install, _appData);

        Assert.True(paths.IsPortable);
        Assert.Equal(_install, paths.StorageRoot);
    }

    [Fact]
    public void AnOlderPortableCopyIsBroughtOverOnceAndLeftInPlace()
    {
        string oldData = Path.Combine(_install, "Data");
        WriteFavorites(Path.Combine(oldData, "favorites.json"), (@"D:\Jobs", "Jobs"), (@"E:\Refs", "Refs"));
        File.WriteAllText(Path.Combine(oldData, "settings.json"), "{\"theme\":\"Light\"}");
        File.WriteAllText(Path.Combine(oldData, "sessions.json"), "{}");
        File.WriteAllText(Path.Combine(oldData, "settings.json.tmp"), "half written");

        var paths = new AppPathsService(_install, _appData);

        Assert.NotNull(paths.Migration);
        Assert.Equal(["favorites.json", "sessions.json", "settings.json"], paths.Migration!.CopiedFiles.Order());
        Assert.Equal(2, ReadFavorites(paths.FavoritesFilePath).Count);
        Assert.Equal("{\"theme\":\"Light\"}", File.ReadAllText(paths.SettingsFilePath));
        Assert.False(File.Exists(Path.Combine(paths.DataDirectory, "settings.json.tmp")));
        Assert.True(File.Exists(Path.Combine(oldData, "favorites.json"))); // the old copy is not touched

        // The user removes a favorite; starting the same old copy again must not bring it back.
        WriteFavorites(paths.FavoritesFilePath, (@"D:\Jobs", "Jobs"));
        var again = new AppPathsService(_install, _appData);
        Assert.Null(again.Migration);
        Assert.Single(ReadFavorites(again.FavoritesFilePath));
    }

    [Fact]
    public void FavoritesFromAnotherPortableCopyAreAddedNotReplaced()
    {
        string appDataData = Path.Combine(_appData, "Data");
        WriteFavorites(Path.Combine(appDataData, "favorites.json"), (@"D:\Jobs", "Jobs"));
        File.WriteAllText(Path.Combine(appDataData, "settings.json"), "{\"theme\":\"Dark\"}");

        string oldData = Path.Combine(_install, "Data");
        WriteFavorites(Path.Combine(oldData, "favorites.json"), (@"d:\jobs\", "Same folder"), (@"E:\Refs", "Refs"));
        File.WriteAllText(Path.Combine(oldData, "settings.json"), "{\"theme\":\"Light\"}");

        var paths = new AppPathsService(_install, _appData);

        Assert.Equal(1, paths.Migration!.FavoritesAdded);
        Assert.Empty(paths.Migration.CopiedFiles);
        Assert.Equal([@"D:\Jobs", @"E:\Refs"], ReadFavorites(paths.FavoritesFilePath).Select(f => f.Path));
        Assert.Equal("{\"theme\":\"Dark\"}", File.ReadAllText(paths.SettingsFilePath)); // AppData's own settings win
    }

    [Fact]
    public void ClearTempFilesKeepsDataTodaysLogAndTheRunningBuild()
    {
        var now = new DateTime(2026, 11, 25, 14, 0, 0);
        var paths = new AppPathsService(_install, _appData);
        string updateRoot = Path.Combine(_root, "Temp", "OrbitFileManager-update");
        string extraction = Path.Combine(_root, "Temp", ".net", "OrbitFileManager");
        string running = Path.Combine(extraction, "current");

        string[] removed =
        [
            Write(Path.Combine(paths.CacheDirectory, "thumbs", "a.bin"), 1000),
            Write(Path.Combine(updateRoot, "1.2.7", "OrbitFileManager.exe"), 5000),
            Write(Path.Combine(updateRoot, "apply-update.log"), 10),
            Write(Path.Combine(paths.LogsDirectory, "orbit-20261101.jsonl"), 300),
            Write(Path.Combine(paths.DataDirectory, "favorites.json.tmp"), 20, now.AddHours(-1)),
            Write(Path.Combine(extraction, "older", "wpfgfx_cor3.dll"), 4000),
        ];
        string[] kept =
        [
            Write(paths.FavoritesFilePath, 50),
            Write(paths.SettingsFilePath, 60),
            Write(Path.Combine(paths.DataDirectory, "sessions.json.bak"), 70),
            Write(Path.Combine(paths.DataDirectory, "sessions.json.tmp"), 80, now), // a save in progress
            Write(Path.Combine(paths.LogsDirectory, "orbit-20261125.jsonl"), 90),
            Write(Path.Combine(running, "wpfgfx_cor3.dll"), 4000),
        ];

        var temp = new TempFilesService(paths.CacheDirectory, paths.LogsDirectory, paths.DataDirectory, updateRoot, () => running, () => now);

        Assert.Equal(new TempUsage(10330, 6), temp.Measure());
        Assert.Equal(new TempUsage(10330, 6), temp.Clear());

        Assert.All(removed, file => Assert.False(File.Exists(file), file));
        Assert.All(kept, file => Assert.True(File.Exists(file), file));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.CacheDirectory));
        Assert.False(Directory.Exists(updateRoot));
        Assert.False(Directory.Exists(Path.Combine(extraction, "older")));
        Assert.Equal(new TempUsage(0, 0), temp.Measure());
    }

    [Fact]
    public void ClearTempFilesSkipsFilesInUse()
    {
        var paths = new AppPathsService(_install, _appData);
        string busy = Write(Path.Combine(paths.CacheDirectory, "busy.bin"), 100);
        string free = Write(Path.Combine(paths.CacheDirectory, "free.bin"), 200);
        var temp = new TempFilesService(paths.CacheDirectory, paths.LogsDirectory, paths.DataDirectory, Path.Combine(_root, "none"), () => null, () => DateTime.Now);

        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(new TempUsage(200, 1), temp.Clear());
        }

        Assert.True(File.Exists(busy));
        Assert.False(File.Exists(free));
    }

    private static string Write(string path, int bytes, DateTime? lastWrite = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (lastWrite is { } time)
            File.SetLastWriteTime(path, time);
        return path;
    }

    private static void WriteFavorites(string path, params (string Path, string Name)[] items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(items.Select(i => new FavoriteItem { Name = i.Name, Path = i.Path }).ToList(), Json));
    }

    private static List<FavoriteItem> ReadFavorites(string path) =>
        JsonSerializer.Deserialize<List<FavoriteItem>>(File.ReadAllText(path), Json)!;
}
