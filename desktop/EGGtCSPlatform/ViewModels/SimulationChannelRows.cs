using System;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.ViewModels;

public static class SimulationLabels
{
    public static readonly FuncValueConverter<object?, string> Converter = new(value =>
        value switch
        {
            StimulusDirection.Positive => "正向",
            StimulusDirection.Negative => "反向",
            StimulusDirection.Bidirectional => "双向",
            ShamWaveformMode.Direct => "直流",
            ShamWaveformMode.Alternating => "交流",
            ExperimentRunMode.Manual => "手动",
            ExperimentRunMode.Automatic => "自动",
            StimulusArrayMode.DualChannel => "双通道",
            StimulusArrayMode.Hd => "HD",
            StimulusArrayMode.MultiTarget => "多靶点",
            StimulationChannelRole.FixedActive => "固定通道",
            StimulationChannelRole.Selectable => "回流通道",
            _ => value?.ToString() ?? "",
        }
    );
}

public partial class SimulationStimulusRow : ObservableObject
{
    [ObservableProperty]
    private int _target = 1;

    [ObservableProperty]
    private string _site = "";

    [ObservableProperty]
    private int _physicalChannel;

    [ObservableProperty]
    private StimulationChannelRole _role;

    [ObservableProperty]
    private double _current;
}

public partial class SimulationAcquisitionRow : ObservableObject
{
    [ObservableProperty]
    private string _site = "";

    [ObservableProperty]
    private int _physicalChannel;
}

public partial class SimulationGenerationTimeRangeRow : ObservableObject
{
    public SimulationGenerationTimeRangeRow(TimeSpan? start, TimeSpan? end)
    {
        _start = start;
        _end = end;
    }

    [ObservableProperty]
    private TimeSpan? _start;

    [ObservableProperty]
    private TimeSpan? _end;
}
