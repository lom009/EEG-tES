using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public enum StimulusWaveformDisplayMode
{
    Detailed,
    Compact,
}

internal readonly record struct StimulusWaveformAxisScale(
    double Minimum,
    double Maximum,
    double TickStep,
    int TimeTickCount
);

public sealed class StimulusWaveformControl : Control
{
    private const double ShamAlternatingActiveSegmentRatio = 0.1d;
    private const double ShamAlternatingRightSegmentStartRatio = 0.9d;
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#E2EAF5"));
    private static readonly IBrush AxisTextBrush = new SolidColorBrush(Color.Parse("#ACB7C8"));
    private static readonly IBrush WaveformBrush = new SolidColorBrush(Color.Parse("#913CFF"));
    private static readonly IPen GridPen = new Pen(GridBrush, 1);
    private static readonly IPen WaveformPen = new Pen(WaveformBrush, 1.6);
    private static readonly IPen TransitionPen = new Pen(
        new SolidColorBrush(Color.Parse("#C99AFF")),
        0.8,
        new DashStyle(new double[] { 2d, 2d }, 0d)
    );

    public static readonly StyledProperty<WaveformDescriptor?> DescriptorProperty =
        AvaloniaProperty.Register<StimulusWaveformControl, WaveformDescriptor?>(nameof(Descriptor));

    public static readonly StyledProperty<StimulusWaveformDisplayMode> DisplayModeProperty =
        AvaloniaProperty.Register<StimulusWaveformControl, StimulusWaveformDisplayMode>(
            nameof(DisplayMode)
        );

    static StimulusWaveformControl()
    {
        AffectsRender<StimulusWaveformControl>(DescriptorProperty, DisplayModeProperty);
    }

    public WaveformDescriptor? Descriptor
    {
        get => GetValue(DescriptorProperty);
        set => SetValue(DescriptorProperty, value);
    }

