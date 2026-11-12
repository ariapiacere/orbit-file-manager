using OrbitFileManager.Services.Updates;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace OrbitFileManager.Tests;

/// <summary>
/// 1.2.0 ships as one OrbitFileManager.exe. Older installs are a folder of ~470 files whose updater copies the new exe
/// over the old one; the new build then removes the leftovers. Later updates replace the single exe in place.
/// The install scripts are run for real here (without restarting anything).
/// </summary>
public sealed class SingleFileUpdateTests : IDisposable
{
    private readonly string _tempRoot;

    public SingleFileUpdateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitSingleFileTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    private static void Write(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ─── Leftovers of the folder install ──────────────────────────────────────

    [Fact]
    public void LegacyManifest_ListsTheOldFolderInstallButNeverTheExeOrUserData()
    {
        IReadOnlyList<string> manifest = InstallLayoutService.LoadLegacyManifest();

        Assert.True(manifest.Count > 400, $"Only {manifest.Count} entries.");
        Assert.Contains("OrbitFileManager.dll", manifest);
        Assert.Contains("OrbitFileManager.runtimeconfig.json", manifest);
        Assert.Contains("coreclr.dll", manifest);
        Assert.Contains(@"zh-Hant\PresentationFramework.resources.dll", manifest);
        Assert.DoesNotContain("OrbitFileManager.exe", manifest, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(manifest, e => e.StartsWith("Data", StringComparison.OrdinalIgnoreCase)
                                          || e.StartsWith("Logs", StringComparison.OrdinalIgnoreCase)
                                          || e.StartsWith("Cache", StringComparison.OrdinalIgnoreCase)
                                          || e.StartsWith('#'));
    }

    [Fact]
    public void RemoveLegacyFolderFiles_DeletesOnlyListedFilesOfAnOldInstall()
    {
        string install = Path.Combine(_tempRoot, "Orbit");
        string[] manifest =
        [
            "OrbitFileManager.dll", "coreclr.dll", "OrbitFileManager.deps.json",
            @"de\PresentationCore.resources.dll", @"ja\PresentationCore.resources.dll",
            // Never honoured, even if the list were tampered with:
            "OrbitFileManager.exe", @"Data\settings.json", @"..\outside.txt", @"C:\Windows\win.ini"
        ];
        foreach (string file in new[] { "OrbitFileManager.exe", "OrbitFileManager.dll", "coreclr.dll", "OrbitFileManager.deps.json",
                                        @"de\PresentationCore.resources.dll", @"ja\PresentationCore.resources.dll", @"ja\my-notes.txt",
                                        @"Data\settings.json", @"Data\sessions.json", @"Logs\orbit.jsonl", "README-mine.txt" })
        {
            Write(Path.Combine(install, file));
        }
        Write(Path.Combine(_tempRoot, "outside.txt"));
        File.SetAttributes(Path.Combine(install, "coreclr.dll"), FileAttributes.ReadOnly);

        int removed = InstallLayoutService.RemoveLegacyFolderFiles(install, manifest, out int failed);

        Assert.Equal(5, removed);
        Assert.Equal(0, failed);
        Assert.False(File.Exists(Path.Combine(install, "OrbitFileManager.dll")));
        Assert.False(File.Exists(Path.Combine(install, "coreclr.dll")));
        Assert.False(Directory.Exists(Path.Combine(install, "de")));                 // emptied → removed
        Assert.True(File.Exists(Path.Combine(install, "ja", "my-notes.txt")));       // not ours → kept with its folder
        Assert.True(File.Exists(Path.Combine(install, "OrbitFileManager.exe")));
        Assert.True(File.Exists(Path.Combine(install, "Data", "settings.json")));
        Assert.True(File.Exists(Path.Combine(install, "Data", "sessions.json")));
        Assert.True(File.Exists(Path.Combine(install, "Logs", "orbit.jsonl")));
        Assert.True(File.Exists(Path.Combine(install, "README-mine.txt")));
        Assert.True(File.Exists(Path.Combine(_tempRoot, "outside.txt")));
    }

    [Fact]
    public void RemoveLegacyFolderFiles_DoesNothingWithoutTheOldInstallMarker()
    {
        // A single exe dropped into some other folder (Downloads, Desktop) must never delete anything there.
        string folder = Path.Combine(_tempRoot, "Downloads");
        Write(Path.Combine(folder, "OrbitFileManager.exe"));
        Write(Path.Combine(folder, "coreclr.dll"));
        Write(Path.Combine(folder, "hostfxr.dll"));

        int removed = InstallLayoutService.RemoveLegacyFolderFiles(folder, InstallLayoutService.LoadLegacyManifest(), out _);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(folder, "coreclr.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "hostfxr.dll")));
    }

    [Fact]
    public void RemoveStaleNativeExtractions_KeepsTheCurrentAndInUseFolders()
    {
        string parent = Path.Combine(_tempRoot, ".net", "OrbitFileManager");
        string current = Path.Combine(parent, "current-id");
        string old = Path.Combine(parent, "old-id");
        string inUse = Path.Combine(parent, "in-use-id");
        Write(Path.Combine(current, "wpfgfx_cor3.dll"));
        Write(Path.Combine(old, "wpfgfx_cor3.dll"));
        Write(Path.Combine(inUse, "wpfgfx_cor3.dll"));

        int removed;
        using (new FileStream(Path.Combine(inUse, "wpfgfx_cor3.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            removed = InstallLayoutService.RemoveStaleNativeExtractions(current);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(current));
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(Path.Combine(inUse, "wpfgfx_cor3.dll")));
        Assert.Equal(2, Directory.GetDirectories(parent).Length); // nothing half-renamed left behind
    }

    // ─── Installing an update ─────────────────────────────────────────────────

    [Fact]
    public void IsSingleFilePackage_RecognisesTheExeAlone()
    {
        string single = Path.Combine(_tempRoot, "single");
        Write(Path.Combine(single, "OrbitFileManager.exe"));
        string folder = Path.Combine(_tempRoot, "folder");
        Write(Path.Combine(folder, "OrbitFileManager.exe"));
        Write(Path.Combine(folder, "OrbitFileManager.dll"));

        Assert.True(UpdateService.IsSingleFilePackage(single));
        Assert.False(UpdateService.IsSingleFilePackage(folder));
    }

    private (UpdateService.InstallScriptPlan Plan, string Script) PrepareScript(
        string stagedDir, string installDir, string exePath, int waitForPid, bool singleFile)
    {
        var plan = new UpdateService.InstallScriptPlan(
            stagedDir, installDir, exePath, waitForPid, "9.9.9",
            ResultFilePath: Path.Combine(_tempRoot, "result.txt"),
            LogFilePath: Path.Combine(_tempRoot, "apply.log"),
            Relaunch: false);
        string script = Path.Combine(_tempRoot, "apply-update.cmd");
        File.WriteAllText(script, UpdateService.BuildInstallScript(plan, singleFile), new UTF8Encoding(false));
        return (plan, script);
    }

    private static void RunScript(string script, TimeSpan timeout)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c \"\"{script}\"\"",
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        Assert.True(process.WaitForExit(timeout), "The install script did not finish.");
    }

    private static int NoSuchProcess() => int.MaxValue - 3;

    [Theory]
    [InlineData("OrbitFileManager.exe")]
    [InlineData("Orbit (renamed).exe")]
    public void SingleFileScript_ReplacesTheRunningExeEvenWithAwkwardPaths(string exeName)
    {
        string staged = Path.Combine(_tempRoot, "staged", "files");
        Write(Path.Combine(staged, "OrbitFileManager.exe"), "new build");
        string install = Path.Combine(_tempRoot, "โปรแกรม 100% & (Orbit)");
        string exe = Path.Combine(install, exeName);
        Write(exe, "old build");
        Write(Path.Combine(install, "Data", "sessions.json"), "tabs");

        var (plan, script) = PrepareScript(staged, install, exe, NoSuchProcess(), singleFile: true);
        RunScript(script, TimeSpan.FromSeconds(30));

        Assert.Equal("new build", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
        Assert.Equal("tabs", File.ReadAllText(Path.Combine(install, "Data", "sessions.json")));
        Assert.Equal("ok 9.9.9", File.ReadAllText(plan.ResultFilePath).Trim());
        Assert.Equal(2, Directory.GetFileSystemEntries(install).Length); // the exe and Data, nothing added
    }

    [Fact]
    public void SingleFileScript_WaitsForOrbitToExitAndRetriesALockedExe()
    {
        string staged = Path.Combine(_tempRoot, "staged");
        Write(Path.Combine(staged, "OrbitFileManager.exe"), "new build");
        string install = Path.Combine(_tempRoot, "Orbit");
        string exe = Path.Combine(install, "OrbitFileManager.exe");
        Write(exe, "old build");

        // Stands in for Orbit: exits after about 2 seconds.
        using Process orbit = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
            Arguments = "-n 3 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        var (plan, script) = PrepareScript(staged, install, exe, orbit.Id, singleFile: true);

        // Something (a virus scanner) still holds the exe for a few seconds after Orbit exits.
        var watch = Stopwatch.StartNew();
        using (var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using Process runner = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/c \"\"{script}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            Assert.True(orbit.WaitForExit(10_000));
            Thread.Sleep(2500);
            Assert.Equal("old build", File.ReadAllText(exe)); // not replaced while locked
            hold.Dispose();
            Assert.True(runner.WaitForExit(30_000), "The install script did not finish.");
        }

        Assert.Equal("new build", File.ReadAllText(exe));
        Assert.Equal("ok 9.9.9", File.ReadAllText(plan.ResultFilePath).Trim());
        Assert.Contains("Replacing", File.ReadAllText(plan.LogFilePath));
    }

    [Fact]
    public void SingleFileScript_ReportsFailureAndKeepsTheOldExeWhenItStaysLocked()
    {
        string staged = Path.Combine(_tempRoot, "staged");
        Write(Path.Combine(staged, "OrbitFileManager.exe"), "new build");
        string install = Path.Combine(_tempRoot, "Orbit");
        string exe = Path.Combine(install, "OrbitFileManager.exe");
        Write(exe, "old build");

        var (plan, script) = PrepareScript(staged, install, exe, NoSuchProcess(), singleFile: true);
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            RunScript(script, TimeSpan.FromSeconds(60));

        Assert.Equal("old build", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
        Assert.Equal("failed copy 9.9.9", File.ReadAllText(plan.ResultFilePath).Trim());
    }

    [Fact]
    public void FolderScript_CopiesTheBuildAndKeepsUserData()
    {
        string staged = Path.Combine(_tempRoot, "staged");
        Write(Path.Combine(staged, "OrbitFileManager.exe"), "new exe");
        Write(Path.Combine(staged, "OrbitFileManager.dll"), "new dll");
        Write(Path.Combine(staged, "Data", "settings.json"), "defaults");
        string install = Path.Combine(_tempRoot, "Orbit folder");
        Write(Path.Combine(install, "OrbitFileManager.exe"), "old exe");
        Write(Path.Combine(install, "Data", "settings.json"), "mine");

        var (plan, script) = PrepareScript(staged, install, Path.Combine(install, "OrbitFileManager.exe"), NoSuchProcess(), singleFile: false);
        RunScript(script, TimeSpan.FromSeconds(60));

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, "OrbitFileManager.exe")));
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(install, "OrbitFileManager.dll")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(install, "Data", "settings.json")));
        Assert.Equal("ok 9.9.9", File.ReadAllText(plan.ResultFilePath).Trim());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                foreach (string file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
