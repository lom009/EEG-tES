using EGGtCSPlatform.SpeechTraining;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechStimulusTests
{
    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void WindowMeansIncludePartialWindowAndNormalizeAgain(int rate)
    {
        var stride = rate / 100;
        var envelope = Enumerable
            .Repeat(10d, stride)
            .Concat(Enumerable.Repeat(20d, stride))
            .Concat(Enumerable.Repeat(30d, 3))
            .ToArray();
        var data = SpeechStimulusProcessor.Prepare(envelope, rate);
        Assert.Equal(new[] { 0d, 0.5, 1d }, data.Amplitudes);
        Assert.Equal((double)envelope.Length / rate, data.Waveform[^1].Seconds);
        Assert.All(data.Waveform, p => Assert.True(double.IsFinite(p.Value)));
    }

    [Fact]
    public void ContinuousCarrierHasSixteenSamplesPerCycleAndMatchesBothContours()
    {
        var data = SpeechStimulusProcessor.BuildPreview([0.5], 0.02);
        Assert.Equal(65, data.Waveform.Count);
        for (var i = 0; i < data.Waveform.Count; i++)
        {
            Assert.Equal(i / 3200d, data.Waveform[i].Seconds);
            Assert.InRange(data.Waveform[i].Value, data.Lower[i].Value, data.Upper[i].Value);
            Assert.Equal(0.5, data.Upper[i].Value);
            Assert.Equal(-0.5, data.Lower[i].Value);
            if (i % 8 == 0)
                Assert.InRange(Math.Abs(data.Waveform[i].Value), 0, 1e-12);
            if (i % 16 == 4)
                Assert.Equal(0.5, data.Waveform[i].Value, 12);
            if (i % 16 == 12)
                Assert.Equal(-0.5, data.Waveform[i].Value, 12);
            if (i >= 16)
                Assert.Equal(data.Waveform[i - 16].Value, data.Waveform[i].Value, 12);
        }
        Assert.Equal(data.Waveform.Select(p => p.Seconds), data.Upper.Select(p => p.Seconds));
        Assert.Equal(data.Upper.Select(p => -p.Value), data.Lower.Select(p => p.Value));
    }

    [Fact]
    public void PchipPassesThroughNodesWithoutOvershootOrStepsAndHoldsLastValue()
    {
        double[] nodes = [0, 0.25, 1, 1, 0.1, 0.6];
        var data = SpeechStimulusProcessor.BuildPreview(nodes, 0.07);
        for (var i = 0; i < nodes.Length; i++)
            Assert.Equal(nodes[i], data.Upper[i * 32].Value, 12);
        foreach (var p in data.Upper)
        {
            var i = Math.Min((int)(p.Seconds * 100), nodes.Length - 1);
            var next = Math.Min(i + 1, nodes.Length - 1);
            Assert.InRange(
                p.Value,
                Math.Min(nodes[i], nodes[next]) - 1e-12,
                Math.Max(nodes[i], nodes[next]) + 1e-12
            );
        }
        Assert.Equal(0.078125, data.Upper[16].Value, 12); // Independent equal-spacing PCHIP value.
        Assert.True(data.Upper[1].Value > 0 && data.Upper[1].Value < nodes[1]);
        Assert.All(data.Upper.Where(p => p.Seconds >= 0.05), p => Assert.Equal(nodes[^1], p.Value));
        for (var i = 1; i < data.Waveform.Count; i++)
            Assert.True(data.Waveform[i].Seconds > data.Waveform[i - 1].Seconds);
        var peak = SpeechStimulusProcessor.BuildPreview([0, 1, 0], 0.02);
        Assert.Equal(0.75, peak.Upper[16].Value, 12);
        Assert.Equal(0.75, peak.Upper[48].Value, 12);
        var linear = SpeechStimulusProcessor.BuildPreview([0, 1], 0.02);
        Assert.Equal(0.5, linear.Upper[16].Value, 12);
    }

    [Theory]
    [InlineData(0.00005)]
    [InlineData(0.0011)]
    [InlineData(0.0100625)]
    [InlineData(0.07)]
    public void IncludesExactAudioEndpointWithoutTruncatingOrZeroingPartialCycles(double duration)
    {
        var data = SpeechStimulusProcessor.BuildPreview([1], duration);
        Assert.Equal(duration, data.Waveform[^1].Seconds);
        Assert.Equal(Math.Sin(2 * Math.PI * 200 * duration), data.Waveform[^1].Value, 12);
        Assert.Equal(1, data.Upper[^1].Value);
        for (var i = 1; i < data.Waveform.Count; i++)
        {
            Assert.True(data.Waveform[i].Seconds > data.Waveform[i - 1].Seconds);
            Assert.InRange(
                data.Waveform[i].Seconds - data.Waveform[i - 1].Seconds,
                0,
                1 / 3200d + 1e-12
            );
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(79)]
    [InlineData(80)]
    [InlineData(4001)]
    public void EmptySilenceConstantAndNearConstantAreZero(int length)
    {
        foreach (var value in new[] { 0d, 0.25, 100d })
        {
            var envelope = Enumerable.Range(0, length).Select(i => value + i % 2 * 1e-14).ToArray();
            var data = SpeechStimulusProcessor.Prepare(envelope, 16000);
            Assert.All(data.Amplitudes, a => Assert.Equal(0, a));
            Assert.All(
                data.Waveform.Concat(data.Upper).Concat(data.Lower),
                p => Assert.Equal(0, p.Value)
            );
        }
    }

    [Fact]
    public void ShortAudioDoesNotAmplifySingleWindowButStillCoversTheWholeDuration()
    {
        var data = SpeechStimulusProcessor.Prepare(
            Enumerable.Range(0, 79).Select(i => (double)i).ToArray(),
            16000
        );
        Assert.Equal(new[] { 0d }, data.Amplitudes);
        Assert.Equal(79 / 16000d, data.Waveform[^1].Seconds);
        Assert.All(data.Waveform, p => Assert.Equal(0, p.Value));
        // Equal window means must also be treated as constant by the second normalization.
        data = SpeechStimulusProcessor.Prepare(
            Enumerable.Range(0, 320).Select(i => (double)(i % 2)).ToArray(),
            16000
        );
        Assert.All(data.Amplitudes, a => Assert.Equal(0, a));
    }

    [Fact]
    public void PreparationUsesFullEnvelopeAndReusesTheSameCachedStimulus()
    {
        var provider = new FileSpeechCorpusProvider();
        var audio = provider.ReadAudio(provider.Corpora[0].Sentences[0]);
        var expected = SpeechStimulusProcessor.Prepare(
            EnvelopeExtractor.Extract(audio),
            audio.SampleRate
        );
        var prepared = TestAudio.Display(audio);
        Assert.Equal(expected.Amplitudes, prepared.Stimulus.Amplitudes);
        Assert.Equal(expected.Waveform, prepared.Stimulus.Waveform);
    }

    [Fact]
    public void InvalidInputAndCancellationAreReported()
    {
        Assert.Throws<ArgumentException>(() =>
            SpeechStimulusProcessor.Prepare([0, double.NaN], 16000)
        );
        Assert.Throws<ArgumentException>(() => SpeechStimulusProcessor.Prepare([-1, 0], 16000));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechStimulusProcessor.Prepare([1], 99));
        Assert.Throws<OperationCanceledException>(() =>
            SpeechStimulusProcessor.Prepare([0, 1], 16000, new(true))
        );
    }
}
