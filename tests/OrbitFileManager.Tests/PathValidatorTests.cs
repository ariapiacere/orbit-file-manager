using OrbitFileManager.Utilities;
using System.IO;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class PathValidatorTests : IDisposable
{
    private readonly string _tempRoot;

    public PathValidatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitPathTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Theory]
    [InlineData("Report_2026.pdf")]
    [InlineData("日本語_ファイル_01.png")]
    [InlineData("My Project Folder")]
    [InlineData(".gitignore")]
    public void ValidateEntryName_AcceptsValidNames(string validName)
    {
        var (isValid, error) = PathValidator.ValidateEntryName(validName);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("bad:name.txt")]
    [InlineData("bad*name.txt")]
    [InlineData("bad?name.txt")]
    [InlineData("sub\\folder")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL.log")]
    [InlineData("COM1")]
    [InlineData("LPT9.dat")]
    public void ValidateEntryName_RejectsInvalidWindowsNames(string invalidName)
    {
        var (isValid, error) = PathValidator.ValidateEntryName(invalidName);
        Assert.False(isValid);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void IsSameOrSubPath_DetectsSelfAndSubdirectoriesWithoutFalsePrefixMatch()
    {
        string source = @"C:\Workspace\ProjectAlpha";

        Assert.True(PathValidator.IsSameOrSubPath(source, @"C:\Workspace\ProjectAlpha"));
        Assert.True(PathValidator.IsSameOrSubPath(source, @"c:\workspace\projectalpha\Assets\Textures"));

        // Sibling directory sharing string prefix must NOT be treated as a subpath
        Assert.False(PathValidator.IsSameOrSubPath(source, @"C:\Workspace\ProjectAlphaBackup"));
        Assert.False(PathValidator.IsSameOrSubPath(source, @"C:\Workspace\Other"));
    }

    [Fact]
    public void BuildBreadcrumbs_ParsesLocalAndUncPathsCorrectly()
    {
        var localSegments = PathValidator.BuildBreadcrumbs(@"C:\Users\Explorer\Projects");
        Assert.Equal(4, localSegments.Count);
        Assert.Equal("C:", localSegments[0].Label);
        Assert.Equal(@"C:\", localSegments[0].FullPath);
        Assert.False(localSegments[0].IsLast);
        Assert.Equal("Projects", localSegments[3].Label);
        Assert.Equal(@"C:\Users\Explorer\Projects", localSegments[3].FullPath);
        Assert.True(localSegments[3].IsLast);

        var uncSegments = PathValidator.BuildBreadcrumbs(@"\\StudioNas\Projects\Shot01");
        Assert.Equal(2, uncSegments.Count);
        Assert.Equal(@"\\StudioNas\Projects", uncSegments[0].Label);
        Assert.Equal("Shot01", uncSegments[1].Label);
        Assert.True(uncSegments[1].IsLast);
    }

    [Fact]
    public void GenerateUniqueDestinationPath_GeneratesSequentialAndCopyNames()
    {
        string existing = Path.Combine(_tempRoot, "notes.txt");
        File.WriteAllText(existing, "a");

        string keepBoth1 = PathValidator.GenerateUniqueDestinationPath(_tempRoot, "notes.txt", isDirectory: false, useCopyLabel: false);
        Assert.Equal(Path.Combine(_tempRoot, "notes (2).txt"), keepBoth1);
        File.WriteAllText(keepBoth1, "b");

        string keepBoth2 = PathValidator.GenerateUniqueDestinationPath(_tempRoot, "notes.txt", isDirectory: false, useCopyLabel: false);
        Assert.Equal(Path.Combine(_tempRoot, "notes (3).txt"), keepBoth2);

        string sameDirCopy1 = PathValidator.GenerateUniqueDestinationPath(_tempRoot, "notes.txt", isDirectory: false, useCopyLabel: true);
        Assert.Equal(Path.Combine(_tempRoot, "notes - Copy.txt"), sameDirCopy1);
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
