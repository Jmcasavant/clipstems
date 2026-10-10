using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Clipstems.App.Settings;

public static class Converters
{
    /// <summary>Visible when the value is a non-empty string.</summary>
    public static readonly IValueConverter NotEmpty = new Lambda(v => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible);

    /// <summary>Collapsed when the value is true.</summary>
    public static readonly IValueConverter Collapsed = new Lambda(v => v is true ? Visibility.Collapsed : Visibility.Visible);

    public static readonly IValueConverter Not = new Lambda(v => v is not true, v => v is not true);

    public static readonly IValueConverter Minutes = new Lambda(v => v is int m
        ? m < 60 ? $"{m} minutes" : m % 60 == 0 ? $"{m / 60} hour{(m == 60 ? "" : "s")}" : $"{m} minutes"
        : "");

    private sealed class Lambda(Func<object?, object?> convert, Func<object?, object?>? back = null) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            back is null ? Binding.DoNothing : back(value);
    }
}
