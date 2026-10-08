using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public sealed class ExperimentPhaseTimeline : Control
{
    private static readonly FontFamily TimelineFontFamily = new(
        "avares://EGGtCSPlatform/Assets/Fonts/alibaba#Alibaba PuHuiTi 3.0"
    );

    private INotifyCollectionChanged? _observedSegments;

    public static readonly StyledProperty<IReadOnlyList<ExperimentTimelineSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<
            ExperimentPhaseTimeline,
            IReadOnlyList<ExperimentTimelineSegment>?
        >(nameof(Segments));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<
        ExperimentPhaseTimeline,
        double
    >(nameof(Maximum), 5d);

    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<
        ExperimentPhaseTimeline,
        double
    >(nameof(Progress));

    public static readonly StyledProperty<double> ViewStartProperty = AvaloniaProperty.Register<
        ExperimentPhaseTimeline,
        double
    >(nameof(ViewStart));

    public static readonly StyledProperty<double> ViewEndProperty = AvaloniaProperty.Register<
        ExperimentPhaseTimeline,
        double
    >(nameof(ViewEnd), 5d);

    public IReadOnlyList<ExperimentTimelineSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double ViewStart
    {
        get => GetValue(ViewStartProperty);
        set => SetValue(ViewStartProperty, value);
    }

    public double ViewEnd
    {
        get => GetValue(ViewEndProperty);
        set => SetValue(ViewEndProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0d || Bounds.Height <= 0d)
            return;

        const double stageHeight = 24d;
        var maximum = Math.Max(0.001d, Maximum);
        var viewStart = Math.Clamp(ViewStart, 0d, maximum);
        var viewEnd = Math.Clamp(ViewEnd, viewStart, maximum);
        if (viewEnd - viewStart < 0.001d)
            viewEnd = Math.Min(maximum, viewStart + 0.001d);
        context.DrawRectangle(
            new SolidColorBrush(Color.Parse("#F2F5F9")),
            null,
            new Rect(0, 0, Bounds.Width, stageHeight),
            4d,
            4d
        );
        if (Segments is { Count: > 0 } segments)
        {
            foreach (var segment in segments)
            {
                if (
                    !TimelineViewportMath.TryGetVisibleSpan(
                        segment.StartSeconds,
                        segment.EndSeconds,
                        viewStart,
                        viewEnd,
                        out var visible
                    )
                )
                    continue;
                var left = visible.StartFraction * Bounds.Width;
                var right = visible.EndFraction * Bounds.Width;
                var rect = new Rect(left + 1d, 0d, Math.Max(0d, right - left - 2d), stageHeight);
                context.DrawRectangle(
                    new SolidColorBrush(Color.Parse(segment.Fill)),
                    null,
                    rect,
                    3d,
                    3d
                );
                var label = CreateText(segment.Label, 12d, Color.Parse(segment.Foreground));
                if (rect.Width >= 24d)
                {
                    using (context.PushClip(rect.Deflate(2d)))
                        context.DrawText(label, new Point(rect.Center.X - label.Width / 2d, 5d));
                }
            }
        }

        if (Segments is { Count: > 1 } adjacentSegments)
        {
            var separatorPen = new Pen(Brushes.White, 1.5d);
            for (var index = 1; index < adjacentSegments.Count; index++)
            {
                var boundary = adjacentSegments[index].StartSeconds;
                if (
                    Math.Abs(adjacentSegments[index - 1].EndSeconds - boundary) > 0.000001d
                    || boundary <= viewStart
                    || boundary >= viewEnd
                )
                    continue;

                var x = (boundary - viewStart) / (viewEnd - viewStart) * Bounds.Width;
                context.DrawLine(separatorPen, new Point(x, 0d), new Point(x, stageHeight));
            }
        }

        if (Progress >= viewStart && Progress <= viewEnd)
        {
            var progressX =
                (Progress - viewStart) / Math.Max(0.001d, viewEnd - viewStart) * Bounds.Width;
            context.DrawLine(
                new Pen(new SolidColorBrush(Color.Parse("#7357DA")), 2d),
                new Point(progressX, 0d),
                new Point(progressX, stageHeight)
            );
        }

        DrawTimeAxis(context, viewStart, viewEnd, stageHeight + 7d);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty)
        {
            if (_observedSegments is not null)
                _observedSegments.CollectionChanged -= OnSegmentsCollectionChanged;
            _observedSegments = Segments as INotifyCollectionChanged;
            if (_observedSegments is not null)
                _observedSegments.CollectionChanged += OnSegmentsCollectionChanged;
        }
        if (
            change.Property == SegmentsProperty
            || change.Property == MaximumProperty
            || change.Property == ProgressProperty
            || change.Property == ViewStartProperty
            || change.Property == ViewEndProperty
        )
            InvalidateVisual();
    }

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();

    private void DrawTimeAxis(DrawingContext context, double viewStart, double viewEnd, double top)
    {
        var axisPen = new Pen(new SolidColorBrush(Color.Parse("#DCE4EF")), 1d);
        context.DrawLine(axisPen, new Point(0d, top), new Point(Bounds.Width, top));
        var span = Math.Max(0.001d, viewEnd - viewStart);
        var interval = GetNiceInterval(span / Math.Max(2d, Math.Floor(Bounds.Width / 92d)));
        var firstTick = Math.Ceiling(viewStart / interval) * interval;
        DrawTick(context, viewStart, viewStart, viewEnd, top, axisPen);
        for (var value = firstTick; value < viewEnd - interval * 0.05d; value += interval)
        {
            var x = (value - viewStart) / span * Bounds.Width;
            var label = CreateText(FormatAxisTime(value), 12d, Color.Parse("#6B7D99"));
            var startLabel = CreateText(FormatAxisTime(viewStart), 12d, Color.Parse("#6B7D99"));
            var endLabel = CreateText(FormatAxisTime(viewEnd), 12d, Color.Parse("#6B7D99"));
            if (x - label.Width / 2 > startLabel.Width + 6
                && x + label.Width / 2 < Bounds.Width - endLabel.Width - 6)
                DrawTick(context, value, viewStart, viewEnd, top, axisPen);
        }
        DrawTick(context, viewEnd, viewStart, viewEnd, top, axisPen);
    }

    private void DrawTick(
        DrawingContext context,
        double value,
        double viewStart,
        double viewEnd,
        double top,
        IPen axisPen
    )
    {
        var x = (value - viewStart) / Math.Max(0.001d, viewEnd - viewStart) * Bounds.Width;
        context.DrawLine(axisPen, new Point(x, top), new Point(x, top + 4d));
        var label = CreateText(FormatAxisTime(value), 12d, Color.Parse("#6B7D99"));
        var labelX = Math.Clamp(x - label.Width / 2d, 0d, Math.Max(0d, Bounds.Width - label.Width));
        context.DrawText(label, new Point(labelX, top + 5d));
    }

    private static double GetNiceInterval(double roughInterval)
    {
        if (roughInterval <= 0d)
            return 1d;
        var magnitude = Math.Pow(10d, Math.Floor(Math.Log10(roughInterval)));
        var normalized = roughInterval / magnitude;
        var nice =
            normalized <= 1d ? 1d
            : normalized <= 2d ? 2d
            : normalized <= 5d ? 5d
            : 10d;
        return nice * magnitude;
    }

    private static string FormatAxisTime(double seconds) =>
        ExperimentTimelineFormatter.Format(seconds);

    private static FormattedText CreateText(string value, double size, Color color) =>
        new(
            value,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                Application.Current?.FindResource("AlibabaPuHuiTi") as FontFamily
                    ?? TimelineFontFamily,
                FontStyle.Normal,
                FontWeight.Normal
            ),
            size,
            new SolidColorBrush(color)
        );
}
