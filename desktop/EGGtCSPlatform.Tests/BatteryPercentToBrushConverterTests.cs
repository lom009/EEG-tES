using System.Globalization;
using Avalonia.Media;
using EGGtCSPlatform.ValueConverters;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class BatteryPercentToBrushConverterTests
{
    private readonly BatteryPercentToBrushConverter _converter = new();

    [Theory]
    [InlineData(null, "#A6B1C0")]
    [InlineData(0, "#EF4444")]
    [InlineData(20, "#EF4444")]
    [InlineData(21, "#F59E0B")]
    [InlineData(40, "#F59E0B")]
    [InlineData(41, "#2F86FF")]
    [InlineData(100, "#2F86FF")]
    public void UsesExpectedBatteryLevelColor(int? batteryPercent, string expectedColor)
    {
        var brush = Assert.IsType<SolidColorBrush>(
            _converter.Convert(batteryPercent, typeof(IBrush), null, CultureInfo.InvariantCulture)
        );

        Assert.Equal(Color.Parse(expectedColor), brush.Color);
    }
}
