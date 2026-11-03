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
