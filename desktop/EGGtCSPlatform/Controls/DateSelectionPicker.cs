using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace EGGtCSPlatform.Controls;

public sealed class DateSelectionPicker : TemplatedControl
{
    public static readonly StyledProperty<DateTime?> SelectedDateProperty =
        AvaloniaProperty.Register<DateSelectionPicker, DateTime?>(
            nameof(SelectedDate),
            defaultBindingMode: BindingMode.TwoWay
        );

    private CalendarDatePicker? _datePicker;

    public DateTime? SelectedDate
    {
        get => GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_datePicker is not null)
        {
            _datePicker.SelectedDateChanged -= OnSelectedDateChanged;
            _datePicker.TemplateApplied -= OnDatePickerTemplateApplied;
        }

        base.OnApplyTemplate(e);
        _datePicker = e.NameScope.Find<CalendarDatePicker>("PART_DatePicker");
        if (_datePicker is null)
            return;

        _datePicker.SelectedDate = SelectedDate;
        _datePicker.SelectedDateChanged += OnSelectedDateChanged;
        _datePicker.TemplateApplied += OnDatePickerTemplateApplied;
        _datePicker.ApplyTemplate();
        ConfigureTextBox(
            _datePicker
                .GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(textBox => textBox.Name == "PART_TextBox")
        );
    }

    private static void ConfigureTextBox(TextBox? textBox)
    {
        if (textBox is not null)
        {
            textBox.IsReadOnly = true;
            textBox.IsHitTestVisible = false;
            textBox.Focusable = false;
            textBox.Cursor = new Cursor(StandardCursorType.Hand);
        }
    }

    private void OnDatePickerTemplateApplied(object? sender, TemplateAppliedEventArgs e) =>
        ConfigureTextBox(e.NameScope.Find<TextBox>("PART_TextBox"));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (
            change.Property == SelectedDateProperty
            && _datePicker is not null
            && _datePicker.SelectedDate != change.GetNewValue<DateTime?>()
        )
        {
            _datePicker.SelectedDate = change.GetNewValue<DateTime?>();
        }
    }

    private void OnSelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_datePicker is not null && SelectedDate != _datePicker.SelectedDate)
            SetCurrentValue(SelectedDateProperty, _datePicker.SelectedDate);
    }
}
