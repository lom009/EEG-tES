using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace EGGtCSPlatform.ValueConverters;

public class BooleanToBoldFontConverter : IValueConverter
{
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) =>
        value is bool and true
            ? Application.Current?.FindResource("AlibabaPuHuiTi")
            : Application.Current?.FindResource("AlibabaPuHuiTi");

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotImplementedException();
}
