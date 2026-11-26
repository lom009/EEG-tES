using System.Collections.Immutable;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Timing;

namespace EGGtCSPlatform.Audio.Core.Pipeline;

/// <summary>A sampled signal with explicit representation and its source time relationship.</summary>
public record AudioSignal
{
    public ISignalBuffer Buffer { get; private init; }
    public AudioTimeline Timeline { get; init; }

    // Retained for PCM consumers. Analysis consumers use the typed accessors.
    public AudioBuffer Audio
    {
        get => GetAudio();
        init
        {
            if (Buffer is not AudioBuffer)
                throw new InvalidOperationException(
                    "Cannot replace an analysis signal with PCM16."
                );
            Buffer = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    public AudioSignal(AudioBuffer audio, AudioTimeline timeline)
        : this((ISignalBuffer)audio, timeline) { }

    protected AudioSignal(ISignalBuffer buffer, AudioTimeline timeline)
    {
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
    }

    public AudioBuffer GetAudio() =>
        Buffer as AudioBuffer ?? throw new InvalidOperationException("The signal is not PCM16.");

    public FloatBuffer GetFloat() =>
        Buffer as FloatBuffer ?? throw new InvalidOperationException("The signal is not Float64.");

    public ComplexBuffer GetComplex() =>
        Buffer as ComplexBuffer
        ?? throw new InvalidOperationException("The signal is not Complex128.");

    public sealed override string ToString() =>
        $"{Buffer.SampleType}: {Buffer.SampleRate} Hz, {Buffer.Channels} channels, {Buffer.Frames} frames ({Timeline.TimeDomain})";
}

public sealed record FloatSignal : AudioSignal
{
    public FloatSignal(FloatBuffer buffer, AudioTimeline timeline)
        : base(buffer, timeline) { }
}

public sealed record ComplexSignal : AudioSignal
{
    public ComplexSignal(ComplexBuffer buffer, AudioTimeline timeline)
        : base(buffer, timeline) { }
}

/// <summary>Static descriptor for validating the complete plan before executing DSP.</summary>
public sealed record AudioSignalFormat(
    int SampleRate,
    int Channels,
    string TimeDomain,
    SignalSampleType SampleType = SignalSampleType.Pcm16
);

public sealed record AudioProcessingContext(string NodeId);

public sealed record AudioProcessResult(
    ImmutableDictionary<string, AudioSignal> Outputs,
    object? Metadata = null
)
{
    public static AudioProcessResult Single(AudioSignal signal, object? metadata = null) =>
        new(ImmutableDictionary<string, AudioSignal>.Empty.Add("audio", signal), metadata);
}

/// <summary>Options must be immutable value objects. Identity must include the algorithm version.</summary>
public interface IAudioProcessor<TOptions>
{
    string Identity { get; }
    IReadOnlyDictionary<string, AudioSignalFormat> Describe(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs,
        TOptions options,
        string nodeId
    );
    ValueTask<AudioProcessResult> ProcessAsync(
        IReadOnlyDictionary<string, AudioSignal> inputs,
        TOptions options,
        AudioProcessingContext context,
        CancellationToken token
    );
}
