using OrbitFileManager.Models;
using System.Windows;
using System.Windows.Media;

namespace OrbitFileManager.Services;

/// <summary>
/// An accent preset. <see cref="DarkAccent"/> and <see cref="LightAccent"/> are the base colours; every other accent
/// brush (hover, selection, tab glow, button ink) is derived from them. In dark mode a preset can also bring its own
/// greys (<see cref="DarkNeutrals"/>) so the accent sits in a matching base, the way Signal Orange sits on warm graphite.
/// </summary>
public sealed record AccentPalette(
    AppAccent Accent,
    string Name,
    Color DarkAccent,
    Color LightAccent,
    IReadOnlyDictionary<string, Color>? DarkNeutrals);

public static class AccentPalettes
{
    // Brush keys an accent preset overrides on top of DarkTheme.xaml / LightTheme.xaml.
    public static readonly string[] AccentKeys =
    [
        "AccentBrush", "AccentHoverBrush", "AccentForegroundBrush", "AccentSubtleBrush",
        "SelectedRowBackgroundBrush", "SelectedRowBorderBrush", "ActiveTabGlowBrush", "ActiveTabGlowBorderBrush"
    ];

    // Warm graphite = the greys in DarkTheme.xaml; listed here only to derive colours from them.
    private static readonly IReadOnlyDictionary<string, Color> WarmGraphite = Neutrals(
        window: "151413", toolbar: "1B1A18", sidebar: "181715", browser: "121110", status: "100F0E", elevated: "221F1C",
        input: "0E0D0C", borderSubtle: "2A2724", borderStrong: "3B3732", hover: "24211E", activeTab: "26221F",
        textPrimary: "F4EFE9", textSecondary: "A8A29E", textMuted: "78716C", thumb: "3A3631", thumbHover: "57514A");

    private static readonly IReadOnlyDictionary<string, Color> CoolSlate = Neutrals(
        window: "131518", toolbar: "191C20", sidebar: "16181C", browser: "101215", status: "0E1013", elevated: "1F2329",
        input: "0C0E10", borderSubtle: "262A31", borderStrong: "363B44", hover: "21252B", activeTab: "23272E",
        textPrimary: "EEF1F5", textSecondary: "A1A8B3", textMuted: "6E7683", thumb: "343A43", thumbHover: "4F5763");

    private static readonly IReadOnlyDictionary<string, Color> DeepLagoon = Neutrals(
        window: "121616", toolbar: "181D1D", sidebar: "151A1A", browser: "0F1313", status: "0D1111", elevated: "1E2424",
        input: "0B0E0E", borderSubtle: "252C2C", borderStrong: "343D3D", hover: "1F2626", activeTab: "212828",
        textPrimary: "EDF3F2", textSecondary: "9FAAA8", textMuted: "6C7876", thumb: "323B3B", thumbHover: "4C5857");

    private static readonly IReadOnlyDictionary<string, Color> DuskPlum = Neutrals(
        window: "151319", toolbar: "1B1920", sidebar: "18161C", browser: "121015", status: "100E13", elevated: "221F29",
        input: "0E0C10", borderSubtle: "2A2631", borderStrong: "3B3644", hover: "24212B", activeTab: "26222E",
        textPrimary: "F3F0F7", textSecondary: "A7A1B3", textMuted: "767083", thumb: "3A3543", thumbHover: "565063");

    private static readonly IReadOnlyDictionary<string, Color> NeutralZinc = Neutrals(
        window: "141415", toolbar: "1A1A1C", sidebar: "171719", browser: "111112", status: "0F0F10", elevated: "202023",
        input: "0D0D0E", borderSubtle: "28282B", borderStrong: "38383C", hover: "222225", activeTab: "242427",
        textPrimary: "F2F2F3", textSecondary: "A3A3AA", textMuted: "71717A", thumb: "36363A", thumbHover: "525258");

    // Light mode keeps the zinc greys of LightTheme.xaml for every preset.
    private static readonly Color LightSurface = Colors.White;
    private static readonly Color LightBorderStrong = Hex("D4D4D8");

    public static IReadOnlyList<AccentPalette> All { get; } =
    [
        new(AppAccent.Orange, "Signal Orange", Hex("FF7A1A"), Hex("EA580C"), null),
        new(AppAccent.Blue, "Orbit Blue", Hex("4DA3FF"), Hex("2563EB"), CoolSlate),
        new(AppAccent.Teal, "Lagoon Teal", Hex("2DD4BF"), Hex("0D9488"), DeepLagoon),
        new(AppAccent.Violet, "Nebula Violet", Hex("A78BFA"), Hex("7C3AED"), DuskPlum),
        new(AppAccent.Green, "Aurora Green", Hex("4ADE80"), Hex("15803D"), NeutralZinc),
        new(AppAccent.Mono, "Graphite Mono", Hex("E4E4E7"), Hex("3F3F46"), NeutralZinc)
    ];

    public static AccentPalette Get(AppAccent accent) => All.FirstOrDefault(p => p.Accent == accent) ?? All[0];

    public static Color AccentFor(AppAccent accent, bool dark) => dark ? Get(accent).DarkAccent : Get(accent).LightAccent;

