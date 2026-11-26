using Avalonia;
using Avalonia.Controls.Primitives;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public sealed class StimulusOverviewCard : TemplatedControl
{
    public static readonly StyledProperty<WaveformDescriptor?> DescriptorProperty =
        AvaloniaProperty.Register<StimulusOverviewCard, WaveformDescriptor?>(nameof(Descriptor));
    public WaveformDescriptor? Descriptor
    {
        get => GetValue(DescriptorProperty);
        set => SetValue(DescriptorProperty, value);
    }

    public static readonly StyledProperty<string> CurrentLabelProperty = AvaloniaProperty.Register<
        StimulusOverviewCard,
        string
    >(nameof(CurrentLabel), string.Empty);
    public string CurrentLabel
    {
        get => GetValue(CurrentLabelProperty);
        set => SetValue(CurrentLabelProperty, value);
    }

    public static readonly StyledProperty<string> CurrentTextProperty = AvaloniaProperty.Register<
        StimulusOverviewCard,
        string
    >(nameof(CurrentText), string.Empty);
    public string CurrentText
    {
        get => GetValue(CurrentTextProperty);
        set => SetValue(CurrentTextProperty, value);
    }

    public static readonly StyledProperty<string> StatusTextProperty = AvaloniaProperty.Register<
        StimulusOverviewCard,
        string
    >(nameof(StatusText), string.Empty);
    public string StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }
}
