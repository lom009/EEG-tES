using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Timing;

namespace EGGtCSPlatform.Audio.Core.Pipeline;

public sealed record AudioBufferSourceOptions(AudioBuffer Buffer);

public sealed record AudioBufferSourceMetadata(
    int SampleRate,
    int Channels,
    int Frames,
    string ContentHash
);

/// <summary>Publishes caller-loaded immutable PCM; performs no I/O or conversion.</summary>
public sealed class AudioBufferSourceProcessor : IAudioProcessor<AudioBufferSourceOptions>
{
    public string Identity => "pcm-buffer-source-1";

    public IReadOnlyDictionary<string, AudioSignalFormat> Describe(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs,
        AudioBufferSourceOptions options,
        string nodeId
    )
    {
        if (inputs.Count != 0)
            throw new ArgumentException("A buffer source must not have inputs.");
        ArgumentNullException.ThrowIfNull(options.Buffer);
        return new Dictionary<string, AudioSignalFormat>
        {
            ["audio"] = new(
                options.Buffer.SampleRate,
                options.Buffer.Channels,
                $"resource:{nodeId}"
            ),
        };
    }

    public ValueTask<AudioProcessResult> ProcessAsync(
        IReadOnlyDictionary<string, AudioSignal> inputs,
        AudioBufferSourceOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        if (inputs.Count != 0)
            throw new ArgumentException("A buffer source must not have inputs.");
        var audio = options.Buffer ?? throw new ArgumentNullException(nameof(options.Buffer));
        return ValueTask.FromResult(
            AudioProcessResult.Single(
                new(audio, new AudioTimeline($"resource:{context.NodeId}")),
                new AudioBufferSourceMetadata(
                    audio.SampleRate,
                    audio.Channels,
                    audio.Frames,
                    audio.ContentHash
                )
            )
        );
    }
}
