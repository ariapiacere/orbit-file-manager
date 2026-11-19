namespace OrbitFileManager.Controls;

/// <summary>A Details column other than Name, for <see cref="DetailsColumnFitter"/>.</summary>
/// <param name="Preferred">Width when there is room.</param>
/// <param name="Min">Narrowest it gets before it is dropped.</param>
/// <param name="DropOrder">Lower numbers are dropped first when even the minimum widths do not fit.</param>
public sealed record FitColumn(string Key, double Preferred, double Min, int DropOrder);

/// <summary>
/// Fits the Details columns into a pane so the list never needs a sideways scrollbar ("fit columns" mode).
/// Name always fills what is left. Step by step, as the pane gets narrower:
/// <list type="number">
/// <item>Every column at its preferred width, Name gets the rest (at least <see cref="NameComfortable"/>).</item>
/// <item>The other columns shrink evenly towards their minimum widths so Name keeps <see cref="NameComfortable"/>.</item>
/// <item>Other columns at their minimum; Name shrinks down to <see cref="NameMin"/>.</item>
/// <item>Columns are dropped in <see cref="FitColumn.DropOrder"/> until the rest fit beside Name.</item>
/// </list>
/// </summary>
public static class DetailsColumnFitter
{
    public const double NameComfortable = 200;
    public const double NameMin = 120;

    /// <returns>The Name width and one width per column, in the order given (0 = dropped). Never wider than <paramref name="available"/>.</returns>
    public static (double Name, double[] Widths) Fit(double available, IReadOnlyList<FitColumn> columns)
    {
        available = Math.Max(0, Math.Floor(available));
        var widths = new double[columns.Count];
        double sumPreferred = columns.Sum(c => c.Preferred);
        double sumMin = columns.Sum(c => Math.Min(c.Min, c.Preferred));

        if (available - sumPreferred >= NameComfortable)
        {
            for (int i = 0; i < columns.Count; i++)
                widths[i] = columns[i].Preferred;
        }
        else if (available - sumMin >= NameComfortable)
        {
            double needed = sumPreferred - (available - NameComfortable);
            double shrinkable = sumPreferred - sumMin;
            for (int i = 0; i < columns.Count; i++)
            {
                double room = columns[i].Preferred - Math.Min(columns[i].Min, columns[i].Preferred);
                widths[i] = columns[i].Preferred - (shrinkable > 0 ? room * needed / shrinkable : 0);
            }
        }
        else
        {
            var kept = Enumerable.Range(0, columns.Count).ToHashSet();
            double keptMin = sumMin;
            foreach (int i in Enumerable.Range(0, columns.Count).OrderBy(i => columns[i].DropOrder))
            {
                if (available - keptMin >= NameMin)
                    break;
                kept.Remove(i);
                keptMin -= Math.Min(columns[i].Min, columns[i].Preferred);
            }

            foreach (int i in kept)
                widths[i] = Math.Min(columns[i].Min, columns[i].Preferred);
        }

        for (int i = 0; i < widths.Length; i++)
            widths[i] = Math.Floor(widths[i]);

        double name = Math.Max(0, available - widths.Sum());
        return (name, widths);
    }
}
