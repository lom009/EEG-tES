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

public sealed class RunLoadingSpinner : Control
{
    private readonly DispatcherTimer _timer;
    private int _frame;

    public static readonly StyledProperty<IBrush?> SpinnerBrushProperty = AvaloniaProperty.Register<
        RunLoadingSpinner,
        IBrush?
    >(nameof(SpinnerBrush), Brushes.White);

    public RunLoadingSpinner()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % 8;
            InvalidateVisual();
        };
    }

    public IBrush? SpinnerBrush
    {
        get => GetValue(SpinnerBrushProperty);
        set => SetValue(SpinnerBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2d, Bounds.Height / 2d);
        var radius = Math.Max(2d, Math.Min(Bounds.Width, Bounds.Height) / 2d - 2d);
        for (var index = 0; index < 8; index++)
        {
            var angle = (index * Math.PI / 4d) - Math.PI / 2d;
            var opacity = 0.18d + 0.82d * ((index - _frame + 8) % 8) / 7d;
            var point = new Point(
                center.X + Math.Cos(angle) * radius,
                center.Y + Math.Sin(angle) * radius
            );
            using (context.PushOpacity(opacity))
                context.DrawEllipse(SpinnerBrush ?? Brushes.White, null, point, 1.5d, 1.5d);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
