using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Views.Pages;

public partial class EnvelopeStimulationPageView : UserControl
{
    public EnvelopeStimulationPageView()
    {
        InitializeComponent();
        ModeSelector.ContainerPrepared += (_, e) => e.Container.IsEnabled = ModeEnabled(e.Index);
        DataContextChanged += (_, _) => ObserveModel();
    }

    private EnvelopeStimulationPageViewModel? _observedModel;

    private bool ModeEnabled(int index) =>
        index is 0 or 3 or 4
        || index == 1
            && DataContext is EnvelopeStimulationPageViewModel { CanUseAdaptiveMode: true }
        || index == 2
            && DataContext is EnvelopeStimulationPageViewModel { CanUseAdaptiveMode: true };

    private void ObserveModel()
    {
        if (_observedModel is not null)
            _observedModel.PropertyChanged -= OnModelChanged;
        _observedModel = DataContext as EnvelopeStimulationPageViewModel;
        if (_observedModel is not null)
            _observedModel.PropertyChanged += OnModelChanged;
        RefreshModeOptions();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvelopeStimulationPageViewModel.CanUseAdaptiveMode))
            RefreshModeOptions();
    }

    private void RefreshModeOptions()
    {
        for (var i = 0; i < ModeSelector.Items.Count; i++)
            if (ModeSelector.ContainerFromIndex(i) is { } container)
                container.IsEnabled = ModeEnabled(i);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveModel();
    }

    private void OnVoiceWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || DataContext is not EnvelopeStimulationPageViewModel model)
            return;
        model.ZoomVoice(e.Delta.Y);
        e.Handled = true;
    }

    private void OnStimulusWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || DataContext is not EnvelopeStimulationPageViewModel model)
            return;
        model.ZoomStimulus(e.Delta.Y);
        e.Handled = true;
    }

    private void OnAudioHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (AudioVolumeControls is null)
            return;
        var stacked = e.NewSize.Width < 650;
        Grid.SetRow(AudioVolumeControls, stacked ? 2 : 0);
        Grid.SetColumn(AudioVolumeControls, stacked ? 0 : 2);
        Grid.SetColumnSpan(AudioVolumeControls, stacked ? 3 : 1);
        AudioHeader.RowDefinitions[1].Height = new GridLength(stacked ? 8 : 0);
        AudioVolumeControls.Width = System.Math.Min(497, e.NewSize.Width);
        AudioVolumeControls.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observedModel is not null)
            _observedModel.PropertyChanged -= OnModelChanged;
        _observedModel = null;
        if (DataContext is EnvelopeStimulationPageViewModel model)
            model.OnNavigatedFrom();
        base.OnDetachedFromVisualTree(e);
    }
}
