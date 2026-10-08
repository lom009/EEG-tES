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

[TemplatePart("PART_ManualButton", typeof(Button))]
[TemplatePart("PART_AutomaticButton", typeof(Button))]
public class RunModeSelector : TemplatedControl
{
    public static readonly StyledProperty<ExperimentRunMode> SelectedModeProperty =
        AvaloniaProperty.Register<RunModeSelector, ExperimentRunMode>(
            nameof(SelectedMode),
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<bool> IsLockedProperty = AvaloniaProperty.Register<
        RunModeSelector,
        bool
    >(nameof(IsLocked));

    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<RunModeSelector, ICommand?>(nameof(SelectCommand));

    public static readonly StyledProperty<bool> IsManualSelectedProperty =
        AvaloniaProperty.Register<RunModeSelector, bool>(nameof(IsManualSelected), true);

    public static readonly StyledProperty<bool> IsAutomaticSelectedProperty =
        AvaloniaProperty.Register<RunModeSelector, bool>(nameof(IsAutomaticSelected));

    public static readonly StyledProperty<double> ThumbOffsetProperty = AvaloniaProperty.Register<
        RunModeSelector,
        double
    >(nameof(ThumbOffset));

    public RunModeSelector()
    {
        UpdateModeState();
    }

    public ExperimentRunMode SelectedMode
    {
        get => GetValue(SelectedModeProperty);
        set => SetValue(SelectedModeProperty, value);
    }

    public bool IsLocked
    {
        get => GetValue(IsLockedProperty);
        set => SetValue(IsLockedProperty, value);
    }

    public ICommand? SelectCommand
    {
        get => GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    public bool IsManualSelected => GetValue(IsManualSelectedProperty);

    public bool IsAutomaticSelected => GetValue(IsAutomaticSelectedProperty);

    public double ThumbOffset => GetValue(ThumbOffsetProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedModeProperty)
            UpdateModeState();
    }

    private void UpdateModeState()
    {
        var automatic = SelectedMode == ExperimentRunMode.Automatic;
        SetCurrentValue(IsManualSelectedProperty, !automatic);
        SetCurrentValue(IsAutomaticSelectedProperty, automatic);
        SetCurrentValue(ThumbOffsetProperty, automatic ? 87d : 0d);
        PseudoClasses.Set(":manual", !automatic);
        PseudoClasses.Set(":automatic", automatic);
    }
}
