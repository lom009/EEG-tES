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

internal static class WaveformAmplitudeScale
{
    public const int IntervalCount = 6;

    public static double GetTickValue(double maximumAmplitude, int index)
    {
        var magnitude = Math.Max(1d, maximumAmplitude);
        return index == IntervalCount / 2
            ? 0d
            : magnitude - index * (2d * magnitude / IntervalCount);
    }

    public static string FormatTick(double value)
    {
        var rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        return rounded == 0d ? "0" : rounded.ToString("0", CultureInfo.CurrentCulture);
    }

    public static double ToY(double value, double maximumAmplitude, double height)
    {
        var normalized = Math.Clamp(value / Math.Max(1d, maximumAmplitude), -1d, 1d);
        return height * (0.5d - normalized * 0.5d);
    }

    public static string FormatBoundary(double maximumAmplitude, bool positive)
    {
        var magnitude = Math.Max(1d, maximumAmplitude);
        return $"{(positive ? "+" : "-")}{magnitude:0.###} μV";
    }
}
