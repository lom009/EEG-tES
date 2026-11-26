using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.ValueConverters;

public sealed class PageContentMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ElectrodeConfigurationPageViewModel or EnvelopeStimulationPageViewModel or PictureNamingPageViewModel
            ? default(Thickness)
            : new Thickness(0, 44, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
