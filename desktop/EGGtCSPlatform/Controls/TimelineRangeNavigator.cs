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

public class TimelineRangeNavigator : TemplatedControl
{
    private static readonly BoxShadows HandleShadow = BoxShadows.Parse("0 1 4 0 #61041F48");
    private const double HandleInset = 16d;
    private double TrackWidth => Math.Max(1d, Bounds.Width - HandleInset * 2d);

    private enum DragTarget
    {
        None,
        Start,
        End,
        Window,
    }

    private DragTarget _dragTarget;
    private double _pressValue;
    private double _pressX;
    private double _pressStart;
    private double _pressEnd;
    private bool _normalizing;
    private bool _isDragging;

    public static readonly StyledProperty<IImage?> HandleImageProperty = AvaloniaProperty.Register<
        TimelineRangeNavigator,
        IImage?
    >(nameof(HandleImage));

    public IImage? HandleImage
    {
        get => GetValue(HandleImageProperty);
        set => SetValue(HandleImageProperty, value);
    }

    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<
        TimelineRangeNavigator,
        double
    >(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<
        TimelineRangeNavigator,
        double
    >(nameof(Maximum), 5d);

    public static readonly StyledProperty<double> ViewStartProperty = AvaloniaProperty.Register<
        TimelineRangeNavigator,
        double
    >(nameof(ViewStart), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> ViewEndProperty = AvaloniaProperty.Register<
        TimelineRangeNavigator,
        double
    >(nameof(ViewEnd), 5d, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> LiveValueProperty = AvaloniaProperty.Register<
        TimelineRangeNavigator,
        double
    >(nameof(LiveValue));

    public static readonly StyledProperty<bool> IsFollowingLatestProperty =
        AvaloniaProperty.Register<TimelineRangeNavigator, bool>(
            nameof(IsFollowingLatest),
            true,
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<double> MinimumSpanProperty = AvaloniaProperty.Register<
        TimelineRangeNavigator,
        double
    >(nameof(MinimumSpan), 1d);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<
        TimelineRangeNavigator,
        IBrush?
    >(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<TimelineRangeNavigator, IBrush?>(nameof(SelectionBrush));

    public static readonly DirectProperty<TimelineRangeNavigator, bool> IsDraggingProperty =
        AvaloniaProperty.RegisterDirect<TimelineRangeNavigator, bool>(
            nameof(IsDragging),
            control => control.IsDragging
        );

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
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

    public double LiveValue
    {
        get => GetValue(LiveValueProperty);
        set => SetValue(LiveValueProperty, value);
    }

    public bool IsFollowingLatest
    {
        get => GetValue(IsFollowingLatestProperty);
        set => SetValue(IsFollowingLatestProperty, value);
    }

    public double MinimumSpan
    {
        get => GetValue(MinimumSpanProperty);
        set => SetValue(MinimumSpanProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public bool IsDragging
    {
        get => _isDragging;
        private set => SetAndRaise(IsDraggingProperty, ref _isDragging, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0d)
            return;
        var centerY = Bounds.Height / 2d;
        var startX = ToX(ViewStart);
        var endX = ToX(ViewEnd);
        context.DrawRectangle(
            TrackBrush ?? new SolidColorBrush(Color.Parse("#E8EEF6")),
            null,
            new Rect(HandleInset, centerY - 4d, TrackWidth, 8d),
            4d,
            4d
        );
        context.DrawRectangle(
            SelectionBrush ?? new SolidColorBrush(Color.Parse("#BCD7FA")),
            null,
            new Rect(startX, centerY - 4d, Math.Max(0d, endX - startX), 8d),
            4d,
            4d
        );
        if (HandleImage is { } handleImage)
        {
            DrawHandleShadow(context, startX, centerY);
            DrawHandleShadow(context, endX, centerY);
            context.DrawImage(handleImage, new Rect(startX - 13d, centerY - 13d, 26d, 26d));
            context.DrawImage(handleImage, new Rect(endX - 13d, centerY - 13d, 26d, 26d));
        }
    }

    private static void DrawHandleShadow(DrawingContext context, double x, double y)
    {
        // Match the SVG's 20px circle; draw the shadow outside the SVG filter pipeline.
        context.DrawRectangle(
            Brushes.White,
            null,
            new Rect(x - 13d + 2.66675d, y - 13d + 2.66699d, 20d, 20d),
            10d,
            10d,
            HandleShadow
        );
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || Bounds.Width <= 0d)
            return;
        var x = point.Position.X;
        var startX = ToX(ViewStart);
        var endX = ToX(ViewEnd);
        _dragTarget =
            Math.Abs(x - startX) <= 10d ? DragTarget.Start
            : Math.Abs(x - endX) <= 10d ? DragTarget.End
            : DragTarget.Window;
        _pressValue = FromX(x);
        _pressX = x;
        _pressStart = ViewStart;
        _pressEnd = ViewEnd;
        if (_dragTarget == DragTarget.Window && (x < startX || x > endX))
        {
            var span = ViewEnd - ViewStart;
            var start = Math.Clamp(
                _pressValue - span / 2d,
                Minimum,
                Math.Max(Minimum, Maximum - span)
            );
            SetCurrentValue(ViewStartProperty, start);
            SetCurrentValue(ViewEndProperty, start + span);
            _pressStart = start;
            _pressEnd = start + span;
        }
        UpdateFollowingState();
        IsDragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (
            _dragTarget == DragTarget.None
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
        )
            return;
        var pointerX = e.GetPosition(this).X;
        var value = FromX(pointerX);
        switch (_dragTarget)
        {
            case DragTarget.Start:
                SetCurrentValue(ViewStartProperty, Math.Min(value, ViewEnd - MinimumSpan));
                break;
            case DragTarget.End:
                SetCurrentValue(ViewEndProperty, Math.Max(value, ViewStart + MinimumSpan));
                break;
            case DragTarget.Window:
                var delta = TimelineRangeMath.ScalePointerDelta(
                    pointerX - _pressX,
                    TrackWidth,
                    Minimum,
                    Maximum
                );
                var range = TimelineRangeMath.PanWindow(
                    Minimum,
                    Maximum,
                    _pressStart,
                    _pressEnd,
                    delta
                );
                SetCurrentValue(ViewStartProperty, range.Start);
                SetCurrentValue(ViewEndProperty, range.End);
                _pressValue = value;
                _pressX = pointerX;
                _pressStart = range.Start;
                _pressEnd = range.End;
                break;
        }
        NormalizeRange();
        UpdateFollowingState();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        UpdateFollowingState();
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndDrag();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (
            change.Property == MinimumProperty
            || change.Property == MaximumProperty
            || change.Property == ViewStartProperty
            || change.Property == ViewEndProperty
            || change.Property == MinimumSpanProperty
        )
            NormalizeRange();
        InvalidateVisual();
    }

    private void NormalizeRange()
    {
        if (_normalizing)
            return;
        _normalizing = true;
        try
        {
            var maximum = Math.Max(Minimum + Math.Max(0.001d, MinimumSpan), Maximum);
            var span = Math.Min(Math.Max(0.001d, MinimumSpan), maximum - Minimum);
            var start = Math.Clamp(ViewStart, Minimum, maximum - span);
            var end = Math.Clamp(ViewEnd, start + span, maximum);
            SetCurrentValue(MaximumProperty, maximum);
            SetCurrentValue(ViewStartProperty, start);
            SetCurrentValue(ViewEndProperty, end);
        }
        finally
        {
            _normalizing = false;
        }
    }

    private void UpdateFollowingState()
    {
        var tolerance = Math.Max(0.1d, (Maximum - Minimum) / TrackWidth * 8d);
        var initialWindow = LiveValue <= ViewEnd && Math.Abs(ViewStart - Minimum) <= tolerance;
        var atLiveEdge = Math.Abs(ViewEnd - LiveValue) <= tolerance;
        SetCurrentValue(IsFollowingLatestProperty, initialWindow || atLiveEdge);
    }

    private void EndDrag()
    {
        _dragTarget = DragTarget.None;
        IsDragging = false;
    }

    private double ToX(double value) =>
        HandleInset
        + (Math.Clamp(value, Minimum, Maximum) - Minimum)
            / Math.Max(0.001d, Maximum - Minimum)
            * TrackWidth;

    private double FromX(double x) =>
        Minimum + Math.Clamp((x - HandleInset) / TrackWidth, 0d, 1d) * (Maximum - Minimum);
}
