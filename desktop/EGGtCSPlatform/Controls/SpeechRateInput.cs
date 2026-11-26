using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace EGGtCSPlatform.Controls;

/// <summary>Speech rate accepts digits only, including edits made by paste.</summary>
public sealed class SpeechRateInput : StepNumericUpDown
{
    private TextBox? _editor;

    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    public SpeechRateInput()
    {
        Minimum = 0;
        Increment = 1;
        FormatString = "0";
        ParsingNumberStyle = NumberStyles.None;
        AddHandler(TextInputEvent, FilterTextInput, RoutingStrategies.Tunnel);
        LostFocus += (_, _) =>
        {
            if (string.IsNullOrEmpty(Text))
            {
                SetCurrentValue(ValueProperty, 0m);
                SetCurrentValue(TextProperty, "0");
            }
        };
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_editor is not null)
            _editor.TextChanged -= OnEditorTextChanged;
        base.OnApplyTemplate(e);
        _editor = e.NameScope.Find<TextBox>("PART_TextBox");
        if (_editor is not null)
        {
            _editor.TextChanged += OnEditorTextChanged;
        }
    }

    private static bool IsValidText(string? text) =>
        string.IsNullOrEmpty(text)
        || (
            text.All(c => c >= '0' && c <= '9')
            && decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)
        );

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_editor is null)
            return;
        if (!IsValidText(_editor.Text))
        {
            var restored = Value?.ToString("0", CultureInfo.InvariantCulture) ?? "0";
            var caret = Math.Min(_editor.CaretIndex, restored.Length);
            _editor.SetCurrentValue(TextBox.TextProperty, restored);
            _editor.CaretIndex = caret;
        }
    }

    private void FilterTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text?.Any(c => c < '0' || c > '9') == true)
            e.Handled = true;
    }

    protected override void OnTextChanged(string? oldValue, string? newValue)
    {
        if (!IsValidText(newValue))
        {
            SetCurrentValue(TextProperty, oldValue ?? "0");
            return;
        }
        base.OnTextChanged(oldValue, newValue);
    }

    protected override decimal? OnCoerceValue(decimal? value) =>
        base.OnCoerceValue(value is { } number ? Math.Max(0, decimal.Truncate(number)) : null);
}
