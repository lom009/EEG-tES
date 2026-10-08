using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public sealed class ExperimentRunActions : TemplatedControl
{
    public static readonly StyledProperty<bool> IsCompletionProperty = AvaloniaProperty.Register<
        ExperimentRunActions,
        bool
    >(nameof(IsCompletion));
    public bool IsCompletion
    {
        get => GetValue(IsCompletionProperty);
        set => SetValue(IsCompletionProperty, value);
    }

    public static readonly StyledProperty<bool> IsEmergencyOnlyProperty =
        AvaloniaProperty.Register<ExperimentRunActions, bool>(nameof(IsEmergencyOnly));
    public bool IsEmergencyOnly
    {
        get => GetValue(IsEmergencyOnlyProperty);
        set => SetValue(IsEmergencyOnlyProperty, value);
    }

    public static readonly StyledProperty<ICommand?> CompletionCommandProperty =
        AvaloniaProperty.Register<ExperimentRunActions, ICommand?>(nameof(CompletionCommand));
    public ICommand? CompletionCommand
    {
        get => GetValue(CompletionCommandProperty);
        set => SetValue(CompletionCommandProperty, value);
    }

    public static readonly StyledProperty<string> CompletionTextProperty =
        AvaloniaProperty.Register<ExperimentRunActions, string>(nameof(CompletionText), "结束实验");
    public string CompletionText
    {
        get => GetValue(CompletionTextProperty);
        set => SetValue(CompletionTextProperty, value);
    }

    public static readonly StyledProperty<bool> IsCompletionEnabledProperty =
        AvaloniaProperty.Register<ExperimentRunActions, bool>(nameof(IsCompletionEnabled), true);
    public bool IsCompletionEnabled
    {
        get => GetValue(IsCompletionEnabledProperty);
        set => SetValue(IsCompletionEnabledProperty, value);
    }

    public static readonly StyledProperty<ICommand?> EmergencyCommandProperty =
        AvaloniaProperty.Register<ExperimentRunActions, ICommand?>(nameof(EmergencyCommand));
    public ICommand? EmergencyCommand
    {
        get => GetValue(EmergencyCommandProperty);
        set => SetValue(EmergencyCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> PrimaryCommandProperty =
        AvaloniaProperty.Register<ExperimentRunActions, ICommand?>(nameof(PrimaryCommand));
    public ICommand? PrimaryCommand
    {
        get => GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
    }

    public static readonly StyledProperty<string> PrimaryTextProperty = AvaloniaProperty.Register<
        ExperimentRunActions,
        string
    >(nameof(PrimaryText), "开始实验");
    public string PrimaryText
    {
        get => GetValue(PrimaryTextProperty);
        set => SetValue(PrimaryTextProperty, value);
    }

    public static readonly StyledProperty<bool> IsEmergencyEnabledProperty =
        AvaloniaProperty.Register<ExperimentRunActions, bool>(nameof(IsEmergencyEnabled), false);
    public bool IsEmergencyEnabled
    {
        get => GetValue(IsEmergencyEnabledProperty);
        set => SetValue(IsEmergencyEnabledProperty, value);
    }

    public static readonly StyledProperty<bool> IsPrimaryEnabledProperty =
        AvaloniaProperty.Register<ExperimentRunActions, bool>(nameof(IsPrimaryEnabled), true);
    public bool IsPrimaryEnabled
    {
        get => GetValue(IsPrimaryEnabledProperty);
        set => SetValue(IsPrimaryEnabledProperty, value);
    }

    public static readonly StyledProperty<bool> ShowStartIconProperty = AvaloniaProperty.Register<
        ExperimentRunActions,
        bool
    >(nameof(ShowStartIcon), false);
    public bool ShowStartIcon
    {
        get => GetValue(ShowStartIconProperty);
        set => SetValue(ShowStartIconProperty, value);
    }

    public static readonly StyledProperty<double> ButtonHeightProperty = AvaloniaProperty.Register<
        ExperimentRunActions,
        double
    >(nameof(ButtonHeight), 44d);
    public double ButtonHeight
    {
        get => GetValue(ButtonHeightProperty);
        set => SetValue(ButtonHeightProperty, value);
    }
}
