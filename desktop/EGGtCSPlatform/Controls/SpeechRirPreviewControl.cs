using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.Controls;

public sealed class SpeechRirPreviewControl : Control
{
    public static readonly StyledProperty<RirResponse?> ResponseProperty =
        AvaloniaProperty.Register<SpeechRirPreviewControl, RirResponse?>(nameof(Response));
    public RirResponse? Response
    {
        get => GetValue(ResponseProperty);
        set => SetValue(ResponseProperty, value);
    }

    static SpeechRirPreviewControl() => AffectsRender<SpeechRirPreviewControl>(ResponseProperty);

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size).Deflate(4);
        context.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray), rect);
        if (Response is not { } response || rect.Width <= 0 || rect.Height <= 0)
            return;
        var values = response.Effective;
        var mid = rect.Y + rect.Height / 2;
        context.DrawLine(new Pen(Brushes.LightGray), new(rect.X, mid), new(rect.Right, mid));
        var stride = Math.Max(1, (int)Math.Ceiling(values.Length / rect.Width));
        var pen = new Pen(Brushes.RoyalBlue, 1);
        for (var i = 0; i < values.Length; i += stride)
        {
            double min = 0,
                max = 0;
            for (var j = i; j < Math.Min(i + stride, values.Length); j++)
            {
                min = Math.Min(min, values[j]);
                max = Math.Max(max, values[j]);
            }
            var x = rect.X + rect.Width * i / values.Length;
            context.DrawLine(
                pen,
                new(x, mid - Math.Clamp(max, -1, 1) * rect.Height * .45),
                new(x, mid - Math.Clamp(min, -1, 1) * rect.Height * .45)
            );
        }
        void Marker(int frame, IBrush brush)
        {
            var x = rect.X + rect.Width * frame / values.Length;
            context.DrawLine(new Pen(brush, 1.5), new(x, rect.Y), new(x, rect.Bottom));
        }
        Marker(response.DirectFrame, Brushes.SeaGreen);
        Marker(response.DivisionFrame, Brushes.DarkOrange);
    }
}
