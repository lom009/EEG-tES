using System;
using System.Linq;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EegDisplayFilterTests
{
    [Theory]
    [InlineData(250)]
    [InlineData(500)]
    public void BypassReturnsOriginalSamples(int sampleRateHz)
    {
        var samples = new[] { -1d, 0d, 2.5d };
        var filter = new CausalEegDisplayFilter(
            sampleRateHz,
            new EegDisplayFilterSettings(null, null, null)
        );

        var result = filter.Process(samples);

        Assert.Same(samples, result);
    }

    [Theory]
    [InlineData(250, 50)]
    [InlineData(250, 60)]
    [InlineData(500, 50)]
    [InlineData(500, 60)]
    public void NotchStronglyAttenuatesConfiguredFrequency(int sampleRateHz, double frequencyHz)
    {
        var ratio = MeasureGain(
            sampleRateHz,
            frequencyHz,
            new EegDisplayFilterSettings(null, null, frequencyHz)
        );

        Assert.True(
            ratio < 0.08d,
            $"Expected notch attenuation below 0.08, actual {ratio:0.0000}."
        );
    }

    [Theory]
    [InlineData(250)]
    [InlineData(500)]
    public void LowPassAttenuatesFrequencyAboveCutoff(int sampleRateHz)
    {
        var passGain = MeasureGain(
            sampleRateHz,
            10d,
            new EegDisplayFilterSettings(null, 30d, null)
        );
        var stopGain = MeasureGain(
            sampleRateHz,
            100d,
            new EegDisplayFilterSettings(null, 30d, null)
        );

        Assert.True(passGain > 0.9d, $"Unexpected pass-band gain {passGain:0.0000}.");
        Assert.True(stopGain < 0.15d, $"Unexpected stop-band gain {stopGain:0.0000}.");
    }

    [Theory]
    [InlineData(250)]
    [InlineData(500)]
    public void HighPassRejectsDcAndKeepsChannelStateIsolated(int sampleRateHz)
    {
        var settings = new EegDisplayFilterSettings(0.5d, null, null);
        var firstChannel = new CausalEegDisplayFilter(sampleRateHz, settings);
        var secondChannel = new CausalEegDisplayFilter(sampleRateHz, settings);
        var output = Enumerable
            .Range(0, sampleRateHz * 8)
            .Select(_ => firstChannel.Process(100d))
            .ToArray();

        Assert.True(Math.Abs(output[^1]) < 0.01d);
        Assert.Equal(0d, secondChannel.Process(0d), 12);
    }

    [Fact]
    public void ResetRestoresInitialFilterState()
    {
        var filter = new CausalEegDisplayFilter(500, new EegDisplayFilterSettings(0.5d, 70d, 50d));
        var first = filter.Process(10d);
        _ = filter.Process(20d);

        filter.Reset();

        Assert.Equal(first, filter.Process(10d), 12);
    }

    private static double MeasureGain(
        int sampleRateHz,
        double frequencyHz,
        EegDisplayFilterSettings settings
    )
    {
        var filter = new CausalEegDisplayFilter(sampleRateHz, settings);
        var count = sampleRateHz * 8;
        var discard = sampleRateHz * 4;
        var inputEnergy = 0d;
        var outputEnergy = 0d;
        for (var index = 0; index < count; index++)
        {
            var input = Math.Sin(2d * Math.PI * frequencyHz * index / sampleRateHz);
            var output = filter.Process(input);
            if (index < discard)
                continue;
            inputEnergy += input * input;
            outputEnergy += output * output;
        }
        return Math.Sqrt(outputEnergy / inputEnergy);
    }
}
