using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SubtitleStudio.Infrastructure;

/// <summary>
/// "Truthy" value to Visibility. True for: true, non-zero numbers, non-empty strings,
/// non-empty collections, any other non-null object. ConverterParameter "Invert" flips it.
/// </summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool truthy = value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0 && !double.IsNaN(d),
            string s => !string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count > 0,
            _ => true,
        };

        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            truthy = !truthy;

        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when value.ToString() equals ConverterParameter (case-insensitive).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var left = value?.ToString() ?? string.Empty;
        var right = parameter?.ToString() ?? string.Empty;
        return left.Equals(right, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