    /// <summary>
    /// The colours a preset puts on top of the base theme, by resource key. Empty for Signal Orange: its colours are
    /// the base themes themselves, hand-tuned in DarkTheme.xaml / LightTheme.xaml.
    /// </summary>
    public static IReadOnlyDictionary<string, object> BuildOverrideValues(AppAccent accent, bool dark)
    {
        var values = new Dictionary<string, object>();
        AccentPalette palette = Get(accent);
        if (palette.Accent == AppAccent.Orange)
            return values;

        Color a = dark ? palette.DarkAccent : palette.LightAccent;
        IReadOnlyDictionary<string, Color> greys = palette.DarkNeutrals ?? WarmGraphite;
        Color toolbar = dark ? greys["ToolbarBackgroundBrush"] : LightSurface;
        Color browser = dark ? greys["BrowserBackgroundBrush"] : LightSurface;
        Color borderStrong = dark ? greys["BorderStrongBrush"] : LightBorderStrong;

        if (dark && palette.DarkNeutrals is not null)
        {
            foreach (var (key, color) in palette.DarkNeutrals)
                values[key] = color;
        }

        values["AccentBrush"] = a;
        values["AccentHoverBrush"] = Mix(a, Colors.White, dark ? 0.22 : 0.15);
        values["AccentForegroundBrush"] = InkFor(a);
        values["AccentSubtleBrush"] = Color.FromArgb(dark ? (byte)0x2E : (byte)0x1F, a.R, a.G, a.B);
        values["SelectedRowBackgroundBrush"] = Mix(browser, a, dark ? 0.17 : 0.12);
        values["SelectedRowBorderBrush"] = a;
        // Stops are pre-blended (opaque) like the hand-tuned orange ones, to avoid muddy alpha mixing.
        values["ActiveTabGlowBrush"] = new[]
        {
            (Mix(toolbar, a, dark ? 0.48 : 0.32), 0.0),
            (Mix(toolbar, a, dark ? 0.20 : 0.15), dark ? 0.35 : 0.4),
            (toolbar, 1.0)
        };
        values["ActiveTabGlowBorderBrush"] = new[] { (a, 0.0), (borderStrong, 1.0) };
        return values;
    }

    /// <summary>Resources for <see cref="BuildOverrideValues"/> as frozen brushes; null when there is nothing to override.</summary>
    public static ResourceDictionary? BuildOverrides(AppAccent accent, bool dark)
    {
        var values = BuildOverrideValues(accent, dark);
        if (values.Count == 0)
            return null;

        var dict = new ResourceDictionary();
        foreach (var (key, value) in values)
        {
            Brush brush = value switch
            {
                Color c => new SolidColorBrush(c),
                (Color, double)[] stops => VerticalGradient(stops),
                _ => throw new InvalidOperationException($"Unexpected value for {key}.")
            };
            brush.Freeze();
            dict[key] = brush;
        }
        return dict;
    }

    /// <summary>Dark ink tinted with the accent, or white, whichever reads better on the accent.</summary>
    public static Color InkFor(Color accent)
    {
        Color darkInk = Mix(accent, Colors.Black, 0.9);
        return Contrast(darkInk, accent) >= Contrast(Colors.White, accent) ? darkInk : Colors.White;
    }

    /// <summary>WCAG contrast ratio between two opaque colours (1 to 21).</summary>
    public static double Contrast(Color x, Color y)
    {
        double lx = Luminance(x), ly = Luminance(y);
        return (Math.Max(lx, ly) + 0.05) / (Math.Min(lx, ly) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>Blends <paramref name="from"/> towards <paramref name="to"/> by <paramref name="amount"/> (0 to 1).</summary>
    public static Color Mix(Color from, Color to, double amount)
    {
        static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(Lerp(from.R, to.R, amount), Lerp(from.G, to.G, amount), Lerp(from.B, to.B, amount));
    }

    private static LinearGradientBrush VerticalGradient((Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (color, offset) in stops)
            brush.GradientStops.Add(new GradientStop(color, offset));
        return brush;
    }

    private static Color Hex(string rgb) => Color.FromRgb(
        Convert.ToByte(rgb[..2], 16), Convert.ToByte(rgb[2..4], 16), Convert.ToByte(rgb[4..6], 16));

    private static IReadOnlyDictionary<string, Color> Neutrals(
        string window, string toolbar, string sidebar, string browser, string status, string elevated, string input,
        string borderSubtle, string borderStrong, string hover, string activeTab,
        string textPrimary, string textSecondary, string textMuted, string thumb, string thumbHover) =>
        new Dictionary<string, Color>
        {
            ["WindowBackgroundBrush"] = Hex(window),
            ["ToolbarBackgroundBrush"] = Hex(toolbar),
            ["SidebarBackgroundBrush"] = Hex(sidebar),
            ["BrowserBackgroundBrush"] = Hex(browser),
            ["InspectorBackgroundBrush"] = Hex(sidebar),
            ["StatusBarBackgroundBrush"] = Hex(status),
            ["ElevatedSurfaceBrush"] = Hex(elevated),
            ["InputBackgroundBrush"] = Hex(input),
            ["BorderSubtleBrush"] = Hex(borderSubtle),
            ["BorderStrongBrush"] = Hex(borderStrong),
            ["HoverBackgroundBrush"] = Hex(hover),
            ["ActiveTabBackgroundBrush"] = Hex(activeTab),
            ["InactiveTabBackgroundBrush"] = Hex(sidebar),
            ["TextPrimaryBrush"] = Hex(textPrimary),
            ["TextSecondaryBrush"] = Hex(textSecondary),
            ["TextMutedBrush"] = Hex(textMuted),
            ["ScrollThumbBrush"] = Hex(thumb),
            ["ScrollThumbHoverBrush"] = Hex(thumbHover)
        };
}
