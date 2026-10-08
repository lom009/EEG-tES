using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace EGGtCSPlatform.Controls;

public class StimulusModeItem : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<
        StimulusModeItem,
        string?
    >(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<
        StimulusModeItem,
        string?
    >(nameof(Subtitle));

    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<
        StimulusModeItem,
        bool
    >(nameof(IsSelected));

    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<
        StimulusModeItem,
        ICommand?
    >(nameof(Command));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsSelectedProperty)
            PseudoClasses.Set(":selected", change.GetNewValue<bool>());
    }
}
