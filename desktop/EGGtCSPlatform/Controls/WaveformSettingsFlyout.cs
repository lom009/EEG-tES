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

[TemplatePart("PART_ToggleButton", typeof(Button))]
public class WaveformSettingsFlyout : TemplatedControl
{
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<
        WaveformSettingsFlyout,
        bool
    >(nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IEnumerable?> LayoutOptionsProperty =
        AvaloniaProperty.Register<WaveformSettingsFlyout, IEnumerable?>(nameof(LayoutOptions));

    public static readonly StyledProperty<object?> SelectedLayoutProperty =
        AvaloniaProperty.Register<WaveformSettingsFlyout, object?>(
            nameof(SelectedLayout),
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<ICommand?> ShowAllChannelsCommandProperty =
        AvaloniaProperty.Register<WaveformSettingsFlyout, ICommand?>(
            nameof(ShowAllChannelsCommand)
        );

    public static readonly StyledProperty<IEnumerable?> ExceptionsProperty =
        AvaloniaProperty.Register<WaveformSettingsFlyout, IEnumerable?>(nameof(Exceptions));

    private Button? _toggleButton;

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public IEnumerable? LayoutOptions
    {
        get => GetValue(LayoutOptionsProperty);
        set => SetValue(LayoutOptionsProperty, value);
    }

    public object? SelectedLayout
    {
        get => GetValue(SelectedLayoutProperty);
        set => SetValue(SelectedLayoutProperty, value);
    }

    public ICommand? ShowAllChannelsCommand
    {
        get => GetValue(ShowAllChannelsCommandProperty);
        set => SetValue(ShowAllChannelsCommandProperty, value);
    }

    public IEnumerable? Exceptions
    {
        get => GetValue(ExceptionsProperty);
        set => SetValue(ExceptionsProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_toggleButton is not null)
            _toggleButton.Click -= OnToggleButtonClick;
        base.OnApplyTemplate(e);
        _toggleButton = e.NameScope.Find<Button>("PART_ToggleButton");
        if (_toggleButton is not null)
            _toggleButton.Click += OnToggleButtonClick;
    }

    private void OnToggleButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        SetCurrentValue(IsOpenProperty, !IsOpen);
}
