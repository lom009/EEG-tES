using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public sealed class StimulusOverviewCard : TemplatedControl
{
    public static readonly DirectProperty<StimulusOverviewCard, bool> IsDirectCurrentProperty =
        AvaloniaProperty.RegisterDirect<StimulusOverviewCard, bool>(nameof(IsDirectCurrent), x => x.IsDirectCurrent);
    public bool IsDirectCurrent => Descriptor?.Kind == StimulusKind.TDcs;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DescriptorProperty)
            RaisePropertyChanged(IsDirectCurrentProperty,
                change.GetOldValue<WaveformDescriptor?>()?.Kind == StimulusKind.TDcs, IsDirectCurrent);
    }

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
