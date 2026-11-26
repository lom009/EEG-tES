using EGGtCSPlatform.Controls;
using Xunit;

namespace EGGtCSPlatform.Tests;

public class NumericStepTests
{
    [Theory]
    [InlineData("--2")]
    [InlineData("1-2")]
    [InlineData("1e3")]
    [InlineData("-7.255")]
    public void MalformedSignedTextCannotChangeCommittedValue(string text)
    {
        var input = new StimulusNumericInput
        {
            AllowNegative = true,
            DecimalPlaces = 2,
            Minimum = -30,
            Maximum = 30,
            Value = -7,
        };
        input.Text = text.Replace(".", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
        Assert.Equal(-7m, input.Value);
        Assert.NotEqual(text, input.Text);
    }

    [Theory]
    [InlineData("0.13", "0.04", "2", "0.04", "0.12")]
    [InlineData("0.14", "0.04", "2", "0.04", "0.16")]
    [InlineData("0.16", "0.04", "2", "0.04", "0.16")]
    [InlineData("-7.25", "-30", "30", "1", "-7")]
    [InlineData("-7.5", "-30", "30", "1", "-8")]
    [InlineData("0", "0.04", "2", "0.04", "0.04")]
    [InlineData("3", "0.04", "2", "0.04", "2")]
    [InlineData("1.99", "0.03", "1.99", "0.04", "1.96")]
    [InlineData("0.03", "0.03", "1.99", "0.04", "0.04")]
    public void ManualValuesAlignToLegalGrid(string value, string minimum, string maximum, string step, string expected)
    {
        static decimal Parse(string text) => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Parse(expected), StepNumericUpDown.AlignToStep(Parse(value), Parse(minimum), Parse(maximum), Parse(step)));
    }

    [Fact]
    public void FractionalStepSupportsDefaultBounds()
    {
        Assert.Equal(1.23m, StepNumericUpDown.AlignToStep(1.234m, decimal.MinValue, decimal.MaxValue, 0.01m));
    }
}
