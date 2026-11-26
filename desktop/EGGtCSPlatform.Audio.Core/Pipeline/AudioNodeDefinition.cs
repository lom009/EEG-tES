using System.Collections.Immutable;
using EGGtCSPlatform.Audio.Core.Routing;

namespace EGGtCSPlatform.Audio.Core.Pipeline;

public abstract class AudioNodeDefinition
{
    public string Id { get; }
    public ImmutableDictionary<string, AudioOutputRef> Inputs { get; }
    public abstract string ProcessorIdentity { get; }
    public abstract object? Parameters { get; }

    protected AudioNodeDefinition(string id, IReadOnlyDictionary<string, AudioOutputRef> inputs)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "$source")
            throw new ArgumentException("节点 ID 无效。", nameof(id));
        Id = id;
        Inputs = inputs.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public abstract IReadOnlyDictionary<string, AudioSignalFormat> Describe(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs
    );
    public abstract ValueTask<AudioProcessResult> ExecuteAsync(
        IReadOnlyDictionary<string, AudioSignal> inputs,
        CancellationToken token
    );
    protected abstract bool OptionsEqual(AudioNodeDefinition other);

    public bool EquivalentTo(AudioNodeDefinition other) =>
        Id == other.Id
        && ProcessorIdentity == other.ProcessorIdentity
        && Inputs.Count == other.Inputs.Count
        && Inputs.All(p => other.Inputs.TryGetValue(p.Key, out var value) && value == p.Value)
        && OptionsEqual(other);
}

public sealed class AudioNodeDefinition<TOptions>(
    string id,
    IAudioProcessor<TOptions> processor,
    TOptions options,
    IReadOnlyDictionary<string, AudioOutputRef> inputs
) : AudioNodeDefinition(id, inputs)
{
    public TOptions Options { get; } = options;
    public override string ProcessorIdentity => processor.Identity;
    public override object? Parameters => Options;

    public override IReadOnlyDictionary<string, AudioSignalFormat> Describe(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs
    ) => processor.Describe(inputs, Options, Id);

    public override ValueTask<AudioProcessResult> ExecuteAsync(
        IReadOnlyDictionary<string, AudioSignal> inputs,
        CancellationToken token
    ) => processor.ProcessAsync(inputs, Options, new(Id), token);

    protected override bool OptionsEqual(AudioNodeDefinition other) =>
        other is AudioNodeDefinition<TOptions> typed
        && EqualityComparer<TOptions>.Default.Equals(Options, typed.Options);
}
