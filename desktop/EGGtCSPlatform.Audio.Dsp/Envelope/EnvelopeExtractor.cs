using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Dsp.Filtering;

namespace EGGtCSPlatform.Audio.Dsp.Envelope;

public static class EnvelopeExtractor
{
    public static double[] Extract(
        AudioBuffer audio,
        CancellationToken cancellationToken = default
    ) => Extract(audio, new EnvelopeOptions(), cancellationToken);

    public static double[] Extract(
        AudioBuffer audio,
        EnvelopeOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(audio);
        var graph = new AudioPipelineBuilder()
            .Then("envelope", new EnvelopeProcessor(), options)
            .Output("envelope")
            .Build();
        return new AudioPipelineExecutor()
            .ExecuteAsync(graph, audio, cancellationToken)
            .GetAwaiter()
            .GetResult()
            .GetFloat("envelope")
            .ToArray();
    }

    // Research-test entrypoint uses the same kernel as the public filter processor.
    internal static double[] Filter(
        double[] samples,
        int sampleRate,
        CancellationToken token = default
    ) => ButterworthFilter.Apply(samples, sampleRate, token);
}
