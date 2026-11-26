using System.Text.Json;
using EGGtCSPlatform.Audio.Dsp.Reverb;
using Xunit;

namespace EGGtCSPlatform.Audio.Dsp.Tests;

public sealed class RirResearchTests
{
    [Fact]
    public void IndependentGeneratorAndFftConvolutionMatchResearchOracle()
    {
        using var json = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "SpeechRirReference.json")
            )
        );
        foreach (var c in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            var position = c.GetProperty("source")
                .EnumerateArray()
                .Select(v => v.GetDouble())
                .ToArray();
            var settings = RirSettings.Default with
            {
                Source = new(position[0], position[1], position[2]),
                T60 = c.GetProperty("t60").GetDouble(),
                LengthSeconds = c.GetProperty("length").GetDouble(),
            };
            var response = new RirGenerator().Generate(settings);
            var expected = c.GetProperty("rir")
                .EnumerateArray()
                .Select(v => v.GetDouble())
                .ToArray();
            Assert.Equal(c.GetProperty("directFrame").GetInt32(), response.DirectFrame);
            Assert.Equal(expected.Length, response.Full.Length);
            Assert.InRange(
                expected.Zip(response.Full).Max(p => Math.Abs(p.First - p.Second)),
                0,
                1e-8
            );
            var signal = Enumerable
                .Range(0, 65)
                .Select(i => .12 * Math.Sin(i * 2 * Math.PI * 431 / 16000))
                .ToArray();
            var convolution = EGGtCSPlatform.Audio.Dsp.Reverb.Reverb.Convolve(
                signal,
                response.Full,
                default
            );
            var expectedConvolution = c.GetProperty("convolution")
                .EnumerateArray()
                .Select(v => v.GetDouble());
            Assert.InRange(
                expectedConvolution.Zip(convolution).Max(p => Math.Abs(p.First - p.Second)),
                0,
                1e-8
            );
        }
    }
}
