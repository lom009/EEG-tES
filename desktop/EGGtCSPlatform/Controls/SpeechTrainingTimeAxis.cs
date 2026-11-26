using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EGGtCSPlatform.Controls;

/// <summary>Time axis for the actual recording, using the plot's 46/18 DIP insets.</summary>
public sealed class SpeechTrainingTimeAxis : Control
{
    public static readonly StyledProperty<double> DurationSecondsProperty =
        AvaloniaProperty.Register<SpeechTrainingTimeAxis, double>(nameof(DurationSeconds), 1);
    public double DurationSeconds
    {
        get => GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    static SpeechTrainingTimeAxis() =>
        AffectsRender<SpeechTrainingTimeAxis>(DurationSecondsProperty);

    private static readonly IBrush TickBrush = new SolidColorBrush(Color.Parse("#7288AB"));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width - 64;
        if (width <= 0)
            return;
        var pen = new Pen(TickBrush, 1);
        context.DrawLine(pen, new Point(46, 5), new Point(46 + width, 5));
        for (var i = 0; i <= 8; i++)
        {
            var label = new FormattedText(
                (i * DurationSeconds / 8).ToString("0.##", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                11,
                TickBrush
            );
            var x = 46 + width * i / 8;
            context.DrawLine(pen, new Point(x, 5), new Point(x, 9));
            context.DrawText(label, new Point(x - label.Width / 2, 13));
        }
    }
}

/// <summary>Voice axis drawn over the curves, outside their clipping area.</summary>
public sealed class SpeechTrainingAmplitudeAxis : Control
{
    public static readonly StyledProperty<double> MaximumAmplitudeProperty =
        AvaloniaProperty.Register<SpeechTrainingAmplitudeAxis, double>(nameof(MaximumAmplitude), 1d,
            validate: value => double.IsFinite(value) && value > 0);

    public double MaximumAmplitude
    {
        get => GetValue(MaximumAmplitudeProperty);
        set => SetValue(MaximumAmplitudeProperty, value);
    }

    static SpeechTrainingAmplitudeAxis() => AffectsRender<SpeechTrainingAmplitudeAxis>(MaximumAmplitudeProperty);

    private static readonly IBrush TickBrush = new SolidColorBrush(Color.Parse("#A9BAD4"));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var height = Bounds.Height - 36;
        if (height <= 0)
            return;
        var pen = new Pen(TickBrush, 1);
        context.DrawLine(pen, new Point(46, 24), new Point(46, 24 + height));
        for (var i = 0; i <= 4; i++)
        {
            var label = new FormattedText(
                (MaximumAmplitude * (1 - i * 0.5)).ToString("0.####", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                10,
                TickBrush
            );
            var y = 24 + height * i / 4;
            context.DrawLine(pen, new Point(46, y), new Point(50, y));
            context.DrawText(label, new Point(40 - label.Width, y - label.Height / 2));
        }
    }
}
