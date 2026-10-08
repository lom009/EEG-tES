using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace EGGtCSPlatform.Controls;

/// <summary>
/// Numeric editor used by stimulus parameters. It deliberately accepts only
/// unsigned decimal input because all stimulus ranges exposed by the UI are
/// non-negative; polarity is selected separately.
/// </summary>
public class StimulusNumericInput : NumericUpDown
{
    private const decimal BoundaryTolerance = 0.000000000001m;

    public static readonly StyledProperty<int> DecimalPlacesProperty = AvaloniaProperty.Register<
        StimulusNumericInput,
        int
    >(nameof(DecimalPlaces), 1);

    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<
        StimulusNumericInput,
        string?
    >(nameof(Unit));

    private TextBox? _textBox;
    private ButtonSpinner? _spinner;
    private string _lastValidText = string.Empty;
    private bool _isRestoringText;

    public StimulusNumericInput()
    {
        UpdateFormatString(DecimalPlaces);
    }

    public int DecimalPlaces
    {
        get => GetValue(DecimalPlacesProperty);
        set => SetValue(DecimalPlacesProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_textBox is not null)
        {
            _textBox.RemoveHandler(TextInputEvent, OnTextInput);
            _textBox.TextChanged -= OnTextChanged;
        }

        base.OnApplyTemplate(e);
        _textBox = e.NameScope.Find<TextBox>("PART_TextBox");
        _spinner = e.NameScope.Find<ButtonSpinner>("PART_Spinner");
        UpdateValidSpinDirections();
        if (_textBox is null)
            return;

        _lastValidText = IsValidText(_textBox.Text) ? _textBox.Text ?? string.Empty : string.Empty;
        _textBox.AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        _textBox.TextChanged += OnTextChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DecimalPlacesProperty)
        {
            var places = Math.Clamp(change.GetNewValue<int>(), 0, 6);
            if (places != DecimalPlaces)
            {
                SetCurrentValue(DecimalPlacesProperty, places);
                return;
            }

            UpdateFormatString(places);
        }

        if (
            change.Property == ValueProperty
            || change.Property == MinimumProperty
            || change.Property == MaximumProperty
            || change.Property == IsReadOnlyProperty
            || change.Property == IsEnabledProperty
        )
        {
            UpdateValidSpinDirections();
        }
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_textBox is null || string.IsNullOrEmpty(e.Text))
            return;

        var current = _textBox.Text ?? string.Empty;
        var start = Math.Min(_textBox.SelectionStart, _textBox.SelectionEnd);
        var end = Math.Max(_textBox.SelectionStart, _textBox.SelectionEnd);
        var candidate = current.Remove(start, end - start).Insert(start, e.Text);
        if (!IsValidText(candidate))
            e.Handled = true;
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_textBox is null || _isRestoringText)
            return;

        var text = _textBox.Text ?? string.Empty;
        if (IsValidText(text))
        {
            _lastValidText = text;
            return;
        }

        _isRestoringText = true;
        var caret = Math.Min(_lastValidText.Length, _textBox.CaretIndex);
        _textBox.Text = _lastValidText;
        _textBox.CaretIndex = caret;
        _isRestoringText = false;
    }

    private bool IsValidText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return true;

        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var separatorIndex = text.IndexOf(separator, StringComparison.CurrentCulture);
        if (
            separatorIndex >= 0
            && text.IndexOf(
                separator,
                separatorIndex + separator.Length,
                StringComparison.CurrentCulture
            ) >= 0
        )
            return false;

        if (separatorIndex >= 0 && DecimalPlaces == 0)
            return false;

        var fractionLength =
            separatorIndex < 0 ? 0 : text.Length - separatorIndex - separator.Length;
        if (fractionLength > DecimalPlaces)
            return false;

        for (var index = 0; index < text.Length; index++)
        {
            if (
                separatorIndex >= 0
                && index >= separatorIndex
                && index < separatorIndex + separator.Length
            )
                continue;
            if (!char.IsDigit(text[index]))
                return false;
        }

        return true;
    }

    private void UpdateFormatString(int places)
    {
        FormatString = places == 0 ? "0" : $"0.{new string('#', places)}";
    }

    private void UpdateValidSpinDirections()
    {
        if (_spinner is null || !IsEnabled || IsReadOnly || Value is not { } value)
        {
            if (_spinner is not null)
                _spinner.ValidSpinDirection = ValidSpinDirections.None;
            return;
        }

        var directions = ValidSpinDirections.None;
        if (value < Maximum - BoundaryTolerance)
            directions |= ValidSpinDirections.Increase;
        if (value > Minimum + BoundaryTolerance)
            directions |= ValidSpinDirections.Decrease;

        _spinner.ValidSpinDirection = directions;
    }
}
