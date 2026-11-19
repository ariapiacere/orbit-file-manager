using OrbitFileManager.Controls;
using Xunit;

namespace OrbitFileManager.Tests;

public sealed class DetailsColumnFitterTests
{
    // Same defaults as BrowserPaneControl: Ext, Size, Type, Modified, Created, Attr.
    private static readonly FitColumn[] AllColumns =
    [
        new("Ext", 56, 38, 3),
        new("Size", 80, 60, 5),
        new("Type", 132, 64, 4),
        new("Modified", 124, 108, 6),
        new("Created", 124, 108, 2),
        new("Attr", 48, 36, 1)
    ];

    private static double Total((double Name, double[] Widths) fit) => fit.Name + fit.Widths.Sum();

    [Fact]
    public void WidePane_KeepsPreferredWidthsAndNameTakesTheRest()
    {
        var fit = DetailsColumnFitter.Fit(1200, AllColumns);

        Assert.Equal(AllColumns.Select(c => c.Preferred), fit.Widths);
        Assert.Equal(1200 - 564, fit.Name);
    }

    [Fact]
    public void MediumPane_ShrinksOtherColumnsSoNameKeepsTwoHundred()
    {
        var fit = DetailsColumnFitter.Fit(700, AllColumns);

        Assert.True(fit.Name >= DetailsColumnFitter.NameComfortable);
        Assert.InRange(Total(fit), 699, 700);
        for (int i = 0; i < AllColumns.Length; i++)
            Assert.InRange(fit.Widths[i], AllColumns[i].Min, AllColumns[i].Preferred);
    }

    [Fact]
    public void NarrowPane_OtherColumnsAtMinimumAndNameBetweenLimits()
    {
        double sumMin = AllColumns.Sum(c => c.Min); // 414
        var fit = DetailsColumnFitter.Fit(sumMin + 150, AllColumns);

        Assert.Equal(AllColumns.Select(c => c.Min), fit.Widths);
        Assert.Equal(150, fit.Name);
    }

    [Theory]
    [InlineData(400, new[] { "Ext", "Size", "Type", "Modified" })] // Attr and Created went
    [InlineData(360, new[] { "Size", "Type", "Modified" })]        // then Ext
    public void VeryNarrowPane_DropsLeastImportantColumnsFirst(double available, string[] expectedKept)
    {
        var fit = DetailsColumnFitter.Fit(available, AllColumns);

        string[] kept = AllColumns.Where((c, i) => fit.Widths[i] > 0).Select(c => c.Key).ToArray();
        Assert.Equal(expectedKept, kept);
        Assert.True(fit.Name >= DetailsColumnFitter.NameMin);
        Assert.True(Total(fit) <= available);
    }

    [Fact]
    public void TinyPane_OnlyNameIsLeft()
    {
        var fit = DetailsColumnFitter.Fit(90, AllColumns);

        Assert.All(fit.Widths, w => Assert.Equal(0, w));
        Assert.Equal(90, fit.Name);
    }

    [Fact]
    public void NeverWiderThanThePane()
    {
        for (int available = 0; available <= 2000; available += 7)
        {
            var fit = DetailsColumnFitter.Fit(available + 0.6, AllColumns);
            Assert.True(Total(fit) <= available + 0.6, $"{available}: total {Total(fit)}");
            Assert.True(fit.Name >= Math.Min(available, DetailsColumnFitter.NameMin), $"{available}: name {fit.Name}");
        }
    }

    [Fact]
    public void ADraggedColumnKeepsItsWidthWhileThereIsRoom()
    {
        var columns = AllColumns.Select(c => c.Key == "Type" ? c with { Preferred = 220 } : c).ToArray();
        var fit = DetailsColumnFitter.Fit(1200, columns);

        Assert.Equal(220, fit.Widths[2]);
        Assert.Equal(1200 - 652, fit.Name);
    }
}
