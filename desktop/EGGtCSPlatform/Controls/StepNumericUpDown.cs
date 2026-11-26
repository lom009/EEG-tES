using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace EGGtCSPlatform.Controls;

/// <summary>Aligns manually entered values when editing is committed.</summary>
public class StepNumericUpDown : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        CommitStep();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter)
            CommitStep();
    }

    protected override void OnSpin(SpinEventArgs e)
    {
        CommitStep();
        base.OnSpin(e);
        CommitStep();
    }

    private void CommitStep()
    {
        if (IsReadOnly || !IsEnabled || Value is not { } value || Increment <= 0)
            return;

        SetCurrentValue(ValueProperty, AlignToStep(value, Minimum, Maximum, Increment));
        // Also refresh text when the rounded value equals the existing value.
        SetCurrentValue(TextProperty, Value?.ToString(FormatString, NumberFormat));
    }

    internal static decimal AlignToStep(decimal value, decimal minimum, decimal maximum, decimal step)
    {
        value = Math.Clamp(value, minimum, maximum);
        if (step <= 0)
            return value;

        // Use remainders so default decimal.MinValue/MaxValue bounds do not
        // overflow when the increment is fractional. The grid is zero-based,
        // matching the stimulus parameter policies.
        var remainder = value % step;
        var aligned = value - remainder;
        var direction = Math.Sign(remainder);
        if (Math.Abs(remainder) >= step / 2 && direction != 0)
        {
            if (direction > 0 && aligned <= decimal.MaxValue - step)
                aligned += step;
            else if (direction < 0 && aligned >= decimal.MinValue + step)
                aligned -= step;
        }

        if (aligned < minimum)
        {
            aligned = minimum - minimum % step;
            if (aligned < minimum && aligned <= decimal.MaxValue - step)
                aligned += step;
        }
        if (aligned > maximum)
        {
            aligned = maximum - maximum % step;
            if (aligned > maximum && aligned >= decimal.MinValue + step)
                aligned -= step;
        }
        return Math.Clamp(aligned, minimum, maximum);
    }
}
