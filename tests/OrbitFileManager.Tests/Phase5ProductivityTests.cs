using OrbitFileManager.Services.Productivity;
using OrbitFileManager.Services.Settings;
using System.IO;
using System.Text;
using System.Windows.Input;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class Phase5ProductivityTests : IDisposable
{
    private readonly string _tempRoot;

    public Phase5ProductivityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitPhase5Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task ChecksumService_ComputesStandardTestVectorsAndVerifiesFormattedHashes()
    {
        string file = Path.Combine(_tempRoot, "nist_abc.bin");
        await File.WriteAllBytesAsync(file, Encoding.ASCII.GetBytes("abc"));

        var svc = new ChecksumService();
        FileChecksumResult res = await svc.ComputeChecksumsAsync(file);

        Assert.Equal(3, res.SizeBytes);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", res.Md5);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", res.Sha1);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", res.Sha256);

        var (matchUpper, algo) = ChecksumService.VerifyExpectedHash(
            res,
            "BA7816BF-8F01CFEA-414140DE-5DAE2223-B00361A3-96177A9C-B410FF61-F20015AD");
        Assert.True(matchUpper);
        Assert.Equal("SHA-256", algo);

        var (mismatch, _) = ChecksumService.VerifyExpectedHash(res, "00000000000000000000000000000000");
        Assert.False(mismatch);
    }

    [Fact]
    public async Task DuplicateFinderService_DistinguishesMiddleByteDifferencesInLargeFiles()
    {
        string subA = Path.Combine(_tempRoot, "DupScan", "FolderA");
        string subB = Path.Combine(_tempRoot, "DupScan", "FolderB");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);

        // 40 KB payload (> 16 KB edge threshold)
        byte[] basePayload = new byte[40 * 1024];
        new Random(42).NextBytes(basePayload);

        // Exact duplicate in FolderB
        string dup1 = Path.Combine(subA, "render_01.exr");
        string dup2 = Path.Combine(subB, "render_01_copy.exr");
        await File.WriteAllBytesAsync(dup1, basePayload);
        await File.WriteAllBytesAsync(dup2, basePayload);

        // Same size AND identical first 8 KB + last 8 KB, but 1 byte different in the middle (offset 20,000)!
        byte[] nearCollision = (byte[])basePayload.Clone();
        nearCollision[20_000] ^= 0xFF;
        string nonDup = Path.Combine(subB, "render_01_variant.exr");
        await File.WriteAllBytesAsync(nonDup, nearCollision);

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_app1"));
        var logger = new StructuredLogger(paths);
        var finder = new DuplicateFinderService(logger);

        DuplicateScanResult result = await finder.FindDuplicatesAsync(
            [Path.Combine(_tempRoot, "DupScan")],
            recursive: true);

        Assert.Equal(3, result.ScannedFileCount);
        Assert.Single(result.Groups);
        Assert.Equal(2, result.Groups[0].Files.Count);
        Assert.Equal(40 * 1024, result.TotalReclaimableBytes);
        Assert.DoesNotContain(result.Groups[0].Files, f => f.FullPath == nonDup);
    }

    [Fact]
    public async Task FolderSizeAnalyzerService_ComputesAccurateSubtreeTotalsAndPercentages()
    {
        string root = Path.Combine(_tempRoot, "DiskUsage");
        string heavyDir = Path.Combine(root, "HeavyTextures", "4K");
        string lightDir = Path.Combine(root, "Scripts");
        Directory.CreateDirectory(heavyDir);
        Directory.CreateDirectory(lightDir);

        await File.WriteAllBytesAsync(Path.Combine(heavyDir, "albedo.png"), new byte[8000]);
        await File.WriteAllBytesAsync(Path.Combine(lightDir, "init.cs"), new byte[1500]);
        await File.WriteAllBytesAsync(Path.Combine(root, "readme.md"), new byte[500]);

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_app2"));
        var logger = new StructuredLogger(paths);
        var analyzer = new FolderSizeAnalyzerService(logger);

        FolderSizeReport report = await analyzer.AnalyzeFolderAsync(root);

        Assert.Equal(10_000, report.TotalSizeBytes);
        Assert.Equal(3, report.TotalFiles);
        Assert.Equal(3, report.TotalSubfolders); // HeavyTextures, 4K, Scripts

        Assert.Equal(3, report.Items.Count);
        Assert.Equal("HeavyTextures", report.Items[0].Name);
        Assert.Equal(8000, report.Items[0].SizeBytes);
        Assert.Equal(0.80, report.Items[0].RatioOfParent, precision: 3);

        Assert.Equal("Scripts", report.Items[1].Name);
        Assert.Equal(1500, report.Items[1].SizeBytes);
    }

    [Fact]
    public void ShortcutService_PersistsCustomBindingsAndRejectsDuplicateGestures()
    {
        string appRoot = Path.Combine(_tempRoot, "_shortcutsApp");
        var paths = new AppPathsService(appRoot);
        var logger = new StructuredLogger(paths);
        var svc = new ShortcutService(paths, logger);

        // Default Ctrl+T -> NewTab
        Assert.Equal(OrbitShortcutAction.NewTab, svc.MatchShortcut(ModifierKeys.Control, Key.T));

        // Attempt to bind DuplicateFinder to Ctrl+T (already used by NewTab) -> must reject
        var (conflictOk, conflictErr) = svc.TryUpdateBinding(OrbitShortcutAction.DuplicateFinder, ModifierKeys.Control, Key.T);
        Assert.False(conflictOk);
        Assert.NotNull(conflictErr);

        // Bind DuplicateFinder to Ctrl+Alt+D -> succeeds and persists to Data/shortcuts.json
        var (ok, err) = svc.TryUpdateBinding(OrbitShortcutAction.DuplicateFinder, ModifierKeys.Control | ModifierKeys.Alt, Key.D);
        Assert.True(ok);
        Assert.Null(err);

        // Reload in a fresh ShortcutService instance
        var reloaded = new ShortcutService(paths, logger);
        Assert.Equal(OrbitShortcutAction.DuplicateFinder, reloaded.MatchShortcut(ModifierKeys.Control | ModifierKeys.Alt, Key.D));

        // Reset defaults
        reloaded.ResetToDefaults();
        Assert.Equal(OrbitShortcutAction.DuplicateFinder, reloaded.MatchShortcut(ModifierKeys.Control | ModifierKeys.Shift, Key.U));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }
}
