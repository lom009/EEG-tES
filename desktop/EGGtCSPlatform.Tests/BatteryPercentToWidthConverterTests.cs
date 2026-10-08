using System.Globalization;
using EGGtCSPlatform.ValueConverters;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class BatteryPercentToWidthConverterTests
{
    private readonly BatteryPercentToWidthConverter _converter = new();

    [Theory]
    [InlineData(null, 0d)]
    [InlineData(-10, 0d)]
    [InlineData(0, 0d)]
    [InlineData(25, 4d)]
    [InlineData(50, 8d)]
    [InlineData(100, 16d)]
    [InlineData(120, 16d)]
    public void ConvertsBatteryPercentToClampedFillWidth(int? batteryPercent, double expectedWidth)
    {
        var width = Assert.IsType<double>(
            _converter.Convert(batteryPercent, typeof(double), null, CultureInfo.InvariantCulture)
        );

        Assert.Equal(expectedWidth, width);
    }
}
