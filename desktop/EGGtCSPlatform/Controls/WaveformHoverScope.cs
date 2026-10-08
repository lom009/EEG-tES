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

public class WaveformHoverScope : ContentControl
{
    public static readonly StyledProperty<WaveformHoverState?> HoverStateProperty =
        AvaloniaProperty.Register<WaveformHoverScope, WaveformHoverState?>(nameof(HoverState));

    public WaveformHoverState? HoverState
    {
        get => GetValue(HoverStateProperty);
        set => SetValue(HoverStateProperty, value);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoverState?.Clear();
    }
}
