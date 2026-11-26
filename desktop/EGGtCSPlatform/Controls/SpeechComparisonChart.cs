using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public sealed class SpeechComparisonChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<SpeechRoundRow>> RowsProperty =
        AvaloniaProperty.Register<SpeechComparisonChart, IReadOnlyList<SpeechRoundRow>>(
            nameof(Rows),
            Array.Empty<SpeechRoundRow>()
        );
    public IReadOnlyList<SpeechRoundRow> Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }
    private readonly List<(Point Point, SpeechRoundRow Row)> _points = [];
    private static readonly IBrush Purple = new SolidColorBrush(Color.Parse("#A044FF"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#91A4C1"));
    private readonly TextBlock _tipNumber = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _tipCondition = new();
    private readonly TextBlock _tipResult = new();
    private SpeechRoundRow? _hoveredRow;

    static SpeechComparisonChart() => AffectsRender<SpeechComparisonChart>(RowsProperty);

    public SpeechComparisonChart()
    {
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 18,
                    Children = { _tipNumber, _tipCondition },
                },
                _tipResult,
            },
        };
        ToolTip.SetTip(this, new ToolTip
        {
            Foreground = new SolidColorBrush(Color.Parse("#EEEEEE")),
            FontSize = 12,
            Template = new FuncControlTemplate<ToolTip>((_, _) => new Border
            {
                Background = new SolidColorBrush(Color.Parse("#CC222222")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10),
                Child = content,
            }),
        });
        ToolTip.SetPlacement(this, PlacementMode.Pointer);
        PointerMoved += Hover;
        PointerExited += (_, _) => CloseTip();
        DetachedFromVisualTree += (_, _) => CloseTip();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty || change.Property == BoundsProperty)
        {
            _points.Clear();
            CloseTip();
        }
    }

    private void CloseTip()
    {
        _hoveredRow = null;
        ToolTip.SetIsOpen(this, false);
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private void Hover(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var near = _points.OrderBy(x => Distance(x.Point, p)).FirstOrDefault();
        if (near.Row is null || Distance(near.Point, p) > 32)
        {
            CloseTip();
            return;
        }
        if (!ReferenceEquals(_hoveredRow, near.Row))
        {
            // Reopen at the new pointer position when moving between training points.
            CloseTip();
            _hoveredRow = near.Row;
            var settings = near.Row.Round.Result.Settings;
            var list = int.TryParse(settings.SentenceList, out var number)
                ? number.ToString("D2", CultureInfo.InvariantCulture)
                : settings.SentenceList;
            _tipNumber.Text = $"第{near.Row.Round.Number}次";
            _tipCondition.Text = $"{settings.Corpus.ToLowerInvariant()}·{list} {near.Row.Round.DelayMilliseconds:0}ms";
            _tipResult.Text = near.Row.Unit == "%" && near.Row.Value is { } value
                ? value.ToString("0.00", CultureInfo.InvariantCulture) + "%"
                : near.Row.MainResult;
        }
        ToolTip.SetIsOpen(this, true);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, Bounds.WithX(0).WithY(0));
        _points.Clear();
        var width = Bounds.Width - 78;
        var height = Bounds.Height - 60;
        if (width <= 0 || height <= 0)
            return;
        var rows = Rows.Where(x => x.Value is not null).ToArray();
        var percent = rows.FirstOrDefault()?.Unit == "%";
        var min = percent || rows.Length == 0 ? 0 : Math.Floor(rows.Min(x => x.Value!.Value)) - 1;
        var max =
            percent || rows.Length == 0 ? 100 : Math.Ceiling(rows.Max(x => x.Value!.Value)) + 1;
        var grid = new Pen(new SolidColorBrush(Color.Parse("#DFE7F3")), 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = 24 + i * height / 4;
            context.DrawLine(grid, new(54, y), new(54 + width, y));
            Label(
                context,
                (max - (max - min) * i / 4).ToString("0.##", CultureInfo.InvariantCulture),
                new(4, y - 8)
            );
        }
        Label(context, rows.FirstOrDefault()?.Unit ?? "%", new(4, 0));
        for (var i = 0; i < rows.Length; i++)
        {
            var p = new Point(
                54 + width * (i + .5) / rows.Length,
                24 + height * (max - rows[i].Value!.Value) / (max - min)
            );
            if (_points.Count > 0)
                context.DrawLine(new Pen(Purple, 2), _points[^1].Point, p);
            context.DrawEllipse(Brushes.White, new Pen(Purple, 2), p, 3, 3);
            _points.Add((p, rows[i]));
            if (rows.Length <= 12 || i % Math.Max(1, rows.Length / 8) == 0 || i == rows.Length - 1)
                Label(context, rows[i].Number, new(p.X - 22, 32 + height));
        }
    }

    private static void Label(DrawingContext context, string text, Point point) =>
        context.DrawText(
            new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                11,
                Muted
            ),
            point
        );
}
