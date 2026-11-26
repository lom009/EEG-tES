using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.IO;

/// <summary>PCM16 conversion with the same rounding and clipping boundaries as DSP nodes.</summary>
public static class Pcm16Codec
{
    public static double[][] Decode(AudioBuffer audio) => VocoderDsp.Decode(audio);

    public static AudioBuffer Encode(double[][] channels, int sampleRate, out bool clipped) =>
        VocoderDsp.Encode(channels, sampleRate, out clipped);
}
