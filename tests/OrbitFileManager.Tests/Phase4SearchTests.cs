using OrbitFileManager.Services.FileSystem;
using OrbitFileManager.Services.Search;
using OrbitFileManager.Services.Settings;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class Phase4SearchTests : IDisposable
{
    private readonly string _tempRoot;

    public Phase4SearchTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"OrbitPhase4Search_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void SearchQueryParser_EvaluatesAllSpecificationQueriesCorrectly()
    {
        DateTime now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        var pngHero = new SearchCandidate("Character_Main.png", ".png", "C:\\a.png", false, false, 4 * 1024 * 1024, now.AddDays(-2), now.AddDays(-1));
        var backupPng = new SearchCandidate("character_backup.png", ".png", "C:\\b.png", false, false, 512, now.AddDays(-30), now.AddDays(-20));
        var bigExr = new SearchCandidate("final_render_01.exr", ".exr", "C:\\r.exr", false, false, 150L * 1024 * 1024, now.AddDays(-1), now.AddHours(-3));
        var oldBlend = new SearchCandidate("rig.blend", ".blend", "C:\\rig.blend", false, false, 20 * 1024 * 1024, now.AddDays(-60), now.AddDays(-45));

        // 1. *.png
        var (q1, _) = SearchQueryParser.TryParse("*.png");
        Assert.True(q1!.Matches(in pngHero, false, now));
        Assert.False(q1.Matches(in bigExr, false, now));

        // 2. *.blend
        var (q2, _) = SearchQueryParser.TryParse("*.blend");
        Assert.True(q2!.Matches(in oldBlend, false, now));

        // 3. name:render AND ext:exr
        var (q3, _) = SearchQueryParser.TryParse("name:render AND ext:exr");
        Assert.True(q3!.Matches(in bigExr, false, now));
        Assert.False(q3.Matches(in pngHero, false, now));

        // 4. ext:png OR ext:jpg
        var (q4, _) = SearchQueryParser.TryParse("ext:png OR ext:jpg");
        Assert.True(q4!.Matches(in pngHero, false, now));
        Assert.False(q4.Matches(in oldBlend, false, now));

        // 5. size:>100MB
        var (q5, _) = SearchQueryParser.TryParse("size:>100MB");
        Assert.True(q5!.Matches(in bigExr, false, now));
        Assert.False(q5.Matches(in pngHero, false, now));

        // 6. modified:<7d
        var (q6, _) = SearchQueryParser.TryParse("modified:<7d");
        Assert.True(q6!.Matches(in pngHero, false, now));
        Assert.False(q6.Matches(in oldBlend, false, now));

        // 7. name:character NOT name:backup
        var (q7, _) = SearchQueryParser.TryParse("name:character NOT name:backup");
        Assert.True(q7!.Matches(in pngHero, false, now));
        Assert.False(q7.Matches(in backupPng, false, now));
    }

    [Fact]
    public void SearchQueryParser_EnforcesRegexTimeoutOnCatastrophicBacktracking()
    {
        var (node, err) = SearchQueryParser.TryParse(@"regex:^([a-z]+)+\1$", TimeSpan.FromMilliseconds(15));
        Assert.Null(err);
        Assert.NotNull(node);

        string evilName = new string('a', 34) + "b";
        var candidate = new SearchCandidate(evilName, ".txt", "C:\\evil.txt", false, false, 10, DateTime.UtcNow, DateTime.UtcNow);

        Assert.Throws<RegexMatchTimeoutException>(() => node!.Matches(in candidate, false, DateTime.UtcNow));
    }

    [Fact]
    public async Task FileSearchService_StreamsIncrementalResultsRecursively()
    {
        string sub = Path.Combine(_tempRoot, "Shots", "Sc07");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_tempRoot, "readme.txt"), "doc");
        File.WriteAllText(Path.Combine(sub, "render_pass.exr"), "exr1");
        File.WriteAllText(Path.Combine(sub, "render_backup.exr"), "exr2");

        var paths = new AppPathsService(Path.Combine(_tempRoot, "_app"));
        var logger = new StructuredLogger(paths);
        var svc = new FileSearchService(paths, new ShellIntegrationService(), logger);

        var collected = new List<string>();
        var (count, err) = await svc.ExecuteSearchAsync(
            new SearchRequest
            {
                QueryText = "name:render NOT name:backup",
                RootLocations = [_tempRoot],
                Recursive = true
            },
            batch => collected.AddRange(batch.Select(b => b.Name)),
            CancellationToken.None);

        Assert.Null(err);
        Assert.Equal(1, count);
        Assert.Equal(["render_pass.exr"], collected);
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
