namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Prepared together off the UI thread and retained for replay of the current sentence.</summary>
public sealed record SpeechWaveformData(
    AudioBuffer Audio,
    IReadOnlyList<SpeechWaveformBucket> Voice,
    IReadOnlyList<SpeechEnvelopePoint> Envelope,
    SpeechStimulusPreview Stimulus
)
{
    public TrainingAudioPreparationResult? Preparation { get; init; }

    public static SpeechWaveformData Prepare(
        TrainingAudioPreparationResult prepared,
        CancellationToken token = default
    ) => Prepare(SpeechAudioAnalysis.Prepare(prepared, token), token);

    public static SpeechWaveformData Prepare(
        SpeechAudioAnalysis analysis,
        CancellationToken token = default
    )
    {
        var prepared = analysis.Preparation;
        prepared.Validate();
        var audio = prepared.Playback;
        var reference = prepared.EnvelopeSource;
        var envelope = analysis.Envelope;
        if (envelope.Length != reference.Audio.Data.Length / (reference.Audio.Channels * 2))
            throw new ArgumentException("包络长度与参考音频不一致。", nameof(analysis));
        var voice = audio.GetWaveform();
        var frames = audio.Data.Length / (audio.Channels * 2);
        var stride = Math.Max(1, (int)Math.Ceiling(frames / 1200d));
        var points = new List<SpeechEnvelopePoint>(voice.Count);
        for (var start = 0; start < frames; start += stride)
        {
            token.ThrowIfCancellationRequested();
            var maximum = 0d;
            var from = Math.Clamp(
                (int)
                    Math.Ceiling(
                        ((double)start / audio.SampleRate - reference.Timeline.StartSeconds)
                            * reference.Audio.SampleRate
                            - 1e-8
                    ),
                0,
                envelope.Length
            );
            var to = Math.Clamp(
                (int)
                    Math.Ceiling(
                        (
                            (double)Math.Min(start + stride, frames) / audio.SampleRate
                            - reference.Timeline.StartSeconds
                        ) * reference.Audio.SampleRate
                            - 1e-8
                    ),
                0,
                envelope.Length
            );
            for (var i = from; i < to; i++)
                if (
                    !reference.Timeline.IsZero(
                        reference.Timeline.StartSeconds + (double)i / reference.Audio.SampleRate
                    )
                )
                    maximum = Math.Max(maximum, envelope[i]);
            points.Add(new((double)start / audio.SampleRate, maximum));
        }
        var stimulus = SpeechStimulusProcessor.Prepare(envelope, reference.Audio.SampleRate, token);
        stimulus = AlignStimulus(stimulus, reference.Timeline, audio.Duration.TotalSeconds, token);
        return new(audio, voice, points.AsReadOnly(), stimulus) { Preparation = prepared };
    }

    // Alignment happens AFTER amplitude normalization. A cue or reverb tail never becomes a
    // normalization sample. Duplicate boundary points express discontinuities without diagonal ramps.
    private static SpeechStimulusPreview AlignStimulus(
        SpeechStimulusPreview stimulus,
        AudioTimeline alignment,
        double duration,
        CancellationToken token
    )
    {
        if (stimulus.Waveform.Count == 0)
            return stimulus;
        var sourceEnd = stimulus.Waveform[^1].Seconds + alignment.StartSeconds;
        if (
            alignment.StartSeconds == 0
            && alignment.Regions.IsEmpty
            && Math.Abs(sourceEnd - duration) < 1e-12
        )
            return stimulus;
        var boundaries = new SortedSet<double> { 0, duration };
        boundaries.Add(Math.Clamp(alignment.StartSeconds, 0, duration));
        boundaries.Add(Math.Clamp(sourceEnd, 0, duration));
        foreach (var region in alignment.Regions)
        {
            boundaries.Add(Math.Clamp(region.StartSeconds, 0, duration));
            boundaries.Add(Math.Clamp(region.EndSeconds, 0, duration));
        }
        var times = new SortedSet<double>(boundaries);
        foreach (var point in stimulus.Waveform)
        {
            var t = point.Seconds + alignment.StartSeconds;
            if (t >= 0 && t <= duration)
                times.Add(t);
        }
        IReadOnlyList<SpeechStimulusPoint> Align(IReadOnlyList<SpeechStimulusPoint> input)
        {
            var result = new List<SpeechStimulusPoint>(times.Count + boundaries.Count);
            double At(double t)
            {
                if (t < alignment.StartSeconds || t > sourceEnd || alignment.IsZero(t))
                    return 0;
                var local = t - alignment.StartSeconds;
                var lo = 0;
                var hi = input.Count - 1;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (input[mid].Seconds < local)
                        lo = mid + 1;
                    else
                        hi = mid;
                }
                if (lo == 0)
                    return input[0].Value;
                var a = input[lo - 1];
                var b = input[lo];
                return a.Value
                    + (b.Value - a.Value) * (local - a.Seconds) / (b.Seconds - a.Seconds);
            }
            foreach (var t in times)
            {
                token.ThrowIfCancellationRequested();
                if (boundaries.Contains(t) && t > 0)
                    result.Add(new(t, At(t - 1e-10)));
                result.Add(new(t, At(t)));
                if (boundaries.Contains(t) && t < duration)
                    result.Add(new(t, At(t + 1e-10)));
            }
            return result.AsReadOnly();
        }
        return new(
            stimulus.Amplitudes,
            Align(stimulus.Waveform),
            Align(stimulus.Upper),
            Align(stimulus.Lower)
        );
    }
}

public sealed record SpeechEnvelopePoint(double Seconds, double Amplitude);
