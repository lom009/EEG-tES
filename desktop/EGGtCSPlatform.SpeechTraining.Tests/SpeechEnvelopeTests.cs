using System.Buffers.Binary;
using System.Text.Json;
using EGGtCSPlatform.SpeechTraining;
using Xunit;
using Xunit.Abstractions;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechEnvelopeTests(ITestOutputHelper output)
{
    [Fact]
    public void MatchesResearchScipyFilterAndHilbertBeforeDisplayReduction()
    {
        using var fixture = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "SpeechEnvelopeReference.json")
            )
        );
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var pcm = item.GetProperty("pcm").EnumerateArray().Select(x => x.GetInt16()).ToArray();
            var rate = item.GetProperty("sampleRate").GetInt32();
            var envelope = EnvelopeExtractor.Extract(
                Audio(pcm, rate),
                new EnvelopeOptions(EnableFilter: true)
            );
            AssertError(item, "envelope", envelope);
            AssertError(
                item,
                "stimulusAmplitudes",
                SpeechStimulusProcessor.Prepare(envelope, rate).Amplitudes.ToArray()
            );
        }
    }

    private void AssertError(JsonElement item, string field, double[] actual)
    {
        var expected = item.GetProperty(field)
            .EnumerateArray()
            .Select(x => x.GetDouble())
            .ToArray();
        Assert.Equal(expected.Length, actual.Length);
        var error = actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
        output.WriteLine(
            $"{item.GetProperty("name").GetString()} {field}: max absolute error {error:E6}"
        );
        Assert.InRange(error, 0, 1e-5);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void AllSampleRatesHaveStableUnitDcGainAndFiniteNonnegativeEnvelope(int rate)
    {
        var constant = EnvelopeExtractor.Extract(
            Audio(Enumerable.Repeat((short)8192, 4097).ToArray(), rate)
        );
        Assert.All(constant, x => Assert.InRange(x, 0.25 - 1e-8, 0.25 + 1e-8));
        var pcm = Enumerable
            .Range(0, 4001)
            .Select(i =>
                (short)(
                    14000 * Math.Sin(2 * Math.PI * 90 * i / rate)
                    + 5000 * Math.Sin(2 * Math.PI * 1700 * i / rate)
                )
            )
            .ToArray();
        var envelope = EnvelopeExtractor.Extract(Audio(pcm, rate));
        Assert.Equal(pcm.Length, envelope.Length);
        Assert.All(envelope, x => Assert.True(double.IsFinite(x) && x >= 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(1001)]
    public void SilenceAndShortInputsAreDefined(int length)
    {
        Assert.All(EnvelopeExtractor.Extract(Audio(new short[length])), x => Assert.Equal(0, x));
        var result = EnvelopeExtractor.Extract(
            Audio(Enumerable.Repeat((short)-4096, length).ToArray())
        );
        Assert.Equal(length, result.Length);
        Assert.All(result, x => Assert.InRange(x, 0.125 - 1e-9, 0.125 + 1e-9));
    }

    [Fact]
    public void StereoIsAveragedBeforeFilteringAndDoesNotNormalizeToPeak()
    {
        var stereo = Enumerable
            .Range(0, 1025)
            .SelectMany(i => new[] { (short)12288, (short)-4096 })
            .ToArray();
        Assert.All(
            EnvelopeExtractor.Extract(Audio(stereo, channels: 2)),
            x => Assert.InRange(x, 0.125 - 1e-9, 0.125 + 1e-9)
        );
        var cancelled = Enumerable
            .Range(0, 1025)
            .SelectMany(i => new[] { (short)12288, (short)-12288 })
            .ToArray();
        Assert.All(
            EnvelopeExtractor.Extract(Audio(cancelled, channels: 2)),
            x => Assert.Equal(0, x)
        );
    }

    [Fact]
    public void DisplayUsesFullEnvelopePeaksWithUnchangedVoiceBucketsAndTimes()
    {
        var audio = Audio(
            Enumerable.Range(0, 2407).Select(i => (short)(16000 * Math.Sin(i * 0.04))).ToArray()
        );
        var full = EnvelopeExtractor.Extract(audio);
        var prepared = TestAudio.Display(audio);
        Assert.Equal(audio.GetWaveform(), prepared.Voice);
        Assert.Equal(prepared.Voice.Count, prepared.Envelope.Count);
        Assert.InRange(prepared.Envelope.Count, 1, 1200);
        var stride = (int)Math.Ceiling(full.Length / 1200d);
        for (var i = 0; i < prepared.Envelope.Count; i++)
        {
            Assert.Equal(prepared.Voice[i].Seconds, prepared.Envelope[i].Seconds);
            Assert.Equal((double)(i * stride) / audio.SampleRate, prepared.Envelope[i].Seconds);
            Assert.Equal(full.Skip(i * stride).Take(stride).Max(), prepared.Envelope[i].Amplitude);
        }
        Assert.True(prepared.Envelope[^1].Seconds < audio.Duration.TotalSeconds);
    }

    [Fact]
    public void CancellationAndMalformedAudioAreReported()
    {
        Assert.Throws<OperationCanceledException>(() =>
            EnvelopeExtractor.Extract(Audio([1, 2, 3]), new(true))
        );
        Assert.Throws<ArgumentException>(() => EnvelopeExtractor.Extract(new(16000, 1, [0])));
    }

    private static AudioBuffer Audio(short[] pcm, int sampleRate = 16000, int channels = 1)
    {
        var bytes = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[i]);
        return new(sampleRate, channels, bytes);
    }
}
