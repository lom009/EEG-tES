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

public static class TimelineRangeMath
{
    public static double ScalePointerDelta(
        double pointerDelta,
        double viewportWidth,
        double viewStart,
        double viewEnd
    )
    {
        if (
            !double.IsFinite(pointerDelta)
            || !double.IsFinite(viewportWidth)
            || viewportWidth <= 0d
        )
            return 0d;

        var visibleSpan = Math.Max(0d, viewEnd - viewStart);
        return pointerDelta / viewportWidth * visibleSpan;
    }

    public static (double Start, double End) PanWindow(
        double minimum,
        double maximum,
        double viewStart,
        double viewEnd,
        double delta
    )
    {
        var normalizedMaximum = Math.Max(minimum, maximum);
        var span = Math.Clamp(viewEnd - viewStart, 0d, normalizedMaximum - minimum);
        var start = Math.Clamp(viewStart + delta, minimum, normalizedMaximum - span);
        return (start, start + span);
    }
}
