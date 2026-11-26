using EGGtCSPlatform.Audio.Core.Buffers;
using SDL3;

namespace EGGtCSPlatform.Audio.Sdl;

/// <summary>One bounded microphone capture in the PCM format required by the recognizer.</summary>
public sealed class SdlSpeechRecorder : IDisposable
{
    public const int SampleRate = 16000;
    public const int MaximumSeconds = 12;
    private const int MaximumBytes = SampleRate * 2 * MaximumSeconds;
    private nint _stream;
    private bool _initialized;

    public SdlSpeechRecorder()
    {
        Check(SDL.InitSubSystem(SDL.InitFlags.Audio));
        _initialized = true;
        try
        {
            var format = new SDL.AudioSpec
            {
                Format = SDL.AudioFormat.AudioS16LE,
                Channels = 1,
                Freq = SampleRate,
            };
            _stream = SDL.OpenAudioDeviceStream(
                SDL.AudioDeviceDefaultRecording, in format, null!, IntPtr.Zero
            );
            Check(_stream != 0);
            Check(SDL.ResumeAudioStreamDevice(_stream));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public AudioBuffer Complete()
    {
        ObjectDisposedException.ThrowIf(_stream == 0, this);
        try
        {
            Check(SDL.PauseAudioStreamDevice(_stream));
            Check(SDL.FlushAudioStream(_stream));
            var available = SDL.GetAudioStreamAvailable(_stream);
            Check(available >= 0);
            var length = Math.Min(available, MaximumBytes) & ~1;
            var pcm = new byte[length];
            var read = length == 0 ? 0 : SDL.GetAudioStreamData(_stream, pcm, length);
            Check(read >= 0);
            return new AudioBuffer(SampleRate, 1, pcm.AsSpan(0, read & ~1));
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_stream != 0)
        {
            SDL.DestroyAudioStream(_stream);
            _stream = 0;
        }
        if (_initialized)
        {
            SDL.QuitSubSystem(SDL.InitFlags.Audio);
            _initialized = false;
        }
    }

    private static void Check(bool success)
    {
        if (!success)
            throw new IOException(SDL.GetError());
    }
}
