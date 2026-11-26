using EGGtCSPlatform.Audio.Core.Buffers;

namespace EGGtCSPlatform.Audio.Core.Pipeline;

public abstract class SingleInputAudioProcessor<TOptions> : IAudioProcessor<TOptions>
{
    public abstract string Identity { get; }
    protected virtual SignalSampleType InputType => SignalSampleType.Pcm16;
    protected abstract AudioSignalFormat Describe(
        AudioSignalFormat input,
        TOptions options,
        string nodeId
    );
    protected abstract AudioProcessResult Process(
        AudioSignal input,
        TOptions options,
        AudioProcessingContext context,
        CancellationToken token
    );

    protected virtual IReadOnlyDictionary<string, AudioSignalFormat> DescribeOutputs(
        AudioSignalFormat input,
        TOptions options,
        string nodeId
    ) => new Dictionary<string, AudioSignalFormat> { ["audio"] = Describe(input, options, nodeId) };

    public IReadOnlyDictionary<string, AudioSignalFormat> Describe(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs,
        TOptions options,
        string nodeId
    )
    {
        if (inputs.Count != 1 || !inputs.TryGetValue("audio", out var input))
            throw new ArgumentException("处理器需要一个名为 audio 的输入。");
        if (input.SampleType != InputType)
            throw new ArgumentException(
                $"{Identity} requires {InputType}, received {input.SampleType}."
            );
        return DescribeOutputs(input, options, nodeId);
    }

    public ValueTask<AudioProcessResult> ProcessAsync(
        IReadOnlyDictionary<string, AudioSignal> inputs,
        TOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        if (
            inputs.Count != 1
            || !inputs.TryGetValue("audio", out var input)
            || input.Buffer.SampleType != InputType
        )
            throw new ArgumentException($"{Identity} requires one {InputType} input.");
        return ValueTask.FromResult(Process(input, options, context, token));
    }
}
