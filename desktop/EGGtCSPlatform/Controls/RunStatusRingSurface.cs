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

public sealed class RunStatusRingSurface : Control
{
    public static readonly StyledProperty<ExperimentRunStage> StateProperty =
        AvaloniaProperty.Register<RunStatusRingSurface, ExperimentRunStage>(nameof(State));

    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<
        RunStatusRingSurface,
        double
    >(nameof(Progress));

    public ExperimentRunStage State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4d)
            return;

        var center = new Point(Bounds.Width / 2d, Bounds.Height / 2d);
        var radius = size / 2d - 3d;
        var (accent, track, fill) = GetPalette(State);
        context.DrawEllipse(
            new SolidColorBrush(fill),
            new Pen(new SolidColorBrush(track), 2d),
            center,
            radius,
            radius
        );
        context.DrawEllipse(
            null,
            new Pen(
                new SolidColorBrush(Color.Parse("#DDE8E3")),
                1d,
                dashStyle: new DashStyle([1d, 3d], 0d)
            ),
            center,
            radius - 9d,
            radius - 9d
        );

        if (
            State
            is not (
                ExperimentRunStage.Acquisition
                or ExperimentRunStage.Blanking
                or ExperimentRunStage.Stimulation
                or ExperimentRunStage.Recovery
                or ExperimentRunStage.Completed
            )
        )
            return;

        var progress =
            State == ExperimentRunStage.Completed ? 1d : Math.Clamp(Progress, 0.025d, 1d);
        var sweep = progress * Math.PI * 2d;
        var startAngle = -Math.PI / 2d;
        var endAngle = startAngle + sweep;
        var arcRadius = radius - 1d;
        var start = new Point(
            center.X + Math.Cos(startAngle) * arcRadius,
            center.Y + Math.Sin(startAngle) * arcRadius
        );
        var end = new Point(
            center.X + Math.Cos(endAngle) * arcRadius,
            center.Y + Math.Sin(endAngle) * arcRadius
        );
        if (progress >= 0.999d)
        {
            context.DrawEllipse(
                null,
                new Pen(new SolidColorBrush(accent), 3d),
                center,
                arcRadius,
                arcRadius
            );
        }
        else
        {
            var geometry = new StreamGeometry();
            using (var geometryContext = geometry.Open())
            {
                geometryContext.BeginFigure(start, false);
                geometryContext.ArcTo(
                    end,
                    new Size(arcRadius, arcRadius),
                    0d,
                    progress > 0.5d,
                    SweepDirection.Clockwise
                );
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(accent), 3d), geometry);
            context.DrawEllipse(new SolidColorBrush(accent), null, end, 4d, 4d);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty || change.Property == ProgressProperty)
            InvalidateVisual();
    }

    private static (Color Accent, Color Track, Color Fill) GetPalette(ExperimentRunStage state) =>
        state switch
        {
            ExperimentRunStage.Stopped => (
                Color.Parse("#E65058"),
                Color.Parse("#F5C9CC"),
                Color.Parse("#FFF7F7")
            ),
            ExperimentRunStage.Standby => (
                Color.Parse("#788AA1"),
                Color.Parse("#C9D3DF"),
                Color.Parse("#FBFCFD")
            ),
            _ => (Color.Parse("#36A66F"), Color.Parse("#CDE8D9"), Color.Parse("#F9FDFC")),
        };
}
