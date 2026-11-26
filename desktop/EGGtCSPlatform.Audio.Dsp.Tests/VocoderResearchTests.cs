using System.Text.Json;
using EGGtCSPlatform.Audio.Dsp.Internal;
using EGGtCSPlatform.Audio.Dsp.Vocoder;
using Xunit;

namespace EGGtCSPlatform.Audio.Dsp.Tests;

public sealed class VocoderResearchTests(Xunit.Abstractions.ITestOutputHelper log)
{
    [Fact]
    public void AllResearchFixturesMatch()
    {
        using var data = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "SpeechVocoderReference.json")
            )
        );
        double maximumError = 0,
            maximumRms = 0;
        Assert.Equal(48, data.RootElement.GetArrayLength());
        foreach (var item in data.RootElement.EnumerateArray())
        {
            double[] Array(string key) =>
                item.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var s = new VocoderOptions(
                (VocoderAlgorithm)item.GetProperty("Algorithm").GetInt32(),
                item.GetProperty("Bands").GetInt32(),
                (VocoderCarrier)item.GetProperty("Carrier").GetInt32(),
                item.GetProperty("Maxima").GetInt32()
            );
            var input = Array("Input");
            var expected = Array("Output");
            var actual = EGGtCSPlatform.Audio.Dsp.Vocoder.Vocoder.Synthesize(input, s);
            Assert.Equal(expected.Length, actual.Length);
            var errors = actual.Zip(expected, (a, b) => a - b).ToArray();
            maximumError = Math.Max(maximumError, errors.Max(Math.Abs));
            maximumRms = Math.Max(maximumRms, VocoderDsp.Rms(errors));
            var random = new VocoderRandom(s.Seed);
            foreach (var uniform in Array("Uniforms"))
                Assert.Equal(uniform, random.Uniform());
            Assert.True(
                errors.Max(Math.Abs) <= 1e-4,
                $"{s.Summary}: maximum error {errors.Max(Math.Abs):G10}"
            );
            Assert.True(
                VocoderDsp.Rms(errors) <= 1e-5,
                $"{s.Summary}: RMS error {VocoderDsp.Rms(errors):G10}"
            );
            if (s.UsesMaxima)
            {
                var q = EGGtCSPlatform.Audio.Dsp.Vocoder.Vocoder.AnalyzeAce(input, s);
                Assert.Equal(Array("Electrodes").Select(v => (int)v), q.Electrodes);
                Assert.Equal(Array("Levels").Select(v => (int)v), q.Levels);
                Assert.InRange(
                    Math.Abs(
                        item.GetProperty("Alpha").GetDouble()
                            - EGGtCSPlatform.Audio.Dsp.Vocoder.Vocoder.Alpha
                    ),
                    0,
                    1e-8
                );
            }
        }
        log.WriteLine(
            $"48 MATLAB cases: max abs error {maximumError:G10}; max RMS error {maximumRms:G10}."
        );
    }

    [Theory]
    [InlineData(VocoderAlgorithm.ClassicalCis)]
    [InlineData(VocoderAlgorithm.Ace)]
    [InlineData(VocoderAlgorithm.ClassicalTle)]
    [InlineData(VocoderAlgorithm.AceTle)]
    public void EmptySilenceShortInputsAndCancellationAreDefined(VocoderAlgorithm algorithm)
    {
        var options = new VocoderOptions(algorithm);
        Assert.Empty(EGGtCSPlatform.Audio.Dsp.Vocoder.Vocoder.Synthesize([], options));
        foreach (int length in new[] { 1, 2, 15, 16, 17, 513 })
        {
            var output = EGGtCSPlatform.Audio.Dsp.Vocoder.Vocoder.Synthesize(
                new double[length],
                options
            );
            Assert.All(output, v => Assert.Equal(0, v));
            Assert.Equal(
                options.UsesMaxima ? (length + 15) / 16 * 16 + 960 : length,
                output.Length
            );
        }
        Assert.Throws<OperationCanceledException>(() =>
            EGGtCSPlatform.Audio.Dsp.Vocoder.Vocoder.Synthesize([.1, .2], options, token: new(true))
        );
    }

    [Fact]
    public void PcmEncodingClipsWithOriginalRounding()
    {
        var audio = IO.Pcm16Codec.Encode(
            [
                [2, -2],
            ],
            16000,
            out var clipped
        );
        Assert.True(clipped);
        Assert.Equal(new[] { 32767 / 32768d, -1 }, IO.Pcm16Codec.Decode(audio)[0]);
        Assert.Throws<ArgumentException>(() => new VocoderOptions(Bands: 0).Validate());
        Assert.Throws<ArgumentException>(() => new VocoderOptions(Bands: 4, Maxima: 8).Validate());
    }
}
