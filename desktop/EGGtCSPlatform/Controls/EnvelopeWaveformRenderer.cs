using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

internal static class EnvelopeWaveformRenderer
{
    private const double Left = 317;
    private const double Width = 1055;
    private const double PixelsPerMilliAmp = 53;
    private static readonly IReadOnlyList<Point> Upper = Parse(EnvelopeWaveformTemplate.Upper);
    private static readonly IReadOnlyList<Point> Lower = Parse(EnvelopeWaveformTemplate.Lower);

    // The last baseline point before the first nonzero segment defines the onset.
    internal static double OnsetFraction { get; } = Upper.TakeWhile(p => p.Y == 0).Last().X;
    private static readonly IBrush AxisBrush = new SolidColorBrush(Color.Parse("#9AAAC2"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#E2E8F2")), 1);
    private static readonly IBrush CurveBrush = new SolidColorBrush(Color.Parse("#9536F3"));
    private static readonly IPen UpperPen = new Pen(CurveBrush, 1.5);
    private static readonly IPen LowerPen = new Pen(
        CurveBrush,
        1,
        new DashStyle(new double[] { 2, 2 }, 0)
    );

    internal static double TimeWindowMilliseconds => 40 / OnsetFraction;

    internal static double[] TimeTicks(double delay) =>
        Enumerable.Range(0, 11).Select(i => delay - 40 + TimeWindowMilliseconds * i / 10).ToArray();

    internal static Point[] Points(bool lower, double current) =>
        (lower ? Lower : Upper).Select(p => new Point(p.X, p.Y * current / 2)).ToArray();

    internal static string FormatTimeTick(double value)
    {
        var rounded = Math.Round(value, 3);
        return rounded == 0 ? "0" : rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }

    internal static void Render(
        DrawingContext context,
        Size size,
        WaveformDescriptor descriptor,
        bool compact
    )
    {
        if (size.Width < 100 || size.Height < 60)
            return;
        var plot = compact
            ? new Rect(5, 4, size.Width - 10, size.Height - 8)
            : new Rect(46, 12, size.Width - 62, size.Height - 55);
        if (!compact)
        {
            for (var i = 0; i <= 4; i++)
            {
                var y = plot.Top + plot.Height * i / 4;
                context.DrawLine(GridPen, new(plot.Left, y), new(plot.Right, y));
                Text(
                    context,
                    (2 - i).ToString(CultureInfo.InvariantCulture),
                    new(plot.Left - 22, y - 7)
                );
            }
            context.DrawLine(GridPen, plot.TopLeft, plot.BottomLeft);
            var ticks = TimeTicks(descriptor.DelayMilliseconds);
            for (var i = 0; i < ticks.Length; i++)
            {
                var label = FormatTimeTick(ticks[i]);
                Text(
                    context,
                    label,
                    new(plot.Left + plot.Width * i / 10 - label.Length * 2.6, plot.Bottom + 8)
                );
            }
            Text(context, "时间(ms)", new(plot.Right - 55, plot.Bottom + 27));
            using (
                context.PushTransform(
                    Matrix.CreateRotation(-Math.PI / 2)
                        * Matrix.CreateTranslation(12, plot.Top + 55)
                )
            )
                Text(context, "电流(mA)", new(0, 0));
        }
        using (context.PushClip(plot))
        {
            Draw(context, plot, Points(false, descriptor.Current), UpperPen);
            Draw(context, plot, Points(true, descriptor.Current), LowerPen);
        }
    }

    private static void Draw(
        DrawingContext context,
        Rect plot,
        IReadOnlyList<Point> points,
        IPen pen
    )
    {
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var point = new Point(
                    plot.Left + points[i].X * plot.Width,
                    plot.Top + (2 - points[i].Y) / 4 * plot.Height
                );
                if (i == 0)
                    stream.BeginFigure(point, false);
                else
                    stream.LineTo(point);
            }
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static void Text(DrawingContext context, string label, Point location) =>
        context.DrawText(
            new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                11,
                AxisBrush
            ),
            location
        );

    private static IReadOnlyList<Point> Parse(string path)
    {
        var points = new List<Point>();
        var baseline = 0d;
        var y = 0d;
        foreach (Match match in Regex.Matches(path, @"([MLH])([\d.]+)(?: ([\d.]+))?"))
        {
            var x = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (match.Groups[1].Value != "H")
                y = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (points.Count == 0)
                baseline = y;
            points.Add(new((x - Left) / Width, (baseline - y) / PixelsPerMilliAmp));
        }
        return points.AsReadOnly();
    }
}
