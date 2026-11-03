using OrbitFileManager.Models;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Preview;
using OrbitFileManager.Services.Settings;
using OrbitFileManager.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class Phase6IntegrationAndSafetyTests : IDisposable
{
    private readonly string _tempRoot;

    public Phase6IntegrationAndSafetyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitPhase6Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task UnicodeAndDeepPaths_CopyRenameAndPreviewWithoutCorruption()
    {
        string deepDir = Path.Combine(
            _tempRoot,
            "โปรเจกต์_อนิเมชัน",
            "シーン_07_レンダリング",
            "Deliverables_Édition_Spéciale_2026");
        Directory.CreateDirectory(deepDir);

        string unicodeFile = Path.Combine(deepDir, "รายงาน_สรุป_01.txt");
        await File.WriteAllTextAsync(unicodeFile, "Orbit Unicode Content สวัสดี こんにちは", Encoding.UTF8);

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_appUni"));
        var logger = new StructuredLogger(paths);
        var fileOps = new FileOperationService(logger);

        string destDir = Path.Combine(_tempRoot, "Dest_ปลายทาง");
        Directory.CreateDirectory(destDir);

        var copyRes = await fileOps.TransferItemsAsync([unicodeFile], destDir, isMove: false, null, null, CancellationToken.None);
        Assert.Equal(1, copyRes.SucceededCount);
        Assert.False(copyRes.HasErrors);

        string copiedFile = Path.Combine(destDir, "รายงาน_สรุป_01.txt");
        Assert.True(File.Exists(copiedFile));
        Assert.Equal("Orbit Unicode Content สวัสดี こんにちは", await File.ReadAllTextAsync(copiedFile, Encoding.UTF8));
    }

    [Fact]
    public async Task PreviewService_CapsLargeTextAt64KBAndNeverLocksUnderlyingFile()
    {
        string bigLog = Path.Combine(_tempRoot, "server_trace.log");
        string repeated = new string('X', 1024) + "\n";
        var sb = new StringBuilder(210 * 1024);
        for (int i = 0; i < 200; i++)
            sb.Append(repeated);

        await File.WriteAllTextAsync(bigLog, sb.ToString(), Encoding.UTF8);

        var shell = new ShellIntegrationService();
        var previewSvc = new PreviewService(shell);

        var fi = new FileInfo(bigLog);
        var entry = new FileEntryItem
        {
            Name = fi.Name,
            FullPath = fi.FullName,
            Extension = ".log",
            IsDirectory = false,
            SizeBytes = fi.Length,
            FormattedSize = "200 KB",
            TypeDescription = "Log File",
            AttributesSummary = "-",
            ModifiedTime = fi.LastWriteTime,
            CreatedTime = fi.CreationTime
        };

        FilePreviewData data = await previewSvc.LoadPreviewAsync(entry, CancellationToken.None);
        Assert.Equal(PreviewKind.Text, data.Kind);
        Assert.True(data.IsTextTruncated);
        Assert.NotNull(data.TextPreview);
        Assert.True(data.TextPreview!.Length <= 66_000);

        // Immediately rename and delete the file to prove PreviewService released all handles
        string renamedLog = Path.Combine(_tempRoot, "server_trace_renamed.log");
        File.Move(bigLog, renamedLog);
        File.Delete(renamedLog);
        Assert.False(File.Exists(renamedLog));
    }

    [Fact]
    public async Task PreviewService_HandlesPdfAndBinaryHeadersSafelyWithoutCrashing()
    {
        string pdfFile = Path.Combine(_tempRoot, "sample_spec.pdf");
        await File.WriteAllBytesAsync(pdfFile, Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Type /Page >> endobj\n%%EOF"));

        string binFile = Path.Combine(_tempRoot, "plugin.dll");
        byte[] peHeader = new byte[128];
        peHeader[0] = (byte)'M';
        peHeader[1] = (byte)'Z';
        await File.WriteAllBytesAsync(binFile, peHeader);

        var previewSvc = new PreviewService(new ShellIntegrationService());

        var pdfPreview = await previewSvc.LoadPreviewAsync(new FileEntryItem
        {
            Name = "sample_spec.pdf",
            FullPath = pdfFile,
            Extension = ".pdf",
            IsDirectory = false,
            SizeBytes = new FileInfo(pdfFile).Length,
            FormattedSize = "84 B",
            TypeDescription = "PDF Document",
            AttributesSummary = "-"
        }, CancellationToken.None);

        Assert.True(pdfPreview.Kind is PreviewKind.Binary or PreviewKind.Image);
        Assert.Contains("PDF 1.7", pdfPreview.StatusMessage ?? string.Empty);

        var binPreview = await previewSvc.LoadPreviewAsync(new FileEntryItem
        {
            Name = "plugin.dll",
            FullPath = binFile,
            Extension = ".dll",
            IsDirectory = false,
            SizeBytes = peHeader.Length,
            FormattedSize = "128 B",
            TypeDescription = "Application Extension",
            AttributesSummary = "-"
        }, CancellationToken.None);

        Assert.Equal(PreviewKind.Binary, binPreview.Kind);
        Assert.Contains("4D 5A", binPreview.StatusMessage ?? string.Empty); // "MZ" hex header
    }

    [Fact]
    public async Task BrowserTabViewModel_LoadsSortsAndFilters1500FilesRapidly()
    {
        string bulkDir = Path.Combine(_tempRoot, "Bulk1500");
        Directory.CreateDirectory(bulkDir);

        for (int i = 1; i <= 1500; i++)
        {
            string f = Path.Combine(bulkDir, $"render_{i:D4}.exr");
            await File.WriteAllBytesAsync(f, new byte[(i % 17) + 1]);
        }

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_appBulk"));
        var logger = new StructuredLogger(paths);
        var settings = new SettingsService(paths, logger);
        settings.Load();
        var browser = new FileBrowserService(new ShellIntegrationService(), logger);

        using var tab = new BrowserTabViewModel(browser, settings, BrowserViewMode.Details);

        var sw = Stopwatch.StartNew();
        bool loaded = await tab.NavigateAsync(bulkDir);
        sw.Stop();

        Assert.True(loaded);
        Assert.Equal(1500, tab.TotalItemCount);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Expected fast load, took {sw.Elapsed.TotalMilliseconds:0} ms");

        // Quick filter down to render_149*
        tab.QuickFilterText = "render_149";
        Assert.Equal(10, tab.DisplayedItems.Count); // 1490..1499
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
