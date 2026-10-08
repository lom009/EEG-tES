using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace EGGtCSPlatform.Controls;

public sealed class AnimatedExpander : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<
        AnimatedExpander,
        bool
    >(nameof(IsExpanded));

    public static readonly StyledProperty<double> ExpansionProgressProperty =
        AvaloniaProperty.Register<AnimatedExpander, double>(
            nameof(ExpansionProgress),
            coerce: static (_, value) => Math.Clamp(value, 0d, 1d)
        );

    static AnimatedExpander()
    {
        AffectsMeasure<AnimatedExpander>(ExpansionProgressProperty);
        IsExpandedProperty.Changed.AddClassHandler<AnimatedExpander>(
            static (control, args) => control.ExpansionProgress = args.GetNewValue<bool>() ? 1d : 0d
        );
    }

    public AnimatedExpander()
    {
        ClipToBounds = true;
        Opacity = 0;
        Transitions =
        [
            new DoubleTransition
            {
                Property = ExpansionProgressProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new CubicEaseInOut(),
            },
        ];
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public double ExpansionProgress
    {
        get => GetValue(ExpansionProgressProperty);
        private set => SetValue(ExpansionProgressProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
            return default;

        Child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * ExpansionProgress);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not null)
            Child.Arrange(new Rect(0, 0, finalSize.Width, Child.DesiredSize.Height));

        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ExpansionProgressProperty)
            Opacity = change.GetNewValue<double>();
    }
}
