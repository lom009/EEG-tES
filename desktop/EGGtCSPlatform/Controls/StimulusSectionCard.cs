using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Metadata;

namespace EGGtCSPlatform.Controls;

public class StimulusSectionCard : TemplatedControl
{
    public static readonly StyledProperty<string?> HeaderProperty = AvaloniaProperty.Register<
        StimulusSectionCard,
        string?
    >(nameof(Header));

    public static readonly StyledProperty<object?> ContentProperty = AvaloniaProperty.Register<
        StimulusSectionCard,
        object?
    >(nameof(Content));

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    [Content]
    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }
}
