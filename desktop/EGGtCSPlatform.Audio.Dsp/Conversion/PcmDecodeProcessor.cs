using System.Buffers.Binary;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.Conversion;

public sealed record PcmDecodeOptions;

public sealed class PcmDecodeProcessor : SingleInputAudioProcessor<PcmDecodeOptions>
{
    public override string Identity => "pcm16-double-decode-1";

    protected override AudioSignalFormat Describe(
        AudioSignalFormat input,
        PcmDecodeOptions options,
        string nodeId
    ) => input with { SampleType = SignalSampleType.Float64 };

    protected override AudioProcessResult Process(
        AudioSignal input,
        PcmDecodeOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        var audio = input.GetAudio();
        var samples = new double[audio.Frames * audio.Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            samples[i] =
                BinaryPrimitives.ReadInt16LittleEndian(audio.Data.Slice(i * 2, 2)) / 32768d;
        }
        return AudioProcessResult.Single(
            new FloatSignal(new(audio.SampleRate, audio.Channels, samples), input.Timeline)
        );
    }
}
