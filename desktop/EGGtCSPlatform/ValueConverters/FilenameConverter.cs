using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace EGGtCSPlatform.ValueConverters;

public class FilenameConverter : IValueConverter
{
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) =>
        value is string path && !string.IsNullOrWhiteSpace(path)
            ? Path.GetFileNameWithoutExtension(path)
            : value;

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotImplementedException();
}
