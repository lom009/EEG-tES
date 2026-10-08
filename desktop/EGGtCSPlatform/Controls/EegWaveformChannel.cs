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

[TemplatePart("PART_WaveformSurface", typeof(WaveformPlotSurface))]
public class EegWaveformChannel : TemplatedControl
{
    private WaveformPlotSurface? _waveformSurface;
    private bool _isPanning;
    private double _pressX;
    private double _pressViewStart;
    private double _pressViewEnd;

    public static readonly StyledProperty<bool> ContinuousStimulationOverlayProperty =
        AvaloniaProperty.Register<EegWaveformChannel, bool>(nameof(ContinuousStimulationOverlay));
    public bool ContinuousStimulationOverlay
    {
        get => GetValue(ContinuousStimulationOverlayProperty);
        set => SetValue(ContinuousStimulationOverlayProperty, value);
    }

    public static readonly StyledProperty<string?> ChannelIdProperty = AvaloniaProperty.Register<
        EegWaveformChannel,
        string?
    >(nameof(ChannelId));

    public static readonly StyledProperty<IReadOnlyList<TimedWaveformPoint>?> SamplesProperty =
        AvaloniaProperty.Register<EegWaveformChannel, IReadOnlyList<TimedWaveformPoint>?>(
            nameof(Samples)
        );

    public static readonly StyledProperty<IWaveformSampleLookup?> SampleLookupProperty =
        AvaloniaProperty.Register<EegWaveformChannel, IWaveformSampleLookup?>(nameof(SampleLookup));

    public static readonly StyledProperty<IBrush?> WaveformBrushProperty =
        AvaloniaProperty.Register<EegWaveformChannel, IBrush?>(nameof(WaveformBrush));

    public static readonly StyledProperty<double> WaveformStrokeThicknessProperty =
        AvaloniaProperty.Register<EegWaveformChannel, double>(
            nameof(WaveformStrokeThickness),
            DisplayOptions.DefaultWaveformStrokeThickness
        );

    public static readonly StyledProperty<IBrush?> ChannelBackgroundProperty =
        AvaloniaProperty.Register<EegWaveformChannel, IBrush?>(nameof(ChannelBackground));

    public static readonly StyledProperty<double> MaximumAmplitudeProperty =
        AvaloniaProperty.Register<EegWaveformChannel, double>(nameof(MaximumAmplitude), 200d);

    public static readonly StyledProperty<bool> ShowAmplitudeUnitProperty =
        AvaloniaProperty.Register<EegWaveformChannel, bool>(nameof(ShowAmplitudeUnit));

    public static readonly StyledProperty<bool> ShowXCursorProperty = AvaloniaProperty.Register<
        EegWaveformChannel,
        bool
    >(nameof(ShowXCursor));

    public static readonly StyledProperty<bool> ShowYCursorProperty = AvaloniaProperty.Register<
        EegWaveformChannel,
        bool
    >(nameof(ShowYCursor), true);

    public static readonly StyledProperty<ExperimentRunStage> StageProperty =
        AvaloniaProperty.Register<EegWaveformChannel, ExperimentRunStage>(nameof(Stage));

