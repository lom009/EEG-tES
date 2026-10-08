using System;
using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace EGGtCSPlatform.Controls;

public sealed class RadarWaveIndicator : Control
{
    private readonly DispatcherTimer _timer;
    private int _frame;
    private bool _isAttached;

    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<
        RadarWaveIndicator,
        bool
    >(nameof(IsActive));

    public RadarWaveIndicator()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % 20;
            InvalidateVisual();
        };
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2d, Bounds.Height / 2d);
        var accent = Color.Parse("#EF2B2D");
        context.DrawEllipse(new SolidColorBrush(accent), null, center, 2d, 2d);
        var phase = _frame / 20d;
        for (var index = 0; index < 2; index++)
        {
            var progress = (phase + index * 0.5d) % 1d;
            var radius =
                3d + progress * Math.Max(3d, Math.Min(Bounds.Width, Bounds.Height) / 2d - 3d);
            var alpha = (byte)(220d * (1d - progress));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
            context.DrawEllipse(null, new Pen(brush, 1.4d), center, radius, radius);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
            UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateTimer()
    {
        if (_isAttached && IsActive)
            _timer.Start();
        else
            _timer.Stop();
        InvalidateVisual();
    }
}
