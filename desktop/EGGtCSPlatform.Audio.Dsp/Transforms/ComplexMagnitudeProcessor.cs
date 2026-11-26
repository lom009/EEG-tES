using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.Transforms;

public sealed record ComplexMagnitudeOptions;

public sealed class ComplexMagnitudeProcessor : SingleInputAudioProcessor<ComplexMagnitudeOptions>
{
    public override string Identity => "complex-magnitude-1";
    protected override SignalSampleType InputType => SignalSampleType.Complex128;

    protected override AudioSignalFormat Describe(
        AudioSignalFormat input,
        ComplexMagnitudeOptions options,
        string nodeId
    ) => input with { SampleType = SignalSampleType.Float64 };

    protected override AudioProcessResult Process(
        AudioSignal input,
        ComplexMagnitudeOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        var source = input.GetComplex();
        var output = new double[source.Frames * source.Channels];
        for (var i = 0; i < output.Length; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            output[i] = source.Data[i].Magnitude;
            if (!double.IsFinite(output[i]))
                throw new InvalidOperationException("语音包络计算产生非有限数值。");
        }
        return AudioProcessResult.Single(
            new FloatSignal(new(source.SampleRate, source.Channels, output), input.Timeline)
        );
    }
}
