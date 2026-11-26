using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechVocoderTests
{
    private static double Rms(double[] values) => Math.Sqrt(values.Sum(x => x * x) / values.Length);

    [Fact]
    public void ResamplingRejectsAliasingAndKeepsToneFrequency()
    {
        var pass = Pcm16Codec.Decode(
            SincResampler.Resample(
                SpeechAudioPreparationTests.Tone(48000, 4800, frequency: 1000),
                16000
            )
        )[0][200..1400];
        var stop = Pcm16Codec.Decode(
            SincResampler.Resample(
                SpeechAudioPreparationTests.Tone(48000, 4800, frequency: 12000),
                16000
            )
        )[0][200..1400];
        Assert.True(Rms(stop) < Rms(pass) * .001);
        var crossings = Enumerable
            .Range(1, pass.Length - 1)
            .Count(i => pass[i] >= 0 && pass[i - 1] < 0);
        Assert.InRange(crossings, 74, 76);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void ResamplingAndReferenceFollowActualTime(int rate)
    {
        var input = SpeechAudioPreparationTests.Tone(rate, rate / 10);
        var result = TestAudio.Prepare(input, new(Vocoder: new()));
        Assert.Equal(16000, result.Playback.SampleRate);
        Assert.Equal(16000, result.EnvelopeSource.Audio.SampleRate);
        Assert.Equal(1600, result.EnvelopeSource.Audio.Frames);
        Assert.Equal(input.Duration, result.Playback.Duration);
        if (rate == 16000)
            Assert.Same(input, result.EnvelopeSource.Audio);
    }

    [Theory]
    [InlineData(VocoderAlgorithm.ClassicalCis)]
    [InlineData(VocoderAlgorithm.Ace)]
    [InlineData(VocoderAlgorithm.ClassicalTle)]
    [InlineData(VocoderAlgorithm.AceTle)]
    public void StereoRepeatabilityAndTail(VocoderAlgorithm algorithm)
    {
        var input = SpeechAudioPreparationTests.Tone(frames: 513, channels: 2);
        var settings = new TrainingAudioSettings(Vocoder: new(algorithm));
        var first = TestAudio.Prepare(input, settings);
        var second = TestAudio.Prepare(input, settings);
        Assert.Equal(first.Playback.ToArray(), second.Playback.ToArray());
        Assert.Same(input, first.EnvelopeSource.Audio);
        Assert.Equal(2, first.Playback.Channels);
        var display = SpeechWaveformData.Prepare(first);
        Assert.All(
            display.Envelope.Where(p => p.Seconds >= input.Duration.TotalSeconds),
            p => Assert.Equal(0, p.Amplitude)
        );
        Assert.All(
            display.Stimulus.Upper.Where(p => p.Seconds > input.Duration.TotalSeconds),
            p => Assert.Equal(0, p.Value)
        );
    }
}
