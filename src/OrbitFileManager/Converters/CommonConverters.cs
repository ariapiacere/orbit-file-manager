using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OrbitFileManager.Converters;

public sealed class BooleanToVisibilityExConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is Visibility.Visible;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible;
    }
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value switch
        {
            null => false,
            string str => !string.IsNullOrWhiteSpace(str),
            int count => count > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => true
        };

        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
            hasValue = !hasValue;

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Visible when an int (e.g. a collection's Count) is greater than <see cref="Threshold"/>.</summary>
public sealed class CountAboveToVisibilityConverter : IValueConverter
{
    public int Threshold { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > Threshold ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EnumEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return Visibility.Collapsed;

        bool match = string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Gives a view's list its rows only while that view mode is showing; the hidden views get null. Each pane has
/// five list views (Details, List, Small, Large, Thumbnails). When all five were bound to the same rows, every
/// reload paid for the four hidden ones too: about 3.5 s instead of 60 ms for 5,000 files.
/// Values: [0] the rows, [1] the tab's view mode. Parameter: the view mode this list shows.
/// </summary>
public sealed class ItemsForViewModeConverter : IMultiValueConverter
{
    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not System.Collections.IEnumerable items || values[1] is null || parameter is null)
            return null;

        return string.Equals(values[1]!.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase) ? items : null;
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EnumEqualsToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string paramStr && targetType.IsEnum)
            return Enum.Parse(targetType, paramStr, ignoreCase: true);

        return Binding.DoNothing;
    }
}

public sealed class CutOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.50 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class HexStringToBrushConverter : IValueConverter
{
    private static readonly BrushConverter Converter = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (Converter.ConvertFromString(hex) is SolidColorBrush brush)
                {
                    if (brush.CanFreeze) brush.Freeze();
                    return brush;
                }
            }
            catch { }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>A double times the converter parameter (invariant culture), e.g. 45% of a panel's height.</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double factor = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) ? f : 1.0;
        return value is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? Math.Max(0, d * factor) : double.PositiveInfinity;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RatioToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is double ratio &&
            values[1] is double totalWidth &&
            !double.IsNaN(totalWidth) &&
            totalWidth > 0)
        {
            return Math.Clamp(ratio, 0.0, 1.0) * totalWidth;
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
