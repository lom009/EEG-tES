using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace EGGtCSPlatform.Controls;

public class StimulusOptionSelector : TemplatedControl
{
    private ListBox? _selector;

    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<
        StimulusOptionSelector,
        string?
    >(nameof(Label));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<StimulusOptionSelector, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty = AvaloniaProperty.Register<
        StimulusOptionSelector,
        object?
    >(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_selector is not null)
            _selector.SelectionChanged -= OnSelectionChanged;

        base.OnApplyTemplate(e);

        _selector = e.NameScope.Find<ListBox>("PART_Selector");
        if (_selector is not null)
            _selector.SelectionChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_selector?.SelectedItem is not { } selectedItem)
            return;

        SetCurrentValue(SelectedItemProperty, selectedItem);
    }
}
