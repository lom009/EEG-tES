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

internal sealed class WaveformPlotSurface : Control
{
    private WaveformHoverState? _observedHoverState;

    public static readonly StyledProperty<IReadOnlyList<TimedWaveformPoint>?> SamplesProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, IReadOnlyList<TimedWaveformPoint>?>(
            nameof(Samples)
        );

    public static readonly StyledProperty<IWaveformSampleLookup?> SampleLookupProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, IWaveformSampleLookup?>(
            nameof(SampleLookup)
        );

    public static readonly StyledProperty<IBrush?> WaveformBrushProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, IBrush?>(nameof(WaveformBrush));

    public static readonly StyledProperty<double> WaveformStrokeThicknessProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, double>(
            nameof(WaveformStrokeThickness),
            DisplayOptions.DefaultWaveformStrokeThickness
        );

    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<
        WaveformPlotSurface,
        IBrush?
    >(nameof(GridBrush));

    public static readonly StyledProperty<double> MaximumAmplitudeProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, double>(nameof(MaximumAmplitude), 200d);

    public static readonly StyledProperty<bool> ShowXCursorProperty = AvaloniaProperty.Register<
        WaveformPlotSurface,
        bool
    >(nameof(ShowXCursor));

    public static readonly StyledProperty<bool> ShowYCursorProperty = AvaloniaProperty.Register<
        WaveformPlotSurface,
        bool
    >(nameof(ShowYCursor), true);

    public static readonly StyledProperty<ExperimentRunStage> StageProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, ExperimentRunStage>(nameof(Stage));

    public static readonly StyledProperty<bool> ContinuousStimulationOverlayProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, bool>(nameof(ContinuousStimulationOverlay));
    public bool ContinuousStimulationOverlay
    {
        get => GetValue(ContinuousStimulationOverlayProperty);
        set => SetValue(ContinuousStimulationOverlayProperty, value);
    }

    public static readonly StyledProperty<string?> ChannelIdProperty = AvaloniaProperty.Register<
        WaveformPlotSurface,
        string?
    >(nameof(ChannelId));

    public static readonly StyledProperty<double> ViewStartProperty = AvaloniaProperty.Register<
        WaveformPlotSurface,
        double
    >(nameof(ViewStart));

    public static readonly StyledProperty<double> ViewEndProperty = AvaloniaProperty.Register<
        WaveformPlotSurface,
        double
    >(nameof(ViewEnd), 10d);

    public static readonly StyledProperty<WaveformHoverState?> HoverStateProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, WaveformHoverState?>(nameof(HoverState));

    public static readonly StyledProperty<IReadOnlyList<ExperimentTimelineSegment>?> TimelineSegmentsProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, IReadOnlyList<ExperimentTimelineSegment>?>(
            nameof(TimelineSegments)
        );

    public static readonly StyledProperty<double> LiveValueProperty = AvaloniaProperty.Register<
        WaveformPlotSurface,
        double
    >(nameof(LiveValue));

    public static readonly StyledProperty<double> DataAvailableThroughProperty =
        AvaloniaProperty.Register<WaveformPlotSurface, double>(nameof(DataAvailableThrough));

    public IReadOnlyList<TimedWaveformPoint>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IWaveformSampleLookup? SampleLookup
    {
        get => GetValue(SampleLookupProperty);
        set => SetValue(SampleLookupProperty, value);
    }

    public IBrush? WaveformBrush
    {
        get => GetValue(WaveformBrushProperty);
        set => SetValue(WaveformBrushProperty, value);
    }