    public StimulusWaveformDisplayMode DisplayMode
    {
        get => GetValue(DisplayModeProperty);
        set => SetValue(DisplayModeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var descriptor = Descriptor;
        if (descriptor is null)
            return;
        if (descriptor.Kind == StimulusKind.EnvelopeTAcs)
        {
            EnvelopeWaveformRenderer.Render(
                context,
                Bounds.Size,
                descriptor,
                DisplayMode == StimulusWaveformDisplayMode.Compact
            );
            return;
        }

        if (DisplayMode == StimulusWaveformDisplayMode.Compact)
        {
            if (Bounds.Width < 40d || Bounds.Height < 24d)
                return;
            var compactPlot = new Rect(5d, 4d, Bounds.Width - 10d, Bounds.Height - 8d);
            var compactScale = GetAxisScale(descriptor);
            var zeroY =
                compactPlot.Bottom
                - (0d - compactScale.Minimum)
                    / (compactScale.Maximum - compactScale.Minimum)
                    * compactPlot.Height;
            // A compact preview shows four cycles; a high frequency must not turn into a solid bar.
            var windowMilliseconds = descriptor.Kind == StimulusKind.TAcs
                ? 4000d / Math.Max(0.1d, descriptor.Frequency)
                : GetTimeWindowMilliseconds(descriptor);
            if (descriptor.Kind == StimulusKind.TDcs)
            {
                double? previous = null;
                foreach (
                    var seconds in GetDirectCurrentTransitionTimes(
                        descriptor,
                        windowMilliseconds / 1000d
                    )
                )
                {
                    if (seconds == previous)
                        continue;
                    previous = seconds;
                    var x =
                        compactPlot.Left
                        + compactPlot.Width * seconds / (windowMilliseconds / 1000d);
                    context.DrawLine(
                        TransitionPen,
                        new Point(x, compactPlot.Top),
                        new Point(x, compactPlot.Bottom)
                    );
                }
            }
            else
            {
                context.DrawLine(
                    new Pen(new SolidColorBrush(Color.Parse("#EAD9FF")), 1d),
                    new Point(compactPlot.Left, zeroY),
                    new Point(compactPlot.Right, zeroY)
                );
            }
            DrawWaveform(
                context,
                compactPlot,
                descriptor,
                compactScale.Minimum,
                compactScale.Maximum,
                windowMilliseconds
            );
            if (descriptor.Kind == StimulusKind.TDcs)
                context.DrawEllipse(
                    Brushes.White,
                    new Pen(WaveformBrush, 0.8),
                    new Point(compactPlot.Left, zeroY),
                    1.8,
                    1.8
                );
            return;
        }

        if (Bounds.Width < 140 || Bounds.Height < 100)
            return;

        // Reserve enough room for the rotated Y-axis title and its tick labels.
        var plot = descriptor.Kind == StimulusKind.TAcs
            ? new Rect(50, 6, Bounds.Width - 62, Bounds.Height - 55)
            : new Rect(78, 18, Bounds.Width - 94, Bounds.Height - 54);
        var scale = GetAxisScale(descriptor);

        DrawGrid(context, plot, scale);
        var timeWindowMilliseconds = GetTimeWindowMilliseconds(descriptor);
        DrawWaveform(
            context,
            plot,
            descriptor,
            scale.Minimum,
            scale.Maximum,
            timeWindowMilliseconds
        );
        DrawAxisLabels(context, plot, descriptor, timeWindowMilliseconds, scale.TimeTickCount);
    }

    private static void DrawGrid(DrawingContext context, Rect plot, StimulusWaveformAxisScale scale)
    {
        var intervalCount = Math.Max(
            1,
            (int)
                Math.Round(
                    (scale.Maximum - scale.Minimum) / scale.TickStep,
                    MidpointRounding.AwayFromZero
                )
        );
        for (var index = 0; index <= intervalCount; index++)
        {
            var y = plot.Top + plot.Height * index / intervalCount;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = scale.Maximum - scale.TickStep * index;
            DrawText(
                context,
                value.ToString("0.#", CultureInfo.InvariantCulture),
                new Point(plot.Left - 32, y - 7),
                12
            );
        }

        context.DrawLine(
            GridPen,
            new Point(plot.Left, plot.Top),
            new Point(plot.Left, plot.Bottom)
        );
    }

    private static void DrawWaveform(
        DrawingContext context,
        Rect plot,
        WaveformDescriptor descriptor,
        double yMin,
        double yMax,
        double timeWindowMilliseconds
    )
    {
        if (descriptor.Kind == StimulusKind.TPcs)
        {
            var pulseGeometry = new StreamGeometry();
            using (var stream = pulseGeometry.Open())
            {
                var first = true;
                foreach (var sample in GetPulsePoints(descriptor))
                {
                    var point = new Point(
                        plot.Left + plot.Width * sample.X,
                        plot.Bottom - (sample.Y - yMin) / (yMax - yMin) * plot.Height
                    );
                    if (first)
                        stream.BeginFigure(point, false);
                    else
                        stream.LineTo(point);
                    first = false;
                }
            }
            context.DrawGeometry(null, WaveformPen, pulseGeometry);
            return;
        }

        if (descriptor.Kind == StimulusKind.TDcs)
        {
            DrawDirectCurrentWaveform(
                context,
                plot,
                descriptor,
                yMin,
                yMax,
                timeWindowMilliseconds / 1000d
            );
            return;
        }

        if (IsShamDirect(descriptor))
        {
            DrawShamDirectWaveform(
                context,
                plot,
                descriptor,
                yMin,
                yMax,
                timeWindowMilliseconds / 1000d
            );
            return;
        }

        if (IsShamAlternating(descriptor))
        {
            DrawShamAlternatingWaveform(
                context,
                plot,
                descriptor,
                yMin,
                yMax,
                timeWindowMilliseconds / 1000d
            );
            return;
        }

        var frequencyCycles = IsFrequencyBased(descriptor)
            ? Math.Max(1d, descriptor.Frequency) * timeWindowMilliseconds / 1000d
            : 0d;
        var sampleCount = Math.Clamp((int)Math.Ceiling(frequencyCycles * 28d), 480, 5000);
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var index = 0; index <= sampleCount; index++)
            {
                var time = (double)index / sampleCount;
                var value = GetWaveformValue(descriptor, time, timeWindowMilliseconds);
                var point = new Point(
                    plot.Left + plot.Width * time,
                    plot.Bottom
                        - (Math.Clamp(value, yMin, yMax) - yMin) / (yMax - yMin) * plot.Height
                );

                if (index == 0)
                    stream.BeginFigure(point, false);
                else
                    stream.LineTo(point);
            }
        }