    public static readonly StyledProperty<double> ViewStartProperty = AvaloniaProperty.Register<
        EegWaveformChannel,
        double
    >(nameof(ViewStart), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> ViewEndProperty = AvaloniaProperty.Register<
        EegWaveformChannel,
        double
    >(nameof(ViewEnd), 10d, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> TimelineMinimumProperty =
        AvaloniaProperty.Register<EegWaveformChannel, double>(nameof(TimelineMinimum));

    public static readonly StyledProperty<double> TimelineMaximumProperty =
        AvaloniaProperty.Register<EegWaveformChannel, double>(nameof(TimelineMaximum), 10d);

    public static readonly StyledProperty<bool> IsFollowingLatestProperty =
        AvaloniaProperty.Register<EegWaveformChannel, bool>(
            nameof(IsFollowingLatest),
            true,
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<WaveformHoverState?> HoverStateProperty =
        AvaloniaProperty.Register<EegWaveformChannel, WaveformHoverState?>(nameof(HoverState));

    public static readonly StyledProperty<IReadOnlyList<ExperimentTimelineSegment>?> TimelineSegmentsProperty =
        AvaloniaProperty.Register<EegWaveformChannel, IReadOnlyList<ExperimentTimelineSegment>?>(
            nameof(TimelineSegments)
        );

    public static readonly StyledProperty<double> LiveValueProperty = AvaloniaProperty.Register<
        EegWaveformChannel,
        double
    >(nameof(LiveValue));

    public static readonly StyledProperty<double> DataAvailableThroughProperty =
        AvaloniaProperty.Register<EegWaveformChannel, double>(nameof(DataAvailableThrough));

    public string? ChannelId
    {
        get => GetValue(ChannelIdProperty);
        set => SetValue(ChannelIdProperty, value);
    }

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

    public IBrush? ChannelBackground
    {
        get => GetValue(ChannelBackgroundProperty);
        set => SetValue(ChannelBackgroundProperty, value);
    }

    public double MaximumAmplitude
    {
        get => GetValue(MaximumAmplitudeProperty);
        set => SetValue(MaximumAmplitudeProperty, value);
    }

    public bool ShowAmplitudeUnit
    {
        get => GetValue(ShowAmplitudeUnitProperty);
        set => SetValue(ShowAmplitudeUnitProperty, value);
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

    public double TimelineMinimum
    {
        get => GetValue(TimelineMinimumProperty);
        set => SetValue(TimelineMinimumProperty, value);
    }

    public double TimelineMaximum
    {
        get => GetValue(TimelineMaximumProperty);
        set => SetValue(TimelineMaximumProperty, value);
    }

    public bool IsFollowingLatest
    {
        get => GetValue(IsFollowingLatestProperty);
        set => SetValue(IsFollowingLatestProperty, value);
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

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _waveformSurface = e.NameScope.Find<WaveformPlotSurface>("PART_WaveformSurface");
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_waveformSurface is not { Bounds.Width: > 0d, Bounds.Height: > 0d } surface)
            return;
        var position = e.GetPosition(surface);
        if (_isPanning && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var delta = TimelineRangeMath.ScalePointerDelta(
                _pressX - position.X,
                surface.Bounds.Width,
                _pressViewStart,
                _pressViewEnd
            );
            var range = TimelineRangeMath.PanWindow(
                TimelineMinimum,
                TimelineMaximum,
                _pressViewStart,
                _pressViewEnd,
                delta
            );
            SetCurrentValue(ViewStartProperty, range.Start);
            SetCurrentValue(ViewEndProperty, range.End);
            UpdateFollowingState();
            e.Handled = true;
        }
        if (
            position.X < 0d
            || position.X > surface.Bounds.Width
            || position.Y < 0d
            || position.Y > surface.Bounds.Height
        )
            return;
        var fraction = Math.Clamp(position.X / surface.Bounds.Width, 0d, 1d);
        HoverState?.Update(ChannelId ?? "通道", ViewStart + (ViewEnd - ViewStart) * fraction);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (
            _waveformSurface is not { Bounds.Width: > 0d } surface
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
        )
            return;
        var position = e.GetPosition(surface);
        if (position.X < 0d || position.X > surface.Bounds.Width)
            return;
        _isPanning = true;
        _pressX = position.X;
        _pressViewStart = ViewStart;
        _pressViewEnd = ViewEnd;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isPanning)
            return;
        _isPanning = false;
        UpdateFollowingState();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void UpdateFollowingState()
    {
        var width = Math.Max(1d, _waveformSurface?.Bounds.Width ?? Bounds.Width);
        var tolerance = Math.Max(0.1d, (TimelineMaximum - TimelineMinimum) / width * 8d);
        var initialWindow =
            LiveValue <= ViewEnd && Math.Abs(ViewStart - TimelineMinimum) <= tolerance;
        var atLiveEdge = Math.Abs(ViewEnd - LiveValue) <= tolerance;
        SetCurrentValue(IsFollowingLatestProperty, initialWindow || atLiveEdge);
    }
}
