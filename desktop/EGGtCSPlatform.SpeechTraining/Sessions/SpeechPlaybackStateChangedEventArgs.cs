namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Playback/output state changed without replacing the sentence, settings, or waveform.</summary>
public sealed class SpeechPlaybackStateChangedEventArgs : EventArgs
{
    internal static readonly SpeechPlaybackStateChangedEventArgs Instance = new();
    private SpeechPlaybackStateChangedEventArgs() { }
}
