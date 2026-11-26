using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Dsp.Conversion;
using EGGtCSPlatform.Audio.Dsp.Filtering;
using EGGtCSPlatform.Audio.Dsp.Transforms;

namespace EGGtCSPlatform.Audio.Dsp.Envelope;

public sealed record EnvelopeOptions(
    double CutoffHz = 150,
    int FilterOrder = 5,
    bool EnableFilter = false,
    ChannelMixMode ChannelMode = ChannelMixMode.Average,
    int ChannelIndex = 0
)
{
    public void Validate()
    {
        if (
            !double.IsFinite(CutoffHz)
            || CutoffHz <= 0
            || FilterOrder is < 1 or > 12
            || !Enum.IsDefined(ChannelMode)
            || ChannelMode == ChannelMixMode.Select && ChannelIndex < 0
        )
            throw new ArgumentException("Invalid envelope options.");
    }
}

/// <summary>Expands into public analysis nodes in the caller's graph; does not run a nested executor.</summary>
public sealed class EnvelopeProcessor : IAudioCompositeProcessor<EnvelopeOptions>
{
    public void Expand(AudioPipelineBuilder branch, string id, EnvelopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        branch
            .Then($"{id}/decode", new PcmDecodeProcessor(), new PcmDecodeOptions())
            .Then(
                $"{id}/channels",
                new ChannelMixProcessor(),
                new ChannelMixOptions(options.ChannelMode, options.ChannelIndex)
            );
        if (options.EnableFilter)
            branch.Then(
                $"{id}/filter",
                new ButterworthFilterProcessor(),
                new ButterworthFilterOptions(options.CutoffHz, options.FilterOrder)
            );
        branch
            .Then($"{id}/hilbert", new HilbertTransformProcessor(), new HilbertTransformOptions())
            .Then(
                $"{id}/magnitude",
                new ComplexMagnitudeProcessor(),
                new ComplexMagnitudeOptions()
            );
    }
}