        context.DrawGeometry(null, WaveformPen, geometry);
    }

    private static void DrawShamAlternatingWaveform(
        DrawingContext context,
        Rect plot,
        WaveformDescriptor descriptor,
        double yMin,
        double yMax,
        double durationSeconds
    )
    {
        var amplitude = Math.Max(0.05d, descriptor.Current);
        var samplesPerSegment = GetShamAlternatingSamplesPerActiveSegment(descriptor);
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var index = 0; index <= samplesPerSegment; index++)
            {
                var elapsedSeconds =
                    durationSeconds * ShamAlternatingActiveSegmentRatio * index / samplesPerSegment;
                AddShamAlternatingPoint(
                    stream,
                    plot,
                    descriptor,
                    yMin,
                    yMax,
                    durationSeconds,
                    amplitude,
                    elapsedSeconds,
                    index == 0
                );
            }

            AddShamAlternatingPoint(
                stream,
                plot,
                descriptor,
                yMin,
                yMax,
                durationSeconds,
                amplitude,
                durationSeconds * ShamAlternatingRightSegmentStartRatio,
                false,
                forcedValue: 0d
            );

            AddShamAlternatingPoint(
                stream,
                plot,
                descriptor,
                yMin,
                yMax,
                durationSeconds,
                amplitude,
                durationSeconds * ShamAlternatingRightSegmentStartRatio,
                false
            );

            for (var index = 1; index <= samplesPerSegment; index++)
            {
                var elapsedSeconds =
                    durationSeconds
                    * (
                        ShamAlternatingRightSegmentStartRatio
                        + ShamAlternatingActiveSegmentRatio * index / samplesPerSegment
                    );
                AddShamAlternatingPoint(
                    stream,
                    plot,
                    descriptor,
                    yMin,
                    yMax,
                    durationSeconds,
                    amplitude,
                    elapsedSeconds,
                    false
                );
            }
        }

        context.DrawGeometry(null, WaveformPen, geometry);
    }

    private static void AddShamAlternatingPoint(
        StreamGeometryContext stream,
        Rect plot,
        WaveformDescriptor descriptor,
        double yMin,
        double yMax,
        double durationSeconds,
        double amplitude,
        double elapsedSeconds,
        bool beginFigure,
        double? forcedValue = null
    )
    {
        var value = forcedValue ?? GetShamAlternatingCurrent(descriptor, elapsedSeconds, amplitude);
        var point = new Point(
            plot.Left + plot.Width * elapsedSeconds / durationSeconds,
            plot.Bottom - (Math.Clamp(value, yMin, yMax) - yMin) / (yMax - yMin) * plot.Height
        );
        if (beginFigure)
            stream.BeginFigure(point, false);
        else
            stream.LineTo(point);
    }

    internal static double[] GetDirectCurrentTransitionTimes(
        WaveformDescriptor descriptor,
        double durationSeconds
    )
    {
        var rampSeconds = Math.Clamp(descriptor.RampSeconds, 0d, durationSeconds / 2d);
        return [0d, rampSeconds, durationSeconds - rampSeconds, durationSeconds];
    }

    private static void DrawDirectCurrentWaveform(
        DrawingContext context,
        Rect plot,
        WaveformDescriptor descriptor,
        double yMin,
        double yMax,
        double durationSeconds
    )
    {
        var amplitude = Math.Max(0.05d, descriptor.Current);
        var signedAmplitude =
            descriptor.Direction == StimulusDirection.Negative ? -amplitude : amplitude;
        var times = GetDirectCurrentTransitionTimes(descriptor, durationSeconds);
        var points = new[]
        {
            (Time: times[0], Value: 0d),
            (Time: times[1], Value: signedAmplitude),
            (Time: times[2], Value: signedAmplitude),
            (Time: times[3], Value: 0d),
        };
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var index = 0; index < points.Length; index++)
            {
                var source = points[index];
                var point = new Point(
                    plot.Left + plot.Width * source.Time / durationSeconds,
                    plot.Bottom
                        - (Math.Clamp(source.Value, yMin, yMax) - yMin)
                            / (yMax - yMin)
                            * plot.Height
                );
                if (index == 0)
                    stream.BeginFigure(point, false);
                else
                    stream.LineTo(point);
            }
        }

        context.DrawGeometry(null, WaveformPen, geometry);
    }

    private static void DrawShamDirectWaveform(
        DrawingContext context,
        Rect plot,
        WaveformDescriptor descriptor,
        double yMin,
        double yMax,
        double durationSeconds
    )
    {
        var amplitude = Math.Max(0.05d, descriptor.Current);
        var signedAmplitude =
            descriptor.Direction == StimulusDirection.Negative ? -amplitude : amplitude;
        var rampSeconds = Math.Clamp(descriptor.RampSeconds, 0d, durationSeconds / 4d);
        var points =
            rampSeconds <= 0d
                ? new[]
                {
                    (Time: 0d, Value: 0d),
                    (Time: 0d, Value: signedAmplitude),
                    (Time: 0d, Value: 0d),
                    (Time: durationSeconds, Value: 0d),
                    (Time: durationSeconds, Value: signedAmplitude),
                    (Time: durationSeconds, Value: 0d),
                }
                : new[]
                {
                    (Time: 0d, Value: 0d),
                    (Time: rampSeconds, Value: signedAmplitude),
                    (Time: rampSeconds * 2d, Value: 0d),
                    (Time: durationSeconds - rampSeconds * 2d, Value: 0d),
                    (Time: durationSeconds - rampSeconds, Value: signedAmplitude),
                    (Time: durationSeconds, Value: 0d),
                };
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var index = 0; index < points.Length; index++)
            {
                var source = points[index];
                var point = new Point(
                    plot.Left + plot.Width * source.Time / durationSeconds,
                    plot.Bottom
                        - (Math.Clamp(source.Value, yMin, yMax) - yMin)
                            / (yMax - yMin)
                            * plot.Height
                );
                if (index == 0)
                    stream.BeginFigure(point, false);
                else
                    stream.LineTo(point);
            }
        }

        context.DrawGeometry(null, WaveformPen, geometry);
    }

    private static double GetWaveformValue(
        WaveformDescriptor descriptor,
        double time,
        double timeWindowMilliseconds
    )
    {
        var amplitude = Math.Max(0.05, descriptor.Current);
        var elapsedSeconds = time * timeWindowMilliseconds / 1000d;
        return descriptor.Kind switch
        {
            StimulusKind.TDcs => GetDirectCurrent(
                descriptor,
                elapsedSeconds,
                timeWindowMilliseconds / 1000d,
                amplitude
            ),
            StimulusKind.TAcs => GetAlternatingCurrent(descriptor, elapsedSeconds, amplitude),
            StimulusKind.TRns => GetRandomNoise(time, amplitude),
            StimulusKind.TPcs => GetPulseCurrent(descriptor, elapsedSeconds, amplitude),
            StimulusKind.Sham => descriptor.ShamMode == ShamWaveformMode.Direct
                ? GetShamDirectCurrent(
                    descriptor,
                    elapsedSeconds,
                    timeWindowMilliseconds / 1000d,
                    amplitude
                )
                : GetShamAlternatingCurrent(descriptor, elapsedSeconds, amplitude),
            _ => 0d,
        };
    }

    internal static double GetDirectCurrent(
        WaveformDescriptor descriptor,
        double elapsedSeconds,
        double durationSeconds,
        double amplitude
    )
    {
        var duration = Math.Max(0.001d, durationSeconds);
        var elapsed = Math.Clamp(elapsedSeconds, 0d, duration);
        var ramp = Math.Clamp(descriptor.RampSeconds, 0d, duration / 2d);
        var envelope =
            ramp <= 0d
                ? elapsed <= 0d || elapsed >= duration
                    ? 0d
                    : 1d
                : elapsed <= ramp
                    ? elapsed / ramp
                    : elapsed >= duration - ramp
                        ? (duration - elapsed) / ramp
                        : 1d;
        return descriptor.Direction == StimulusDirection.Negative
            ? -amplitude * envelope
            : amplitude * envelope;
    }

    private static double GetAlternatingCurrent(
        WaveformDescriptor descriptor,
        double elapsedSeconds,
        double amplitude
    )
    {
        var wave = Math.Sin(elapsedSeconds * Math.PI * 2d * Math.Max(0.1d, descriptor.Frequency));
        return descriptor.Direction == StimulusDirection.Positive
            ? amplitude * (wave + 1d) / 2d
            : amplitude * wave;
    }

    private static double GetRandomNoise(double time, double amplitude)
    {
        var signal =
            Math.Sin(time * 137d)
            + 0.62d * Math.Sin(time * 311d + 0.7d)
            + 0.38d * Math.Sin(time * 557d + 1.9d);
        return amplitude * signal / 2d;
    }

    private static double GetPulseCurrent(
        WaveformDescriptor descriptor,
        double elapsedSeconds,
        double amplitude
    )
    {
        var duty = Math.Clamp(descriptor.DutyPercent / 100d, 0.01d, 0.99d);
        var cyclePosition = elapsedSeconds * Math.Max(0.1d, descriptor.Frequency);
        var phase = cyclePosition % 1d;
        var value = phase < duty ? amplitude : 0d;
        return
            descriptor.Direction == StimulusDirection.Bidirectional && (int)cyclePosition % 2 == 1
            ? -value
            : value;
    }

    // The pulse preview spans four periods. Explicit edges preserve 1% pulses at any control size.
    internal static IReadOnlyList<Point> GetPulsePoints(WaveformDescriptor descriptor)
    {
        var points = new List<Point>();
        var duty = Math.Clamp(descriptor.DutyPercent / 100d, 0.01d, 0.99d);
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var amplitude =
                Math.Max(0.05d, descriptor.Current)
                * (
                    descriptor.Direction == StimulusDirection.Bidirectional && cycle % 2 == 1
                        ? -1
                        : 1
                );
            var start = cycle / 4d;
            var end = (cycle + duty) / 4d;
            points.Add(new Point(start, 0));
            points.Add(new Point(start, amplitude));
            points.Add(new Point(end, amplitude));
            points.Add(new Point(end, 0));
            points.Add(new Point((cycle + 1) / 4d, 0));
        }
        return points;
    }

    internal static double GetShamDirectCurrent(
        WaveformDescriptor descriptor,
        double elapsedSeconds,
        double durationSeconds,
        double amplitude
    )
    {
        var duration = Math.Max(0.001d, durationSeconds);
        var elapsed = Math.Clamp(elapsedSeconds, 0d, duration);
        var ramp = Math.Clamp(descriptor.RampSeconds, 0d, duration / 4d);
        if (ramp <= 0d)
            return 0d;

        var envelope =
            elapsed <= ramp ? elapsed / ramp
            : elapsed <= ramp * 2d ? (ramp * 2d - elapsed) / ramp
            : elapsed >= duration - ramp ? (duration - elapsed) / ramp
            : elapsed >= duration - ramp * 2d ? (elapsed - (duration - ramp * 2d)) / ramp
            : 0d;
        return descriptor.Direction == StimulusDirection.Negative
            ? -amplitude * envelope
            : amplitude * envelope;
    }

    internal static double GetShamAlternatingCurrent(
        WaveformDescriptor descriptor,
        double elapsedSeconds,
        double amplitude
    )
    {
        var duration = GetTimeWindowMilliseconds(descriptor) / 1000d;
        var elapsed = Math.Clamp(elapsedSeconds, 0d, duration);
        if (
            elapsed >= duration * ShamAlternatingActiveSegmentRatio
            && elapsed < duration * ShamAlternatingRightSegmentStartRatio
        )
            return 0d;

        var segmentElapsed =
            elapsed < duration * ShamAlternatingActiveSegmentRatio
                ? elapsed
                : elapsed - duration * ShamAlternatingRightSegmentStartRatio;
        return amplitude
            * Math.Sin(segmentElapsed * Math.PI * 2d * Math.Max(0.1d, descriptor.Frequency));
    }

    internal static int GetShamAlternatingSamplesPerActiveSegment(WaveformDescriptor _) => 28;

    private static void DrawAxisLabels(
        DrawingContext context,
        Rect plot,
        WaveformDescriptor descriptor,
        double timeWindowMilliseconds,
        int tickCount
    )
    {
        for (var index = 0; index <= tickCount; index++)
        {
            var x = plot.Left + plot.Width * index / tickCount;
            var value = UsesMillisecondTimeAxis(descriptor)
                ? timeWindowMilliseconds * index / tickCount
                : timeWindowMilliseconds * index / tickCount / 1000d;
            var label = value.ToString("0.###", CultureInfo.InvariantCulture);
            DrawText(context, label, new Point(x - (label.Length * 3.1), plot.Bottom + 7), 12);
        }

        DrawRotatedYAxisTitle(context, plot, descriptor);
        DrawText(
            context,
            UsesMillisecondTimeAxis(descriptor) ? "时间(ms)" : "时间(s)",
            new Point(plot.Right - 42, plot.Bottom + 21),
            12
        );
    }

    private static void DrawRotatedYAxisTitle(
        DrawingContext context,
        Rect plot,
        WaveformDescriptor descriptor
    )
    {
        var title = descriptor.Kind switch
        {
            StimulusKind.TDcs => "电流增幅(mA)",
            StimulusKind.TAcs => "电流(mA)",
            _ => "电流幅值(mA)",
        };
        var formatted = CreateFormattedText(title, 12);
        var centre = new Point(13, plot.Center.Y);
        var origin = new Point(centre.X - formatted.Width / 2d, centre.Y - formatted.Height / 2d);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2d, centre)))
            context.DrawText(formatted, origin);
    }

    private static bool IsFrequencyBased(WaveformDescriptor descriptor) =>
        descriptor.Kind is StimulusKind.TAcs or StimulusKind.TPcs
        || descriptor.Kind == StimulusKind.Sham
            && descriptor.ShamMode == ShamWaveformMode.Alternating;

    internal static bool UsesMillisecondTimeAxis(WaveformDescriptor descriptor) =>
        descriptor.Kind is StimulusKind.TAcs or StimulusKind.TPcs
        || IsShamAlternating(descriptor) && GetTimeWindowMilliseconds(descriptor) < 1000d;

    private static bool IsShamDirect(WaveformDescriptor descriptor) =>
        descriptor.Kind == StimulusKind.Sham && descriptor.ShamMode == ShamWaveformMode.Direct;

    private static bool IsShamAlternating(WaveformDescriptor descriptor) =>
        descriptor.Kind == StimulusKind.Sham && descriptor.ShamMode == ShamWaveformMode.Alternating;

    internal static StimulusWaveformAxisScale GetAxisScale(WaveformDescriptor descriptor)
    {
        if (descriptor.Kind == StimulusKind.TDcs || IsShamDirect(descriptor))
        {
            var timeTickCount = descriptor.Kind == StimulusKind.TDcs ? 6 : 10;
            return descriptor.Direction == StimulusDirection.Negative
                ? new StimulusWaveformAxisScale(-2.5d, 0d, 0.5d, timeTickCount)
                : new StimulusWaveformAxisScale(0d, 2.5d, 0.5d, timeTickCount);
        }

        if (IsShamAlternating(descriptor))
            return new StimulusWaveformAxisScale(-2.5d, 2.5d, 0.5d, 10);

        var isBipolar =
            descriptor.Direction is StimulusDirection.Negative or StimulusDirection.Bidirectional
            || descriptor.Kind == StimulusKind.TRns;
        var minimum = isBipolar ? -2.2d : 0d;
        const double maximum = 2.2d;
        return new StimulusWaveformAxisScale(minimum, maximum, (maximum - minimum) / 4d, 10);
    }

    internal static double GetTimeWindowMilliseconds(WaveformDescriptor descriptor)
    {
        if (descriptor.Kind == StimulusKind.TDcs)
            return 60_000d;

        if (IsShamDirect(descriptor))
            return 150_000d;

        if (IsShamAlternating(descriptor))
            return 10_000d / Math.Max(0.1d, descriptor.Frequency);

        if (descriptor.Kind == StimulusKind.TAcs)
            return 1000d;

        if (descriptor.Kind == StimulusKind.TPcs)
            return 4000d / Math.Max(0.1d, descriptor.Frequency);

        return 10000d;
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double fontSize)
    {
        context.DrawText(CreateFormattedText(text, fontSize), origin);
    }

    private static FormattedText CreateFormattedText(string text, double fontSize)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            fontSize,
            AxisTextBrush
        );
    }
}
