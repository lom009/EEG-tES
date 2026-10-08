using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace EGGtCSPlatform.Controls;

public class FormInput : TemplatedControl
{
    public static readonly StyledProperty<string> HeaderTextProperty = AvaloniaProperty.Register<
        FormInput,
        string
    >(nameof(HeaderText), "头部文本");

    public static readonly StyledProperty<string?> InputTextProperty = AvaloniaProperty.Register<
        FormInput,
        string?
    >(nameof(InputText), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> WatermarkProperty = AvaloniaProperty.Register<
        FormInput,
        string
    >(nameof(Watermark), "Watermark");

    public static readonly StyledProperty<char> PasswordCharProperty = AvaloniaProperty.Register<
        FormInput,
        char
    >(nameof(PasswordChar), '\0');

    public string HeaderText
    {
        get => GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    public string? InputText
    {
        get => GetValue(InputTextProperty);
        set => SetValue(InputTextProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public char PasswordChar
    {
        get => GetValue(PasswordCharProperty);
        set => SetValue(PasswordCharProperty, value);
    }
}
