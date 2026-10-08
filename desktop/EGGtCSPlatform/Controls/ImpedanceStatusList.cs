using System;
using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace EGGtCSPlatform.Controls;

public class ImpedanceStatusList : TemplatedControl
{
    public static readonly StyledProperty<ICommand?> PointCommandProperty =
        AvaloniaProperty.Register<ImpedanceStatusList, ICommand?>(nameof(PointCommand));
    public ICommand? PointCommand { get => GetValue(PointCommandProperty); set => SetValue(PointCommandProperty, value); }

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ImpedanceStatusList, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<
        ImpedanceStatusList,
        string?
    >(nameof(Title));

    public static readonly StyledProperty<string?> StatusTextProperty = AvaloniaProperty.Register<
        ImpedanceStatusList,
        string?
    >(nameof(StatusText));

    public static readonly StyledProperty<string?> ButtonTextProperty = AvaloniaProperty.Register<
        ImpedanceStatusList,
        string?
    >(nameof(ButtonText));

    public static readonly StyledProperty<string?> EmptyTitleProperty = AvaloniaProperty.Register<
        ImpedanceStatusList,
        string?
    >(nameof(EmptyTitle));

    public static readonly StyledProperty<string?> EmptyDescriptionProperty =
        AvaloniaProperty.Register<ImpedanceStatusList, string?>(nameof(EmptyDescription));

    public static readonly StyledProperty<string?> ValidationTextProperty =
        AvaloniaProperty.Register<ImpedanceStatusList, string?>(nameof(ValidationText));

    public static readonly StyledProperty<bool> HasItemsProperty = AvaloniaProperty.Register<
        ImpedanceStatusList,
        bool
    >(nameof(HasItems));

    public static readonly StyledProperty<bool> IsPassedProperty = AvaloniaProperty.Register<
        ImpedanceStatusList,
        bool
    >(nameof(IsPassed));

    public static readonly StyledProperty<bool> IsFailedProperty = AvaloniaProperty.Register<
        ImpedanceStatusList,
        bool
    >(nameof(IsFailed));

    public static readonly StyledProperty<bool> HasValidationMessageProperty =
        AvaloniaProperty.Register<ImpedanceStatusList, bool>(nameof(HasValidationMessage));

    public static readonly StyledProperty<bool> IsDetectingProperty = AvaloniaProperty.Register<
        ImpedanceStatusList,
        bool
    >(nameof(IsDetecting));

    public static readonly StyledProperty<bool> IsInteractionLockedProperty =
        AvaloniaProperty.Register<ImpedanceStatusList, bool>(nameof(IsInteractionLocked));

    public static readonly StyledProperty<ICommand?> DetectionCommandProperty =
        AvaloniaProperty.Register<ImpedanceStatusList, ICommand?>(nameof(DetectionCommand));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public string? ButtonText
    {
        get => GetValue(ButtonTextProperty);
        set => SetValue(ButtonTextProperty, value);
    }

    public string? EmptyTitle
    {
        get => GetValue(EmptyTitleProperty);
        set => SetValue(EmptyTitleProperty, value);
    }

    public string? EmptyDescription
    {
        get => GetValue(EmptyDescriptionProperty);
        set => SetValue(EmptyDescriptionProperty, value);
    }

    public string? ValidationText
    {
        get => GetValue(ValidationTextProperty);
        set => SetValue(ValidationTextProperty, value);
    }

    public bool HasItems
    {
        get => GetValue(HasItemsProperty);
        set => SetValue(HasItemsProperty, value);
    }

    public bool IsPassed
    {
        get => GetValue(IsPassedProperty);
        set => SetValue(IsPassedProperty, value);
    }

    public bool IsFailed
    {
        get => GetValue(IsFailedProperty);
        set => SetValue(IsFailedProperty, value);
    }

    public bool HasValidationMessage
    {
        get => GetValue(HasValidationMessageProperty);
        set => SetValue(HasValidationMessageProperty, value);
    }

    public bool IsDetecting
    {
        get => GetValue(IsDetectingProperty);
        set => SetValue(IsDetectingProperty, value);
    }

    public bool IsInteractionLocked
    {
        get => GetValue(IsInteractionLockedProperty);
        set => SetValue(IsInteractionLockedProperty, value);
    }

    public ICommand? DetectionCommand
    {
        get => GetValue(DetectionCommandProperty);
        set => SetValue(DetectionCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPassedProperty || change.Property == IsFailedProperty)
        {
            PseudoClasses.Set(":passed", IsPassed);
            PseudoClasses.Set(":failed", IsFailed);
        }
        if (change.Property == IsDetectingProperty)
            PseudoClasses.Set(":detecting", IsDetecting);
    }
}
