using System;
using Avalonia;
using Avalonia.Controls;

namespace EGGtCSPlatform.Controls;

/// <summary>Two square scoring buttons sized from the word card's available content width.</summary>
public sealed class SpeechScoreButtonsPanel : Panel
{
    private static (double Side, double Gap) Dimensions(double width)
    {
        var gap = Math.Min(6, width);
        return (Math.Min(Math.Max(24, width * 0.3), Math.Max(0, (width - gap) / 2)), gap);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The word card normally provides a finite width; 54 is the minimum pair in an unconstrained host.
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 54;
        var (side, _) = Dimensions(width);
        foreach (var child in Children)
            child.Measure(new Size(side, side));
        return new Size(width, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (side, gap) = Dimensions(finalSize.Width);
        var x = (finalSize.Width - side * 2 - gap) / 2;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, 0, side, side));
            x += side + gap;
        }
        return finalSize;
    }
}
