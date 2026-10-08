using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace EGGtCSPlatform.ValueConverters;

public sealed class BatteryPercentToWidthConverter : IValueConverter
{
    public double MaximumWidth { get; set; } = 16d;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int batteryPercent ? Math.Clamp(batteryPercent, 0, 100) / 100d * MaximumWidth : 0d;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