    public double WaveformStrokeThickness
    {
        get => GetValue(WaveformStrokeThicknessProperty);
        set => SetValue(WaveformStrokeThicknessProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public double MaximumAmplitude
    {
        get => GetValue(MaximumAmplitudeProperty);
        set => SetValue(MaximumAmplitudeProperty, value);
    }

    public bool ShowXCursor
    {
        get => GetValue(ShowXCursorProperty);
        set => SetValue(ShowXCursorProperty, value);
    }

    public bool ShowYCursor
    {
        get => GetValue(ShowYCursorProperty);
        set => SetValue(ShowYCursorProperty, value);
    }

    public ExperimentRunStage Stage
    {
        get => GetValue(StageProperty);
        set => SetValue(StageProperty, value);
    }

    public string? ChannelId
    {
        get => GetValue(ChannelIdProperty);
        set => SetValue(ChannelIdProperty, value);
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

    public WaveformHoverState? HoverState
    {
        get => GetValue(HoverStateProperty);
        set => SetValue(HoverStateProperty, value);
    }

    public IReadOnlyList<ExperimentTimelineSegment>? TimelineSegments
    {
        get => GetValue(TimelineSegmentsProperty);
        set => SetValue(TimelineSegmentsProperty, value);
    }

    public double LiveValue
    {
        get => GetValue(LiveValueProperty);
        set => SetValue(LiveValueProperty, value);
    }

    public double DataAvailableThrough
    {
        get => GetValue(DataAvailableThroughProperty);
        set => SetValue(DataAvailableThroughProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0d || height <= 0d)
            return;

        DrawRevealedStimulationIntervals(context, width, height);

        var gridPen = new Pen(GridBrush ?? new SolidColorBrush(Color.Parse("#DCE6F4")), 1d);
        for (var column = 0; column <= 9; column++)
        {
            var x = width * column / 9d;
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, height));
        }
        for (var row = 0; row <= WaveformAmplitudeScale.IntervalCount; row++)
        {
            var y = height * row / WaveformAmplitudeScale.IntervalCount;
            context.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        if (Samples is { Count: > 1 } samples)
        {
            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                var hasFigure = false;
                for (var index = 0; index < samples.Count; index++)
                {
                    var sample = samples[index];
                    var x =
                        (sample.TimeSeconds - ViewStart)
                        / Math.Max(0.001d, ViewEnd - ViewStart)
                        * width;
                    var y = WaveformAmplitudeScale.ToY(sample.Value, MaximumAmplitude, height);
                    if (!hasFigure || sample.StartsNewSegment)
                    {
                        geometryContext.BeginFigure(new Point(x, y), false);
                        hasFigure = true;
                    }
                    else
                        geometryContext.LineTo(new Point(x, y));
                }
            }
            context.DrawGeometry(
                null,
                new Pen(WaveformBrush ?? Brushes.MediumPurple, WaveformStrokeThickness),
                geometry
            );
        }

        var hover = HoverState;
        if (
            hover is not { IsActive: true }
            || hover.TimeSeconds < ViewStart
            || hover.TimeSeconds > ViewEnd
        )
            return;

        var hoverX =
            (hover.TimeSeconds - ViewStart) / Math.Max(0.001d, ViewEnd - ViewStart) * width;
        var cursorPen = new Pen(
            new SolidColorBrush(Color.Parse("#7F8FA6")),
            1d,
            dashStyle: new DashStyle([4d, 4d], 0d)
        );
        if (ShowXCursor)
            context.DrawLine(cursorPen, new Point(hoverX, 0), new Point(hoverX, height));

        ExperimentTimelineSegment? acquisitionSegment = null;
        if (TimelineSegments is { Count: > 0 } segments)
        {
            foreach (var segment in segments)
            {
                if (
                    segment.Stage == ExperimentRunStage.Acquisition
                    && hover.TimeSeconds >= segment.StartSeconds
                    && hover.TimeSeconds < segment.EndSeconds
                )
                {
                    acquisitionSegment = segment;
                    break;
                }
            }
        }

        var coordinate = default(WaveformHoverSampleResult);
        var hasCoordinate =
            acquisitionSegment is not null
            && hover.TimeSeconds <= DataAvailableThrough
            && SampleLookup?.TryResolveHoverSample(
                hover.TimeSeconds,
                acquisitionSegment.StartSeconds,
                Math.Min(acquisitionSegment.EndSeconds, DataAvailableThrough),
                out coordinate
            ) == true;
        var hoverY = hasCoordinate ? ToY(coordinate.Value, height) : height / 2d;
        if (hasCoordinate && ShowYCursor)
        {
            context.DrawLine(cursorPen, new Point(0, hoverY), new Point(width, hoverY));
            var pointBrush = WaveformBrush ?? Brushes.MediumPurple;
            context.DrawEllipse(
                pointBrush,
                new Pen(Brushes.White, 1d),
                new Point(hoverX, hoverY),
                3.5d,
                3.5d
            );
        }

        // A tooltip has no waveform value to report until acquisition data is available.
        if (hasCoordinate)
            DrawHoverCard(
                context,
                width,
                height,
                new Point(hoverX, hoverY),
                hover.TimeSeconds,
                coordinate
            );
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (
            change.Property == SamplesProperty
            || change.Property == SampleLookupProperty
            || change.Property == MaximumAmplitudeProperty
            || change.Property == ShowXCursorProperty
            || change.Property == ShowYCursorProperty
            || change.Property == ContinuousStimulationOverlayProperty
            || change.Property == StageProperty
            || change.Property == TimelineSegmentsProperty
            || change.Property == LiveValueProperty
            || change.Property == DataAvailableThroughProperty
            || change.Property == WaveformBrushProperty
            || change.Property == WaveformStrokeThicknessProperty
            || change.Property == ViewStartProperty
            || change.Property == ViewEndProperty
        )
            InvalidateVisual();
        if (change.Property == HoverStateProperty)
            ObserveHoverState(change.GetNewValue<WaveformHoverState?>());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveHoverState(HoverState);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ObserveHoverState(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void DrawHoverCard(
        DrawingContext context,
        double width,
        double height,
        Point hoverPoint,
        double timeSeconds,
        WaveformHoverSampleResult? coordinate
    )
    {
        var hoverText = coordinate switch
        {
            { UsesNearestPoint: true } nearest =>
                $"{ChannelId ?? "通道"}   {nearest.Value:0.###} μV   最近点\n采样 {FormatTime(nearest.SampleTimeSeconds)}   光标 {FormatTime(timeSeconds)}",
            { } value =>
                $"{ChannelId ?? "通道"}   {value.Value:0.###} μV   {FormatTime(timeSeconds)}",
            _ => $"{ChannelId ?? "通道"}   -- μV   {FormatTime(timeSeconds)}",
        };
        var text = new FormattedText(
            hoverText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11d,
            Brushes.White
        );
        var firstLineHeight = text.Height;
        var lineBreakIndex = hoverText.IndexOf('\n');
        if (lineBreakIndex >= 0)
        {
            firstLineHeight = new FormattedText(
                hoverText[..lineBreakIndex],
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                11d,
                Brushes.White
            ).Height;
        }
        var cardWidth = text.Width + 28d;
        var cardHeight = text.Height + 10d;
        var x = Math.Clamp(hoverPoint.X + 10d, 4d, Math.Max(4d, width - cardWidth - 4d));
        var preferredY = hoverPoint.Y - cardHeight - 8d;
        var y =
            preferredY >= 4d ? preferredY : Math.Min(height - cardHeight - 4d, hoverPoint.Y + 8d);
        var card = new Rect(x, Math.Max(4d, y), cardWidth, cardHeight);
        context.DrawRectangle(
            new SolidColorBrush(Color.Parse("#180F172A")),
            null,
            new Rect(card.X, card.Y + 2d, card.Width, card.Height),
            6d,
            6d
        );
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#CC000000")), null, card, 6d, 6d);
        context.DrawEllipse(
            WaveformBrush ?? Brushes.MediumPurple,
            null,
            new Point(card.X + 11d, card.Y + 5d + firstLineHeight / 2d),
            3d,
            3d
        );
        context.DrawText(text, new Point(card.X + 20d, card.Y + 5d));
    }

    private void DrawRevealedStimulationIntervals(
        DrawingContext context,
        double width,
        double height
    )
    {
        if (TimelineSegments is not { Count: > 0 } segments)
            return;

        var fill = new SolidColorBrush(Color.Parse("#0DE46EFF"));
        foreach (var segment in segments)
        {
            if (
                segment.Stage != ExperimentRunStage.Stimulation
                || (ContinuousStimulationOverlay && Stage == ExperimentRunStage.Stimulation
                    && LiveValue >= segment.StartSeconds && LiveValue < segment.EndSeconds)
                || !TimelineViewportMath.TryGetVisibleSpan(
                    segment.StartSeconds,
                    segment.EndSeconds,
                    ViewStart,
                    ViewEnd,
                    out var visible,
                    LiveValue
                )
            )
                continue;

            var left = visible.StartFraction * width;
            var right = visible.EndFraction * width;
            context.DrawRectangle(fill, null, new Rect(left, 0d, right - left, height));
        }
    }

    private double ToY(double value, double height) =>
        WaveformAmplitudeScale.ToY(value, MaximumAmplitude, height);

    private void ObserveHoverState(WaveformHoverState? state)
    {
        if (ReferenceEquals(_observedHoverState, state))
            return;
        if (_observedHoverState is not null)
            _observedHoverState.PropertyChanged -= OnHoverStatePropertyChanged;
        _observedHoverState = state;
        if (_observedHoverState is not null)
            _observedHoverState.PropertyChanged += OnHoverStatePropertyChanged;
        InvalidateVisual();
    }

    private void OnHoverStatePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        InvalidateVisual();

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        return value.TotalHours >= 1d
            ? value.ToString(@"hh\:mm\:ss\.fff")
            : value.ToString(@"mm\:ss\.fff");
    }
}
