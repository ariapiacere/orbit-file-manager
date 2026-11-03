using OrbitFileManager.Models;
using OrbitFileManager.Services.FileOperations;
using OrbitFileManager.Services.Settings;
using System.IO;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class FileOperationServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileOperationService _service;

    public FileOperationServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitFileOpsTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_app"));
        var logger = new StructuredLogger(paths);
        _service = new FileOperationService(logger);
    }

    [Fact]
    public async Task CreateAndCaseOnlyRename_WorksForFilesAndDirectories()
    {
        var (okDir, dirPath, _) = await _service.CreateFolderAsync(_tempRoot, "render_output");
        Assert.True(okDir);
        Assert.NotNull(dirPath);

        // Case-only rename on Windows NTFS
        var (okCaseRename, newDirPath, err) = await _service.RenameAsync(dirPath!, "Render_Output");
        Assert.True(okCaseRename, err);
        Assert.Equal("Render_Output", new DirectoryInfo(newDirPath!).Name);

        // Duplicate creation should fail safely
        var (dupOk, _, dupErr) = await _service.CreateFolderAsync(_tempRoot, "Render_Output");
        Assert.False(dupOk);
        Assert.NotNull(dupErr);
    }

    [Fact]
    public async Task TransferItems_PreventsSelfSubfolderCopy()
    {
        string parent = Path.Combine(_tempRoot, "Project");
        string child = Path.Combine(parent, "Subfolder");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(parent, "readme.md"), "hello");

        var result = await _service.TransferItemsAsync(
            [parent],
            child,
            isMove: false,
            conflictPrompt: null);

        Assert.Equal(0, result.SucceededCount);
        Assert.Single(result.Errors);
        Assert.Contains("into itself", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransferItems_ConflictKeepBothAndOverwriteAndSkip_BehaveSafely()
    {
        string srcDir = Path.Combine(_tempRoot, "Src");
        string dstDir = Path.Combine(_tempRoot, "Dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        string srcFile = Path.Combine(srcDir, "data.json");
        string dstFile = Path.Combine(dstDir, "data.json");
        File.WriteAllText(srcFile, "NEW_CONTENT");
        File.WriteAllText(dstFile, "ORIGINAL_CONTENT");

        // 1. Skip preserves original destination
        var skipResult = await _service.TransferItemsAsync(
            [srcFile],
            dstDir,
            isMove: false,
            conflictPrompt: _ => Task.FromResult(new ConflictDecision(ConflictResolution.Skip, false)));

        Assert.Equal(0, skipResult.SucceededCount);
        Assert.Equal(1, skipResult.SkippedCount);
        Assert.Equal("ORIGINAL_CONTENT", File.ReadAllText(dstFile));

        // 2. KeepBoth creates data (2).json while preserving original
        var keepBothResult = await _service.TransferItemsAsync(
            [srcFile],
            dstDir,
            isMove: false,
            conflictPrompt: _ => Task.FromResult(new ConflictDecision(ConflictResolution.KeepBoth, false)));

        Assert.Equal(1, keepBothResult.SucceededCount);
        Assert.Equal("ORIGINAL_CONTENT", File.ReadAllText(dstFile));
        Assert.Equal("NEW_CONTENT", File.ReadAllText(Path.Combine(dstDir, "data (2).json")));

        // 3. Overwrite safely replaces destination
        var overwriteResult = await _service.TransferItemsAsync(
            [srcFile],
            dstDir,
            isMove: false,
            conflictPrompt: _ => Task.FromResult(new ConflictDecision(ConflictResolution.Overwrite, false)));

        Assert.Equal(1, overwriteResult.SucceededCount);
        Assert.Equal("NEW_CONTENT", File.ReadAllText(dstFile));
    }

    [Fact]
    public async Task TransferItems_Cancellation_DoesNotLeavePartialTempFiles()
    {
        string srcDir = Path.Combine(_tempRoot, "CancelSrc");
        string dstDir = Path.Combine(_tempRoot, "CancelDst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        string srcFile = Path.Combine(srcDir, "payload.bin");
        File.WriteAllBytes(srcFile, new byte[256 * 1024]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _service.TransferItemsAsync(
            [srcFile],
            dstDir,
            isMove: false,
            conflictPrompt: null,
            cancellationToken: cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Empty(Directory.GetFiles(dstDir));
    }

    [Fact]
    public async Task DeleteItems_PermanentDelete_HandlesReadOnlyFiles()
    {
        string roFile = Path.Combine(_tempRoot, "readonly.txt");
        File.WriteAllText(roFile, "protected");
        File.SetAttributes(roFile, FileAttributes.ReadOnly);

        var result = await _service.DeleteItemsAsync([roFile], permanent: true);

        Assert.True(result.IsFullSuccess);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal([roFile], result.RemovedPaths);
        Assert.False(File.Exists(roFile));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                foreach (string f in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }
}
