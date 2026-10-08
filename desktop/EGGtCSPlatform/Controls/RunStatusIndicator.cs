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

[TemplatePart("PART_StatusRing", typeof(Ellipse))]
[TemplatePart("PART_InnerRing", typeof(Ellipse))]
[TemplatePart("PART_StatusIcon", typeof(TextBlock))]
public class RunStatusIndicator : TemplatedControl
{
    public static readonly StyledProperty<ExperimentRunStage> StateProperty =
        AvaloniaProperty.Register<RunStatusIndicator, ExperimentRunStage>(nameof(State));

    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<
        RunStatusIndicator,
        double
    >(nameof(Progress));

    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<
        RunStatusIndicator,
        string?
    >(nameof(Text));

    public ExperimentRunStage State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != StateProperty)
            return;
        PseudoClasses.Set(
            ":active",
            State
                is ExperimentRunStage.Acquisition
                    or ExperimentRunStage.Blanking
                    or ExperimentRunStage.Stimulation
                    or ExperimentRunStage.Recovery
        );
        PseudoClasses.Set(":danger", State == ExperimentRunStage.Stopped);
        PseudoClasses.Set(":completed", State == ExperimentRunStage.Completed);
        PseudoClasses.Set(":stimulating", State == ExperimentRunStage.Stimulation);
    }
}
