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

internal sealed class WaveformAmplitudeAxis : Control
{
    private static readonly IBrush Foreground = new SolidColorBrush(Color.Parse("#ACB7C8"));
    private static readonly IPen TickPen = new Pen(Foreground, 1d)
    {
        LineCap = PenLineCap.Flat,
        LineJoin = PenLineJoin.Miter,
    };

    public static readonly StyledProperty<double> MaximumAmplitudeProperty =
        AvaloniaProperty.Register<WaveformAmplitudeAxis, double>(nameof(MaximumAmplitude), 200d);

    public static readonly StyledProperty<bool> ShowUnitProperty = AvaloniaProperty.Register<
        WaveformAmplitudeAxis,
        bool
    >(nameof(ShowUnit));

    static WaveformAmplitudeAxis()
    {
        AffectsRender<WaveformAmplitudeAxis>(MaximumAmplitudeProperty, ShowUnitProperty);
    }

    public double MaximumAmplitude
    {
        get => GetValue(MaximumAmplitudeProperty);
        set => SetValue(MaximumAmplitudeProperty, value);
    }

    public bool ShowUnit
    {
        get => GetValue(ShowUnitProperty);
        set => SetValue(ShowUnitProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0d || Bounds.Height <= 0d)
            return;

        // Join the boundary ticks and axis in one path so both corners use a true miter join.
        var axis = new StreamGeometry();
        using (var geometry = axis.Open())
        {
            geometry.BeginFigure(new Point(Bounds.Width + 4d, 0d), false);
            geometry.LineTo(new Point(Bounds.Width, 0d));
            geometry.LineTo(new Point(Bounds.Width, Bounds.Height));
            geometry.LineTo(new Point(Bounds.Width + 4d, Bounds.Height));
            geometry.EndFigure(false);
        }
        context.DrawGeometry(null, TickPen, axis);

        for (var index = 0; index <= WaveformAmplitudeScale.IntervalCount; index++)
        {
            var y = Bounds.Height * index / WaveformAmplitudeScale.IntervalCount;
            var text = CreateText(
                WaveformAmplitudeScale.FormatTick(
                    WaveformAmplitudeScale.GetTickValue(MaximumAmplitude, index)
                )
            );
            if (index > 0 && index < WaveformAmplitudeScale.IntervalCount)
                context.DrawLine(
                    TickPen,
                    new Point(Bounds.Width, y),
                    new Point(Bounds.Width + 4d, y)
                );
            context.DrawText(text, new Point(Bounds.Width - 6d - text.Width, y - text.Height / 2d));
        }

        if (!ShowUnit)
            return;

        var unit = CreateText("μV");
        var centre = new Point(0d, unit.Width / 2d);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2d, centre)))
            context.DrawText(
                unit,
                new Point(centre.X - unit.Width / 2d, centre.Y - unit.Height / 2d)
            );
    }

    private static FormattedText CreateText(string text) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11d,
            Foreground
        );
}
