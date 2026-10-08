using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace EGGtCSPlatform.Controls;

public class IndexFeatureCard : TemplatedControl
{
    public static readonly StyledProperty<BoxShadows> CardBoxShadowProperty =
        AvaloniaProperty.Register<IndexFeatureCard, BoxShadows>(nameof(CardBoxShadow));

    public BoxShadows CardBoxShadow
    {
        get => GetValue(CardBoxShadowProperty);
        set => SetValue(CardBoxShadowProperty, value);
    }

    public static readonly StyledProperty<IImage?> ImageSourceProperty = AvaloniaProperty.Register<
        IndexFeatureCard,
        IImage?
    >(nameof(ImageSource));

    public IImage? ImageSource
    {
        get => GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<
        IndexFeatureCard,
        string?
    >(nameof(Title));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<
        IndexFeatureCard,
        string?
    >(nameof(Subtitle));

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly StyledProperty<IImage?> StatusIconSourceProperty =
        AvaloniaProperty.Register<IndexFeatureCard, IImage?>(nameof(StatusIconSource));

    public IImage? StatusIconSource
    {
        get => GetValue(StatusIconSourceProperty);
        set => SetValue(StatusIconSourceProperty, value);
    }

    public static readonly StyledProperty<IBrush?> SubtitleBrushProperty =
        AvaloniaProperty.Register<IndexFeatureCard, IBrush?>(
            nameof(SubtitleBrush),
            new SolidColorBrush(Color.Parse("#6B7D99"))
        );

    public IBrush? SubtitleBrush
    {
        get => GetValue(SubtitleBrushProperty);
        set => SetValue(SubtitleBrushProperty, value);
    }

    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<
        IndexFeatureCard,
        ICommand?
    >(nameof(Command));

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<IndexFeatureCard, object?>(nameof(CommandParameter));

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
}
