using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.Controls;

/// <summary>A static curve cache whose lifetime follows its data, not playback progress.</summary>
public sealed class SpeechWaveformCache : Decorator
{
    public static readonly StyledProperty<object?> SourceProperty =
        AvaloniaProperty.Register<SpeechWaveformCache, object?>(nameof(Source));
    public static readonly StyledProperty<object?> SecondarySourceProperty =
        AvaloniaProperty.Register<SpeechWaveformCache, object?>(nameof(SecondarySource));
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<SpeechWaveformCache, double>(nameof(Duration));
    public static readonly StyledProperty<double> AmplitudeProperty =
        AvaloniaProperty.Register<SpeechWaveformCache, double>(nameof(Amplitude));
    public static readonly StyledProperty<double> CurrentProperty =
        AvaloniaProperty.Register<SpeechWaveformCache, double>(nameof(Current));
    public static readonly StyledProperty<double> DelayProperty =
        AvaloniaProperty.Register<SpeechWaveformCache, double>(nameof(Delay));

    public object? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public object? SecondarySource { get => GetValue(SecondarySourceProperty); set => SetValue(SecondarySourceProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Amplitude { get => GetValue(AmplitudeProperty); set => SetValue(AmplitudeProperty, value); }
    public double Current { get => GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }
    public double Delay { get => GetValue(DelayProperty); set => SetValue(DelayProperty, value); }

    public SpeechWaveformCache() => CacheMode = new BitmapCache();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Replaying creates a new output plan, even when its preview is identical.
        if (change.Property == SourceProperty
            && change.OldValue is SpeechStimulusPreview previous
            && change.NewValue is SpeechStimulusPreview next
            && previous.Amplitudes.SequenceEqual(next.Amplitudes)
            && previous.Waveform.SequenceEqual(next.Waveform)
            && previous.Upper.SequenceEqual(next.Upper)
            && previous.Lower.SequenceEqual(next.Lower))
            return;
        if (change.Property == SourceProperty || change.Property == SecondarySourceProperty
            || change.Property == DurationProperty || change.Property == AmplitudeProperty
            || change.Property == CurrentProperty || change.Property == DelayProperty)
        {
            CacheMode = new BitmapCache();
            InvalidateVisual();
        }
    }
}
