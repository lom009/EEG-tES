using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public class ElectrodeHeadMap : TemplatedControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<ElectrodeHeadMap, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<ICommand?> PointCommandProperty =
        AvaloniaProperty.Register<ElectrodeHeadMap, ICommand?>(nameof(PointCommand));

    public static readonly StyledProperty<ICommand?> PointDoubleClickCommandProperty =
        AvaloniaProperty.Register<ElectrodeHeadMap, ICommand?>(nameof(PointDoubleClickCommand));

    public static readonly StyledProperty<ElectrodeHeadOrientation> OrientationProperty =
        AvaloniaProperty.Register<ElectrodeHeadMap, ElectrodeHeadOrientation>(nameof(Orientation));

    public static readonly StyledProperty<bool> IsBackViewProperty = AvaloniaProperty.Register<
        ElectrodeHeadMap,
        bool
    >(nameof(IsBackView));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? PointCommand
    {
        get => GetValue(PointCommandProperty);
        set => SetValue(PointCommandProperty, value);
    }

    public ICommand? PointDoubleClickCommand
    {
        get => GetValue(PointDoubleClickCommandProperty);
        set => SetValue(PointDoubleClickCommandProperty, value);
    }

    public ElectrodeHeadOrientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public bool IsBackView => GetValue(IsBackViewProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != OrientationProperty)
            return;

        var isBackView = Orientation == ElectrodeHeadOrientation.Back;
        SetValue(IsBackViewProperty, isBackView);
        PseudoClasses.Set(":back", isBackView);
    }
}
