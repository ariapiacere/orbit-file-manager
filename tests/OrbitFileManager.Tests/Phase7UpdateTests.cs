using OrbitFileManager.Services.Updates;
using System.Text.Json;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class Phase7UpdateTests
{
    private static JsonElement Release(string tag, bool withChecksum = true, bool prerelease = false)
    {
        string version = tag.TrimStart('v');
        string checksumAsset = withChecksum
            ? $$""", { "name": "OrbitFileManager-win-x64-{{version}}.zip.sha256", "size": 90, "browser_download_url": "https://example.test/pkg.zip.sha256" }"""
            : string.Empty;
        string json = $$"""
            {
              "tag_name": "{{tag}}",
              "draft": false,
              "prerelease": {{(prerelease ? "true" : "false")}},
              "html_url": "https://example.test/release",
              "body": "Faster recycle bin.",
              "assets": [
                { "name": "OrbitFileManager-win-x64-{{version}}.zip", "size": 70000000, "browser_download_url": "https://example.test/pkg.zip" }{{checksumAsset}}
              ]
            }
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void ParseLatestRelease_NewerVersionWithAssets_ReturnsUpdate()
    {
        UpdateInfo? update = UpdateService.ParseLatestRelease(Release("v1.2.0"), new Version(1, 1, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 2, 0), update!.Version);
        Assert.Equal("https://example.test/pkg.zip", update.PackageUrl);
        Assert.Equal("https://example.test/pkg.zip.sha256", update.ChecksumUrl);
        Assert.Equal(70000000, update.PackageSize);
        Assert.Equal("Faster recycle bin.", update.Notes);
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.9")]
    public void ParseLatestRelease_SameOrOlderVersion_ReturnsNull(string tag)
    {
        Assert.Null(UpdateService.ParseLatestRelease(Release(tag), new Version(1, 1, 0)));
    }

    [Fact]
    public void ParseLatestRelease_MissingChecksum_ReturnsNull()
    {
        Assert.Null(UpdateService.ParseLatestRelease(Release("v1.2.0", withChecksum: false), new Version(1, 1, 0)));
    }

    [Fact]
    public void ParseLatestRelease_Prerelease_ReturnsNull()
    {
        Assert.Null(UpdateService.ParseLatestRelease(Release("v1.2.0", prerelease: true), new Version(1, 1, 0)));
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.4", 1, 4, 0)]
    [InlineData("V2.0.1", 2, 0, 1)]
    public void TryParseVersion_AcceptsReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out Version? version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void TryParseVersion_RejectsNonVersionTags()
    {
        Assert.False(UpdateService.TryParseVersion("latest", out _));
    }

    [Theory]
    [InlineData("https://github.com/ariapiacere/orbit-file-manager/releases/tag/v1.1.1", "v1.1.1")]
    [InlineData("/ariapiacere/orbit-file-manager/releases/tag/v2.0.0/", "v2.0.0")]
    public void TryParseReleaseTag_ReadsTagFromLatestRedirect(string location, string expected)
    {
        Assert.True(UpdateService.TryParseReleaseTag(new Uri(location, UriKind.RelativeOrAbsolute), out string? tag));
        Assert.Equal(expected, tag);
    }

    [Theory]
    [InlineData("https://github.com/ariapiacere/orbit-file-manager/releases")]
    [InlineData("https://github.com/ariapiacere/orbit-file-manager/releases/tag/")]
    public void TryParseReleaseTag_NoTag_ReturnsFalse(string location)
    {
        Assert.False(UpdateService.TryParseReleaseTag(new Uri(location), out _));
        Assert.False(UpdateService.TryParseReleaseTag(null, out _));
    }

    [Fact]
    public void FromReleaseTag_NewerVersion_UsesFixedDownloadUrls()
    {
        UpdateInfo? update = UpdateService.FromReleaseTag("v1.2.0", new Version(1, 1, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 2, 0), update!.Version);
        Assert.Equal("https://github.com/ariapiacere/orbit-file-manager/releases/download/v1.2.0/OrbitFileManager-win-x64-1.2.0.zip", update.PackageUrl);
        Assert.Equal(update.PackageUrl + ".sha256", update.ChecksumUrl);
        Assert.Equal("https://github.com/ariapiacere/orbit-file-manager/releases/tag/v1.2.0", update.ReleasePageUrl);
        Assert.Equal(0, update.PackageSize);
        Assert.False(string.IsNullOrWhiteSpace(update.Notes));
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.9")]
    [InlineData("latest")]
    public void FromReleaseTag_SameOlderOrInvalid_ReturnsNull(string tag)
    {
        Assert.Null(UpdateService.FromReleaseTag(tag, new Version(1, 1, 0)));
    }
}
