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

[TemplatePart("PART_StageCard", typeof(Border))]
[TemplatePart("PART_DurationInput", typeof(ExperimentDurationInput))]
[TemplatePart("PART_UnitSelector", typeof(ComboBox))]
public class ExperimentStageControl : TemplatedControl
{
    public static readonly StyledProperty<int> IndexProperty = AvaloniaProperty.Register<
        ExperimentStageControl,
        int
    >(nameof(Index));

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<
        ExperimentStageControl,
        string?
    >(nameof(Title));

    public static readonly StyledProperty<ExperimentRunStage> StageProperty =
        AvaloniaProperty.Register<ExperimentStageControl, ExperimentRunStage>(nameof(Stage));

    public static readonly StyledProperty<decimal?> DurationValueProperty =
        AvaloniaProperty.Register<ExperimentStageControl, decimal?>(
            nameof(DurationValue),
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<IEnumerable?> UnitsProperty = AvaloniaProperty.Register<
        ExperimentStageControl,
        IEnumerable?
    >(nameof(Units));

    public static readonly StyledProperty<DurationUnitOption?> SelectedUnitProperty =
        AvaloniaProperty.Register<ExperimentStageControl, DurationUnitOption?>(
            nameof(SelectedUnit),
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<int> DurationStepMillisecondsProperty =
        AvaloniaProperty.Register<ExperimentStageControl, int>(
            nameof(DurationStepMilliseconds),
            1000
        );

    public static readonly StyledProperty<int> DurationMaximumMillisecondsProperty =
        AvaloniaProperty.Register<ExperimentStageControl, int>(
            nameof(DurationMaximumMilliseconds),
            65535000
        );

    public static readonly StyledProperty<int> DurationMinimumMillisecondsProperty =
        AvaloniaProperty.Register<ExperimentStageControl, int>(
            nameof(DurationMinimumMilliseconds),
            1000
        );

    public static readonly StyledProperty<ExperimentStageStatus> StatusProperty =
        AvaloniaProperty.Register<ExperimentStageControl, ExperimentStageStatus>(nameof(Status));

    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<
        ExperimentStageControl,
        double
    >(nameof(Progress));

    public static readonly StyledProperty<bool> IsDurationEditableProperty =
        AvaloniaProperty.Register<ExperimentStageControl, bool>(nameof(IsDurationEditable));

    public static readonly StyledProperty<string?> StatusTextProperty = AvaloniaProperty.Register<
        ExperimentStageControl,
        string?
    >(nameof(StatusText), "待执行");

    public static readonly StyledProperty<string?> ValidationTextProperty =
        AvaloniaProperty.Register<ExperimentStageControl, string?>(nameof(ValidationText));

    public int Index
    {
        get => GetValue(IndexProperty);
        set => SetValue(IndexProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public ExperimentRunStage Stage
    {
        get => GetValue(StageProperty);
        set => SetValue(StageProperty, value);
    }

    public decimal? DurationValue
    {
        get => GetValue(DurationValueProperty);
        set => SetValue(DurationValueProperty, value);
    }

    public IEnumerable? Units
    {
        get => GetValue(UnitsProperty);
        set => SetValue(UnitsProperty, value);
    }

    public DurationUnitOption? SelectedUnit
    {
        get => GetValue(SelectedUnitProperty);
        set => SetValue(SelectedUnitProperty, value);
    }

    public int DurationStepMilliseconds
    {
        get => GetValue(DurationStepMillisecondsProperty);
        set => SetValue(DurationStepMillisecondsProperty, value);
    }

    public int DurationMaximumMilliseconds
    {
        get => GetValue(DurationMaximumMillisecondsProperty);
        set => SetValue(DurationMaximumMillisecondsProperty, value);
    }

    public int DurationMinimumMilliseconds
    {
        get => GetValue(DurationMinimumMillisecondsProperty);
        set => SetValue(DurationMinimumMillisecondsProperty, value);
    }

    public ExperimentStageStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool IsDurationEditable
    {
        get => GetValue(IsDurationEditableProperty);
        set => SetValue(IsDurationEditableProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string? ValidationText
    {
        get => GetValue(ValidationTextProperty);
        set => SetValue(ValidationTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatusProperty || change.Property == StageProperty)
        {
            PseudoClasses.Set(":running", Status == ExperimentStageStatus.Running);
            PseudoClasses.Set(":completed", Status == ExperimentStageStatus.Completed);
            PseudoClasses.Set(":stopped", Status == ExperimentStageStatus.Stopped);
            PseudoClasses.Set(":acquisition", Stage == ExperimentRunStage.Acquisition);
            PseudoClasses.Set(":blanking", Stage == ExperimentRunStage.Blanking);
            PseudoClasses.Set(":stimulation", Stage == ExperimentRunStage.Stimulation);
            PseudoClasses.Set(":recovery", Stage == ExperimentRunStage.Recovery);
        }
    }
}
