using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public sealed class ExperimentDurationInput : NumericUpDown
{
    public static readonly StyledProperty<decimal?> CommittedValueProperty =
        AvaloniaProperty.Register<ExperimentDurationInput, decimal?>(
            nameof(CommittedValue),
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<DurationUnitOption?> UnitProperty =
        AvaloniaProperty.Register<ExperimentDurationInput, DurationUnitOption?>(nameof(Unit));

    public static readonly StyledProperty<int> StepMillisecondsProperty = AvaloniaProperty.Register<
        ExperimentDurationInput,
        int
    >(nameof(StepMilliseconds), 1000);

    public static readonly StyledProperty<int> MaximumMillisecondsProperty =
        AvaloniaProperty.Register<ExperimentDurationInput, int>(
            nameof(MaximumMilliseconds),
            65535000
        );

    public static readonly StyledProperty<int> MinimumMillisecondsProperty =
        AvaloniaProperty.Register<ExperimentDurationInput, int>(nameof(MinimumMilliseconds), 1000);

    private TextBox? _textBox;
    private string _lastNumericText = string.Empty;
    private decimal? _lastPositiveCommittedValue;
    private bool _hasInvalidStepValue;
    private bool _isSynchronizing;
    private bool _isRestoringText;

    public ExperimentDurationInput()
    {
        Minimum = 0;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        LostFocus += OnInputLostFocus;
        UpdateUnitSettings();
    }

    public decimal? CommittedValue
    {
        get => GetValue(CommittedValueProperty);
        set => SetValue(CommittedValueProperty, value);
    }

    public DurationUnitOption? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public int StepMilliseconds
    {
        get => GetValue(StepMillisecondsProperty);
        set => SetValue(StepMillisecondsProperty, value);
    }

    public int MaximumMilliseconds
    {
        get => GetValue(MaximumMillisecondsProperty);
        set => SetValue(MaximumMillisecondsProperty, value);
    }

    public int MinimumMilliseconds
    {
        get => GetValue(MinimumMillisecondsProperty);
        set => SetValue(MinimumMillisecondsProperty, value);
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
        if (_textBox is null)
            return;

        _lastNumericText = IsNumericText(_textBox.Text, DecimalPlaces())
            ? _textBox.Text ?? string.Empty
            : string.Empty;
        _textBox.AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        _textBox.TextChanged += OnTextChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CommittedValueProperty)
        {
            var value = change.GetNewValue<decimal?>();
            if (value is > 0)
                _lastPositiveCommittedValue = value;
            SynchronizeValue(value);
        }
        else if (
            change.Property == UnitProperty
            || change.Property == StepMillisecondsProperty
            || change.Property == MinimumMillisecondsProperty
            || change.Property == MaximumMillisecondsProperty
        )
        {
            UpdateUnitSettings();
            SynchronizeValue(CommittedValue);
        }
    }

    protected override void OnValueChanged(decimal? oldValue, decimal? newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (_isSynchronizing)
            return;

        if (newValue is null)
        {
            _hasInvalidStepValue = false;
            SetCurrentValue(CommittedValueProperty, null);
            return;
        }

        if (
            Unit is not null
            && ExperimentDurationMath.IsAboveMaximum(
                newValue.Value,
                Unit.Value,
                MaximumMilliseconds
            )
        )
        {
            _hasInvalidStepValue = true;
            return;
        }

        if (TryNormalize(newValue.Value, out var normalized))
        {
            _hasInvalidStepValue = false;
            _lastPositiveCommittedValue = normalized;
            SetCurrentValue(CommittedValueProperty, normalized);
        }
        else
        {
            _hasInvalidStepValue = true;
        }
    }

    protected override void OnSpin(SpinEventArgs e)
    {
        e.Handled = true;
        if (!IsEnabled || IsReadOnly || Unit is null || StepMilliseconds <= 0)
            return;

        var currentMilliseconds =
            CommittedValue is { } current
            && ExperimentDurationMath.TryToStepMilliseconds(
                current,
                Unit.Value,
                StepMilliseconds,
                MinimumMilliseconds,
                MaximumMilliseconds,
                out var converted
            )
                ? converted
                : 0m;
        var targetMilliseconds =
            e.Direction == SpinDirection.Increase
                ? currentMilliseconds + StepMilliseconds
                : currentMilliseconds - StepMilliseconds;
        if (targetMilliseconds < MinimumMilliseconds || targetMilliseconds > MaximumMilliseconds)
            return;

        var display = ExperimentDurationMath.ToDisplayValue(targetMilliseconds, Unit.Value);
        _hasInvalidStepValue = false;
        _lastPositiveCommittedValue = display;
        SetCurrentValue(CommittedValueProperty, display);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitOrRestore();
        base.OnKeyDown(e);
    }

    internal static bool IsNumericText(string? text, int decimalPlaces)
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
        if (separatorIndex >= 0 && decimalPlaces == 0)
            return false;

        var fractionLength =
            separatorIndex < 0 ? 0 : text.Length - separatorIndex - separator.Length;
        if (fractionLength > decimalPlaces)
            return false;

        for (var index = 0; index < text.Length; index++)
        {
            if (
                separatorIndex >= 0
                && index >= separatorIndex
                && index < separatorIndex + separator.Length
            )
                continue;
            if (text[index] is < '0' or > '9')
                return false;
        }

        return true;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_textBox is null || string.IsNullOrEmpty(e.Text))
            return;

        var current = _textBox.Text ?? string.Empty;
        var start = Math.Min(_textBox.SelectionStart, _textBox.SelectionEnd);
        var end = Math.Max(_textBox.SelectionStart, _textBox.SelectionEnd);
        var candidate = current.Remove(start, end - start).Insert(start, e.Text);
        if (!IsNumericText(candidate, DecimalPlaces()))
            e.Handled = true;
    }

    private void OnInputLostFocus(object? sender, RoutedEventArgs e) => CommitOrRestore();

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_textBox is null || _isRestoringText)
            return;

        var text = _textBox.Text ?? string.Empty;
        if (IsNumericText(text, DecimalPlaces()))
        {
            _lastNumericText = text;
            return;
        }

        _isRestoringText = true;
        var caret = Math.Min(_lastNumericText.Length, _textBox.CaretIndex);
        _textBox.Text = _lastNumericText;
        _textBox.CaretIndex = caret;
        _isRestoringText = false;
    }

    private bool TryNormalize(decimal displayValue, out decimal normalized)
    {
        normalized = 0;
        if (
            Unit is null
            || !ExperimentDurationMath.TryToStepMilliseconds(
                displayValue,
                Unit.Value,
                StepMilliseconds,
                MinimumMilliseconds,
                MaximumMilliseconds,
                out var milliseconds
            )
        )
            return false;

        normalized = ExperimentDurationMath.ToDisplayValue(milliseconds, Unit.Value);
        return true;
    }

    private void CommitOrRestore()
    {
        if (
            TryReadDisplayText(out var displayValue)
            && Unit is not null
            && ExperimentDurationMath.IsBelowMinimum(displayValue, Unit.Value, MinimumMilliseconds)
        )
        {
            ApplyLimit(MinimumMilliseconds);
            return;
        }

        if (
            TryReadDisplayText(out displayValue)
            && Unit is not null
            && ExperimentDurationMath.IsAboveMaximum(displayValue, Unit.Value, MaximumMilliseconds)
        )
        {
            ApplyLimit(MaximumMilliseconds);
            return;
        }

        if (!_hasInvalidStepValue)
            return;

        _hasInvalidStepValue = false;
        var restore = _lastPositiveCommittedValue ?? CommittedValue;
        SetCurrentValue(CommittedValueProperty, restore);
        SynchronizeValue(restore);
    }

    private void ApplyLimit(int milliseconds)
    {
        if (Unit is null)
            return;
        var value = ExperimentDurationMath.ToDisplayValue(milliseconds, Unit.Value);
        _hasInvalidStepValue = false;
        _lastPositiveCommittedValue = value;
        SetCurrentValue(CommittedValueProperty, value);
        SynchronizeValue(value);
    }

    private bool TryReadDisplayText(out decimal value)
    {
        value = 0;
        var text = _textBox?.Text;
        return IsNumericText(text, DecimalPlaces())
            && decimal.TryParse(
                text,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.CurrentCulture,
                out value
            );
    }

    private void SynchronizeValue(decimal? value)
    {
        _isSynchronizing = true;
        try
        {
            SetCurrentValue(ValueProperty, value);
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void UpdateUnitSettings()
    {
        var places = DecimalPlaces();
        FormatString = places == 0 ? "0" : $"0.{new string('#', places)}";
        if (Unit is not null && StepMilliseconds > 0)
        {
            Increment = ExperimentDurationMath.ToDisplayValue(StepMilliseconds, Unit.Value);
            Minimum = ExperimentDurationMath.ToDisplayValue(MinimumMilliseconds, Unit.Value);
            Maximum = ExperimentDurationMath.ToDisplayValue(MaximumMilliseconds, Unit.Value);
        }
    }

    private int DecimalPlaces() =>
        Unit is null || StepMilliseconds <= 0
            ? ExperimentDurationMath.MaximumDisplayDecimalPlaces
            : ExperimentDurationMath.GetDisplayDecimalPlaces(StepMilliseconds, Unit.Value);
}
