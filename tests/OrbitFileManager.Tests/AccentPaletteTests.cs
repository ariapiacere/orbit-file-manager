using OrbitFileManager.Models;
using OrbitFileManager.Services;
using OrbitFileManager.Services.Settings;
using System.IO;
using System.Windows.Media;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class AccentPaletteTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitAccentTests_" + Guid.NewGuid().ToString("N"));

    public static TheoryData<AppAccent, bool> CustomPresets()
    {
        var data = new TheoryData<AppAccent, bool>();
        foreach (AppAccent accent in Enum.GetValues<AppAccent>())
        {
            if (accent == AppAccent.Orange)
                continue; // hand-tuned in DarkTheme.xaml / LightTheme.xaml
            data.Add(accent, true);
            data.Add(accent, false);
        }
        return data;
    }

    [Fact]
    public void EveryAccentHasOnePalette()
    {
        foreach (AppAccent accent in Enum.GetValues<AppAccent>())
            Assert.Single(AccentPalettes.All, p => p.Accent == accent);
    }

    [Fact]
    public void SignalOrangeKeepsTheBaseThemes()
    {
        Assert.Empty(AccentPalettes.BuildOverrideValues(AppAccent.Orange, dark: true));
        Assert.Empty(AccentPalettes.BuildOverrideValues(AppAccent.Orange, dark: false));
    }

    [Theory]
    [MemberData(nameof(CustomPresets))]
    public void PresetOverridesEveryAccentBrush(AppAccent accent, bool dark)
    {
        var values = AccentPalettes.BuildOverrideValues(accent, dark);
        foreach (string key in AccentPalettes.AccentKeys)
            Assert.True(values.ContainsKey(key), $"{accent} {(dark ? "dark" : "light")} is missing {key}");

        // Dark presets bring their own greys; light mode keeps LightTheme.xaml's.
        Assert.Equal(dark, values.ContainsKey("BrowserBackgroundBrush"));
    }

    [Theory]
    [MemberData(nameof(CustomPresets))]
    public void PresetColoursStayReadable(AppAccent accent, bool dark)
    {
        var values = AccentPalettes.BuildOverrideValues(accent, dark);
        var accentColor = (Color)values["AccentBrush"];
        var ink = (Color)values["AccentForegroundBrush"];
        Color background = dark ? (Color)values["BrowserBackgroundBrush"] : Colors.White;

        // Button text on the accent, and the accent used for icons, rails and links on the list background.
        Assert.True(AccentPalettes.Contrast(ink, accentColor) >= 4.5, $"{accent}: button text contrast {AccentPalettes.Contrast(ink, accentColor):0.0}");
        Assert.True(AccentPalettes.Contrast(accentColor, background) >= 3.0, $"{accent}: accent contrast {AccentPalettes.Contrast(accentColor, background):0.0}");

        if (dark)
        {
            var text = (Color)values["TextPrimaryBrush"];
            Assert.True(AccentPalettes.Contrast(text, background) >= 12, $"{accent}: text contrast {AccentPalettes.Contrast(text, background):0.0}");
        }
    }

    [Fact]
    public void AccentChoiceIsSavedAndLoaded()
    {
        var paths = new AppPathsService(Path.Combine(_tempRoot, "_app"));
        var logger = new StructuredLogger(paths);
        var settings = new SettingsService(paths, logger);
        settings.Load();
        Assert.Equal(AppAccent.Orange, settings.Current.Accent);

        settings.Current.Accent = AppAccent.Blue;
        settings.Save();

        var reloaded = new SettingsService(paths, logger);
        reloaded.Load();
        Assert.Equal(AppAccent.Blue, reloaded.Current.Accent);
        Assert.Contains("\"Blue\"", File.ReadAllText(paths.SettingsFilePath));
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
