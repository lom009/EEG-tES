using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.Controls;

/// <summary>Actual-speech stimulus preview with a shared audio time axis. Display only.</summary>
public sealed class SpeechStimulusPreviewControl : Control
{
    public static readonly StyledProperty<bool> DrawCurvesProperty =
        AvaloniaProperty.Register<SpeechStimulusPreviewControl, bool>(nameof(DrawCurves), true);
    public static readonly StyledProperty<bool> DrawAxesProperty =
        AvaloniaProperty.Register<SpeechStimulusPreviewControl, bool>(nameof(DrawAxes), true);
    public static readonly StyledProperty<bool> DrawGridProperty =
        AvaloniaProperty.Register<SpeechStimulusPreviewControl, bool>(nameof(DrawGrid), true);
    public bool DrawGrid { get => GetValue(DrawGridProperty); set => SetValue(DrawGridProperty, value); }
    public bool DrawCurves { get => GetValue(DrawCurvesProperty); set => SetValue(DrawCurvesProperty, value); }
    public bool DrawAxes { get => GetValue(DrawAxesProperty); set => SetValue(DrawAxesProperty, value); }

    public static readonly StyledProperty<SpeechStimulusPreview?> PreviewProperty =
        AvaloniaProperty.Register<SpeechStimulusPreviewControl, SpeechStimulusPreview?>(
            nameof(Preview)
        );
    public static readonly StyledProperty<double> DurationSecondsProperty =
        AvaloniaProperty.Register<SpeechStimulusPreviewControl, double>(
            nameof(DurationSeconds),
            1,
            validate: value => double.IsFinite(value) && value > 0
        );
    public static readonly StyledProperty<double> MaximumCurrentProperty =
        AvaloniaProperty.Register<SpeechStimulusPreviewControl, double>(
            nameof(MaximumCurrent),
            0,
            validate: value => double.IsFinite(value) && value >= 0
        );
    public static readonly StyledProperty<double> DelayMillisecondsProperty =
        AvaloniaProperty.Register<SpeechStimulusPreviewControl, double>(
            nameof(DelayMilliseconds),
            0,
            validate: value => double.IsFinite(value) && value >= 0
        );
    public static readonly StyledProperty<double> MaximumAmplitudeProperty =
        AvaloniaProperty.Register<SpeechStimulusPreviewControl, double>(
            nameof(MaximumAmplitude),
            2,
            validate: value => double.IsFinite(value) && value > 0
        );

    public SpeechStimulusPreview? Preview
    {
        get => GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }
    public double DurationSeconds
    {
        get => GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }
    public double MaximumCurrent
    {
        get => GetValue(MaximumCurrentProperty);
        set => SetValue(MaximumCurrentProperty, value);
    }
    public double DelayMilliseconds
    {
        get => GetValue(DelayMillisecondsProperty);
        set => SetValue(DelayMillisecondsProperty, value);
    }
    public double MaximumAmplitude
    {
        get => GetValue(MaximumAmplitudeProperty);
        set => SetValue(MaximumAmplitudeProperty, value);
    }

    static SpeechStimulusPreviewControl() =>
        AffectsRender<SpeechStimulusPreviewControl>(
            DrawCurvesProperty, DrawAxesProperty, DrawGridProperty, PreviewProperty,
            DurationSecondsProperty,
            MaximumCurrentProperty,
            DelayMillisecondsProperty,
            MaximumAmplitudeProperty
        );

    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#E2E8F2")), 1);
    private static readonly IBrush AxisBrush = new SolidColorBrush(Color.Parse("#9AAAC2"));
    private static readonly IPen WaveformPen = new Pen(
        new SolidColorBrush(Color.Parse("#A6A0FF")),
        0.8
    );
    private static readonly IBrush ContourBrush = new SolidColorBrush(Color.Parse("#9536F3"));
    private static readonly IPen UpperPen = new Pen(ContourBrush, 1.2);
    private static readonly IPen LowerPen = new Pen(
        ContourBrush,
        1,
        new DashStyle(new double[] { 2, 2 }, 0)
    );

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || (DrawAxes && (Bounds.Width <= 64 || Bounds.Height <= 36)))
            return;
        var plot = DrawAxes ? new Rect(46, 24, Bounds.Width - 64, Bounds.Height - 36) : new Rect(Bounds.Size);
        if (DrawAxes && DrawGrid)
        {
            for (var i = 0; i <= 4; i++)
            {
                var y = plot.Top + plot.Height * i / 4;
                context.DrawLine(GridPen, new(plot.Left, y), new(plot.Right, y));
            }
            for (var i = 0; i <= 8; i++)
            {
                var x = plot.Left + plot.Width * i / 8;
                context.DrawLine(GridPen, new(x, plot.Top), new(x, plot.Bottom));
            }
        }
        if (DrawCurves && Preview is { } preview)
            using (context.PushClip(plot))
            {
                Draw(context, plot, preview.Waveform, WaveformPen);
                Draw(context, plot, preview.Upper, UpperPen);
                Draw(context, plot, preview.Lower, LowerPen);
            }
        if (!DrawAxes) return;
        // Paint axis ticks last to keep them readable at any zoom level.
        var axisPen = new Pen(AxisBrush, 1);
        context.DrawLine(axisPen, plot.TopLeft, plot.BottomLeft);
        context.DrawText(Label("mA"), new(0, 5));
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            var label = Label(
                (MaximumAmplitude * (1 - i * 0.5)).ToString("0.####", CultureInfo.InvariantCulture)
            );
            context.DrawLine(axisPen, new(plot.Left, y), new(plot.Left + 4, y));
            context.DrawText(label, new(plot.Left - 6 - label.Width, y - label.Height / 2));
        }
    }

    private void Draw(
        DrawingContext context,
        Rect plot,
        IReadOnlyList<SpeechStimulusPoint> points,
        IPen pen
    )
    {
        if (points.Count == 0)
            return;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            // A baseline before the delayed onset; all three curves share the same offset.
            stream.BeginFigure(new(plot.Left, plot.Center.Y), false);
            stream.LineTo(
                new(
                    plot.Left + DelayMilliseconds / 1000 / DurationSeconds * plot.Width,
                    plot.Center.Y
                )
            );
            foreach (var p in points)
                stream.LineTo(
                    Project(
                        p,
                        plot,
                        DurationSeconds,
                        MaximumCurrent,
                        DelayMilliseconds,
                        MaximumAmplitude
                    )
                );
        }
        context.DrawGeometry(null, pen, geometry);
    }

    internal static Point Project(
        SpeechStimulusPoint point,
        Rect plot,
        double duration,
        double current,
        double delayMilliseconds,
        double maximumAmplitude
    ) =>
        new(
            plot.Left + (point.Seconds + delayMilliseconds / 1000) / duration * plot.Width,
            // Do not clamp: clip the geometry, rather than flatten peaks against the axis boundary.
            plot.Top
                + (0.5 - point.Value * current / (2 * maximumAmplitude)) * plot.Height
        );

    private static FormattedText Label(string text) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11,
            AxisBrush
        );
}
