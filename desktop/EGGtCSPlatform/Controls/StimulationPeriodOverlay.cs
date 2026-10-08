using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EGGtCSPlatform.ViewModels.Pages;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.Controls;

// The interval and the waveform share timeline coordinates, including when panning history.
public sealed class StimulationPeriodOverlay : Decorator
{
    public static readonly StyledProperty<IReadOnlyList<ExperimentTimelineSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<StimulationPeriodOverlay, IReadOnlyList<ExperimentTimelineSegment>?>(nameof(Segments));
    public static readonly StyledProperty<double> ViewStartProperty = AvaloniaProperty.Register<StimulationPeriodOverlay, double>(nameof(ViewStart));
    public static readonly StyledProperty<double> ViewEndProperty = AvaloniaProperty.Register<StimulationPeriodOverlay, double>(nameof(ViewEnd));
    public static readonly StyledProperty<double> LiveValueProperty = AvaloniaProperty.Register<StimulationPeriodOverlay, double>(nameof(LiveValue));
    public static readonly StyledProperty<bool> ContinuousSpanProperty = AvaloniaProperty.Register<StimulationPeriodOverlay, bool>(nameof(ContinuousSpan), true);

    public IReadOnlyList<ExperimentTimelineSegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double ViewStart { get => GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }
    public double LiveValue { get => GetValue(LiveValueProperty); set => SetValue(LiveValueProperty, value); }
    public bool ContinuousSpan { get => GetValue(ContinuousSpanProperty); set => SetValue(ContinuousSpanProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty || change.Property == ViewStartProperty
            || change.Property == ViewEndProperty || change.Property == LiveValueProperty
            || change.Property == ContinuousSpanProperty)
        {
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    private bool TryGetActiveSpan(out TimelineViewportSpan span)
    {
        span = default;
        if (Segments is null) return false;
        foreach (var segment in Segments)
            if (segment.Stage == ExperimentRunStage.Stimulation
                && LiveValue >= segment.StartSeconds && LiveValue < segment.EndSeconds)
                return TimelineViewportMath.TryGetVisibleSpan(segment.StartSeconds, segment.EndSeconds,
                    ViewStart, ViewEnd, out span);
        return false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (ContinuousSpan && TryGetActiveSpan(out var span))
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#0DE46EFF")), null,
                new Rect(span.StartFraction * Bounds.Width, 0,
                    (span.EndFraction - span.StartFraction) * Bounds.Width, Bounds.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            var visible = TryGetActiveSpan(out var span);
            child.IsVisible = visible;
            if (visible)
            {
                var width = Math.Min(177, finalSize.Width);
                var height = Math.Min(104, finalSize.Height);
                var center = ContinuousSpan ? (span.StartFraction + span.EndFraction) * finalSize.Width / 2 : finalSize.Width / 2;
                child.Arrange(new Rect(Math.Clamp(center - width / 2, 0, finalSize.Width - width),
                    Math.Max(0, (finalSize.Height - height) / 2), width, height));
            }
        }
        return finalSize;
    }
}
