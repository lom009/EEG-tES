using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Transformation;

namespace EGGtCSPlatform.ValueConverters;

public sealed class VerticalOffsetToTransformConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var offset = value is double number ? number : 0d;
        return TransformOperations.Parse(
            $"translate(0px, {offset.ToString(CultureInfo.InvariantCulture)}px)"
        );
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
