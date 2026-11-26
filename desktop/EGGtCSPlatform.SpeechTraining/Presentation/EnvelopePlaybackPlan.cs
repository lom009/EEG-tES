using System.Collections.Immutable;

namespace EGGtCSPlatform.SpeechTraining;

public sealed record EnvelopePlaybackSettings(
    decimal MaximumCurrent,
    ushort DelayMilliseconds,
    ushort DebugFrequencyHz,
    byte ChannelMask
)
{
    public void Validate()
    {
        if (MaximumCurrent <= 0 || MaximumCurrent > 255 * 0.033m)
            throw new ArgumentOutOfRangeException(nameof(MaximumCurrent), "电流超出包络协议范围。");
        if (DelayMilliseconds > 300 || DebugFrequencyHz is < 200 or > 1000 || ChannelMask == 0)
            throw new ArgumentException("包络频率、延时或通道配置无效。");
    }
}

/// <summary>One frozen source for both device bytes and the preview in milliamps.</summary>
public sealed record EnvelopePlaybackPlan
{
    private EnvelopePlaybackPlan(
        long playbackId,
        EnvelopePlaybackSettings settings,
        ImmutableArray<byte> samples,
        SpeechStimulusPreview preview,
        double audioDuration
    )
    {
        PlaybackId = playbackId;
        Settings = settings;
        Samples = samples;
        Preview = preview;
        AudioDurationSeconds = audioDuration;
    }

    public const ushort SampleRateHz = 100;
    public long PlaybackId { get; }
    public EnvelopePlaybackSettings Settings { get; }
    public ImmutableArray<byte> Samples { get; }
    public SpeechStimulusPreview Preview { get; }
    public double AudioDurationSeconds { get; }
    public TimeSpan Duration =>
        TimeSpan.FromSeconds(Samples.Length / 100d)
        + TimeSpan.FromMilliseconds(Settings.DelayMilliseconds);
    public double DisplayDurationSeconds => Math.Max(AudioDurationSeconds, Duration.TotalSeconds);

    public static EnvelopePlaybackPlan Create(
        long playbackId,
        SpeechWaveformData waveform,
        EnvelopePlaybackSettings settings,
        CancellationToken token = default
    )
    {
        settings.Validate();
        var preparation =
            waveform.Preparation ?? throw new ArgumentException("缺少完整包络准备结果。");
        var timeline = preparation.EnvelopeSource.Timeline;
        // Amplitudes are the full reference's normalized 100 Hz nodes, never display buckets.
        var amplitudes = waveform.Stimulus.Amplitudes;
        var duration = waveform.Audio.Duration.TotalSeconds;
        var count = checked((int)Math.Ceiling(duration * SampleRateHz - 1e-8));
        if (count is <= 0 or > 65518 || amplitudes.Count == 0)
            throw new ArgumentException("包络样本数必须为 1～65518，不能截断或分包。");
        var sourceDuration = preparation.Envelope.Frames / (double)preparation.Envelope.SampleRate;
        var nodes = amplitudes.ToArray();
        if (nodes.Any(a => !double.IsFinite(a) || a is < 0 or > 1))
            throw new ArgumentException("包络幅值必须是 0～1 的有限数值。");
        timeline.Validate();
        var slopes = SpeechStimulusProcessor.PchipSlopes(nodes, token);
        var bytes = ImmutableArray.CreateBuilder<byte>(count);
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var time = i / 100d;
            var local = time - timeline.StartSeconds;
            var amplitude =
                local < 0 || local >= sourceDuration || time >= duration || timeline.IsZero(time)
                    ? 0
                    : SpeechStimulusProcessor.Interpolate(nodes, slopes, local * 100);
            if (!double.IsFinite(amplitude) || amplitude is < 0 or > 1)
                throw new ArgumentException("包络幅值无效。");
            bytes.Add(
                checked((byte)decimal.Floor((decimal)amplitude * settings.MaximumCurrent / 0.033m))
            );
        }
        var samples = bytes.MoveToImmutable();
        var currents = samples.Select(b => (double)(b * 0.033m)).ToArray();
        var preview = SpeechStimulusProcessor.BuildPreview(
            currents,
            count / 100d,
            token,
            settings.DebugFrequencyHz,
            preserveZeroIntervals: true
        );
        return new(playbackId, settings, samples, preview, duration);
    }
}
