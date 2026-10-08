using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace EGGtCSPlatform.ValueConverters;

public sealed class BatteryPercentToBrushConverter : IValueConverter
{
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#2F86FF"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush LowBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#A6B1C0"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            int batteryPercent when batteryPercent <= 20 => LowBrush,
            int batteryPercent when batteryPercent <= 40 => WarningBrush,
            int => NormalBrush,
            _ => UnknownBrush,
        };

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
