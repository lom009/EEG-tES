using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.ViewModels;

public sealed record SimulationTopology(
    StimulusArrayMode ArrayMode,
    IReadOnlyList<ExperimentPackageTarget> Targets,
    IReadOnlyList<ExperimentPackageAcquisitionChannel> Acquisition,
    string Reference,
    string Ground
);

public sealed record SimulationKindOption(StimulusKind Kind, string Label);

public partial class SimulationGeneratorViewModel : ObservableObject
{
    private readonly SimulationGenerationService _generator;
    private CancellationTokenSource? _cancellation;
    private readonly Dictionary<
        (StimulusKind, ShamWaveformMode),
        (double Frequency, double Ramp, double Duty)
    > _waveformEdits = new();
    public event EventHandler? Generated;

    public SimulationGeneratorViewModel(
        SimulationGenerationService generator,
        IEegPhysicalChannelMappingService mappings,
        StimulusCapabilityProfile capability
    )
    {
        _generator = generator;
        var sites = capability.StimulusSiteIds.OrderBy(x => x).Take(2).ToArray();
        var active = capability.FixedActivePhysicalChannelIds.OrderBy(x => x).FirstOrDefault(1);
        var returns = capability
            .SelectablePhysicalChannelIds.Where(x => x != active)
            .Take(capability.DualChannelSelectableCount)
            .ToArray();
        var current = Math.Min(2, capability.MaximumTotalPeakCurrent);
        var acquisition = mappings
            .Load()
            .Mappings.Where(x =>
                x.PhysicalChannel.HasValue && x.ElectrodeId != "AFz" && x.ElectrodeId != "FCz"
            )
            .Take(6)
            .Select(x => new ExperimentPackageAcquisitionChannel(x.ElectrodeId, x.PhysicalChannel))
            .ToArray();
        if (acquisition.Length == 0)
            acquisition = [new("C3", 1), new("C4", 2)];
        var channels = new List<ExperimentPackageChannel>
        {
            new(sites.First(), active, StimulationChannelRole.FixedActive, current),
        };
        channels.AddRange(
            returns.Select(
                (p, i) =>
                    new ExperimentPackageChannel(
                        sites.Last(),
                        p,
                        StimulationChannelRole.Selectable,
                        current / returns.Length
                    )
            )
        );
        foreach (var channel in channels)
            StimulusChannels.Add(
                new SimulationStimulusRow
                {
                    Target = 1,
                    Site = channel.SiteId,
                    PhysicalChannel = channel.PhysicalChannelId,
                    Role = channel.Role,
                    Current = channel.CurrentMilliAmps,
                }
            );
        foreach (var channel in acquisition)
            AcquisitionChannels.Add(
                new SimulationAcquisitionRow
                {
                    Site = channel.SiteId,
                    PhysicalChannel = channel.PhysicalChannelId ?? 1,
                }
            );
    }

    public IReadOnlyList<SimulationKindOption> Kinds { get; } =
    [
        new(StimulusKind.TDcs, "tDCS"),
        new(StimulusKind.TAcs, "tACS"),
        new(StimulusKind.TRns, "tRNS"),
        new(StimulusKind.TPcs, "tPCS"),
        new(StimulusKind.Sham, "假刺激"),
        new(StimulusKind.EnvelopeTAcs, "包络-tACS"),
    ];
    public IReadOnlyList<StimulusDirection> Directions { get; } =
        Enum.GetValues<StimulusDirection>();
    public IReadOnlyList<ShamWaveformMode> ShamModes { get; } = Enum.GetValues<ShamWaveformMode>();
    public IReadOnlyList<ExperimentRunMode> Modes { get; } = Enum.GetValues<ExperimentRunMode>();
    public IReadOnlyList<int> Rates { get; } = [250, 500];
    public ObservableCollection<string> Results { get; } = [];
    public ObservableCollection<SimulationStimulusRow> StimulusChannels { get; } = [];
    public ObservableCollection<SimulationAcquisitionRow> AcquisitionChannels { get; } = [];
    public ObservableCollection<SimulationGenerationTimeRangeRow> GenerationTimeRanges { get; } =
    [new(new TimeSpan(9, 30, 0), new TimeSpan(18, 30, 0))];
    public IReadOnlyList<StimulusArrayMode> ArrayModes { get; } =
        Enum.GetValues<StimulusArrayMode>();
    public IReadOnlyList<StimulationChannelRole> ChannelRoles { get; } =
        Enum.GetValues<StimulationChannelRole>();

    [ObservableProperty]
    private StimulusArrayMode _arrayMode = StimulusArrayMode.DualChannel;

    [ObservableProperty]
    private string _reference = "FCz";

    [ObservableProperty]
    private string _ground = "AFz";

    [ObservableProperty]
    private SimulationStimulusRow? _selectedStimulusChannel;

    [ObservableProperty]
    private SimulationAcquisitionRow? _selectedAcquisitionChannel;

    [RelayCommand]
    private void AddStimulusChannel() =>
        StimulusChannels.Add(
            new()
            {
                Target = 1,
                Site = "",
                PhysicalChannel =
                    StimulusChannels.Select(x => x.PhysicalChannel).DefaultIfEmpty(0).Max() + 1,
                Role = StimulationChannelRole.Selectable,
                Current = 2,
            }
        );

    [RelayCommand]
    private void RemoveStimulusChannel()
    {
        if (SelectedStimulusChannel is { } row)
            StimulusChannels.Remove(row);
    }

    [RelayCommand]
    private void AddAcquisitionChannel() =>
        AcquisitionChannels.Add(
            new()
            {
                Site = "",
                PhysicalChannel =
                    AcquisitionChannels.Select(x => x.PhysicalChannel).DefaultIfEmpty(0).Max() + 1,
            }
        );

    [RelayCommand]
    private void RemoveAcquisitionChannel()
    {
        if (SelectedAcquisitionChannel is { } row)
            AcquisitionChannels.Remove(row);
    }

    [RelayCommand]
    private void AddGenerationTimeRange()
    {
        GenerationTimeRanges.Add(new(new TimeSpan(9, 30, 0), new TimeSpan(18, 30, 0)));
        RemoveGenerationTimeRangeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveGenerationTimeRange))]
    private void RemoveGenerationTimeRange(SimulationGenerationTimeRangeRow? row)
    {
        if (row is not null && GenerationTimeRanges.Count > 1 && GenerationTimeRanges.Remove(row))
            RemoveGenerationTimeRangeCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveGenerationTimeRange(SimulationGenerationTimeRangeRow? row) =>
        row is not null && GenerationTimeRanges.Count > 1;

    [ObservableProperty]
    private SimulationKindOption _selectedKind = new(StimulusKind.TAcs, "tACS");

    [ObservableProperty]
    private StimulusDirection _direction = StimulusDirection.Bidirectional;

    [ObservableProperty]
    private ShamWaveformMode _shamMode = ShamWaveformMode.Alternating;

    [ObservableProperty]
    private ExperimentRunMode _mode = ExperimentRunMode.Automatic;

    [ObservableProperty]
    private bool _randomized32;

    [ObservableProperty]
    private int _count = 1;

    [ObservableProperty]
    private string _subjectPrefix = "SUB";

    [ObservableProperty]
    private string _startedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");

    [ObservableProperty]
    private bool _allowMonday = true;

    [ObservableProperty]
    private bool _allowTuesday = true;

    [ObservableProperty]
    private bool _allowWednesday = true;

    [ObservableProperty]
    private bool _allowThursday = true;

    [ObservableProperty]
    private bool _allowFriday = true;

    [ObservableProperty]
    private bool _allowSaturday;

    [ObservableProperty]
    private bool _allowSunday;

    [ObservableProperty]
    private double _intervalMinutes = 60;

    [ObservableProperty]
    private bool _useRandomInterval;

    [ObservableProperty]
    private double _minimumIntervalMinutes = 30;

    [ObservableProperty]
    private double _maximumIntervalMinutes = 90;

    [ObservableProperty]
    private int _seed = 20260910;

    [ObservableProperty]
    private double _frequency = 40;

    [ObservableProperty]
    private double _rampSeconds = 7;

    [ObservableProperty]
    private double _dutyPercent = 50;

    [ObservableProperty]
    private double _modulationHz = 4;

    [ObservableProperty]
    private double _depth = 0.8;

    [ObservableProperty]
    private double _delayMilliseconds;

    [ObservableProperty]
    private bool _envelopeSham;

    [ObservableProperty]
    private int _sampleRate = 500;

    [ObservableProperty]
    private double _acquisitionSeconds = 10;

    [ObservableProperty]
    private double _blankingSeconds = 1;

    [ObservableProperty]
    private double _stimulationSeconds = 15;

    [ObservableProperty]
    private double _recoverySeconds = 1;

    [ObservableProperty]
    private int _cycles = 1;

    [ObservableProperty]
    private double _amplitude = 25;

    [ObservableProperty]
    private double _noise = 5;

    [ObservableProperty]
    private double _rhythmHz = 10;

    [ObservableProperty]
    private string _status = "配置完成后可先校验，再保存到历史记录。";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isGenerating;
    public bool IsEditable => !IsGenerating;
    public bool IsEnvelope => SelectedKind.Kind == StimulusKind.EnvelopeTAcs;
    public bool ShowShamMode => SelectedKind.Kind == StimulusKind.Sham;
    public StimulusParameterPolicy ParameterPolicy =>
        StimulusParameterPolicy.For(SelectedKind.Kind, ShamMode);
    public StimulusParameterRange DutyRange => StimulusParameterPolicy.Duty;
    public bool ShowWaveformParameters =>
        ParameterPolicy.ShowFrequency
        || ParameterPolicy.ShowRamp
        || ParameterPolicy.ShowDutyPercent;
    public int RampColumn => ParameterPolicy.ShowFrequency ? 1 : 0;
    public string FrequencyLabel => IsEnvelope ? "载波频率 / Hz" : "频率 / Hz";
    public string RampLabel => IsEnvelope ? "渐入渐出 / 秒" : "缓升缓降 / 秒";

    private static (StimulusKind, ShamWaveformMode) EditKey(
        StimulusKind kind,
        ShamWaveformMode mode
    ) => (kind, kind == StimulusKind.Sham ? mode : ShamWaveformMode.Direct);

    partial void OnSelectedKindChanged(
        SimulationKindOption? oldValue,
        SimulationKindOption newValue
    ) =>
        RestoreWaveformEdits(
            EditKey(oldValue?.Kind ?? StimulusKind.TAcs, ShamMode),
            EditKey(newValue.Kind, ShamMode)
        );

    partial void OnShamModeChanged(ShamWaveformMode oldValue, ShamWaveformMode newValue)
    {
        if (ShowShamMode)
            RestoreWaveformEdits(
                EditKey(SelectedKind.Kind, oldValue),
                EditKey(SelectedKind.Kind, newValue)
            );
    }

    private void RestoreWaveformEdits(
        (StimulusKind, ShamWaveformMode) previous,
        (StimulusKind, ShamWaveformMode) next
    )
    {
        _waveformEdits[previous] = (Frequency, RampSeconds, DutyPercent);
        var values = _waveformEdits.GetValueOrDefault(next, (40d, 7d, 50d));
        OnPropertyChanged(nameof(IsEnvelope));
        OnPropertyChanged(nameof(ShowShamMode));
        OnPropertyChanged(nameof(ParameterPolicy));
        OnPropertyChanged(nameof(ShowWaveformParameters));
        OnPropertyChanged(nameof(RampColumn));
        OnPropertyChanged(nameof(FrequencyLabel));
        OnPropertyChanged(nameof(RampLabel));
        Frequency = values.Item1;
        RampSeconds = values.Item2;
        DutyPercent = values.Item3;
    }

    partial void OnIsGeneratingChanged(bool value) => OnPropertyChanged(nameof(IsEditable));

    [RelayCommand]
    private void ApplyRandomizedPreset()
    {
        SelectedKind = Kinds.Last();
        Randomized32 = true;
        Count = 32;
        EnvelopeSham = false;
        Mode = ExperimentRunMode.Automatic;
        Status = "32名被试 · 16例包络刺激 / 16例假刺激 · 每人1次 · 分配由随机种子确定";
    }

    public SimulationRequest CreateRequest()
    {
        if (StimulusChannels.Any(x => x.Target < 1))
            throw new ArgumentException("靶点编号必须大于零。");
        if (
            StimulusChannels
                .GroupBy(x => x.Target)
                .Any(g => g.Count(x => x.Role == StimulationChannelRole.FixedActive) != 1)
        )
            throw new ArgumentException("每个靶点必须有且仅有一个 FixedActive 固定通道。");
        var topology = new SimulationTopology(
            ArrayMode,
            StimulusChannels
                .GroupBy(x => x.Target)
                .Select(group => new ExperimentPackageTarget(
                    group.Key,
                    group.Key,
                    group.Single(x => x.Role == StimulationChannelRole.FixedActive).Current,
                    group
                        .Select(x => new ExperimentPackageChannel(
                            x.Site.Trim(),
                            x.PhysicalChannel,
                            x.Role,
                            x.Current
                        ))
                        .ToArray()
                ))
                .ToArray(),
            AcquisitionChannels
                .Select(x => new ExperimentPackageAcquisitionChannel(
                    x.Site.Trim(),
                    x.PhysicalChannel
                ))
                .ToArray(),
            Reference.Trim(),
            Ground.Trim()
        );
        var ids = topology.Targets.ToDictionary(x => x.TargetKey, _ => Guid.NewGuid());
        var targets = topology
            .Targets.Select(t => new StimulusTargetSnapshot(
                ids[t.TargetKey],
                t.DisplayOrder,
                t.PeakCurrentMilliAmps,
                t.Channels.Single(x =>
                    x.Role == StimulationChannelRole.FixedActive
                ).PhysicalChannelId,
                t.Channels.Select(c => new StimulationPhysicalChannelSnapshot(
                        c.PhysicalChannelId,
                        c.Role,
                        c.CurrentMilliAmps
                    ))
                    .ToArray()
            ))
            .ToArray();
        var stimulus = new StimulusConfigurationSnapshot(
            SelectedKind.Kind,
            topology.ArrayMode,
            Direction,
            ShamMode,
            RampSeconds,
            Frequency,
            DutyPercent,
            targets,
            IsEnvelope
                ? new EnvelopeParameters(ModulationHz, Depth, DelayMilliseconds, EnvelopeSham)
                : null
        );
        var template = new ExperimentConfigurationTemplate(
            stimulus,
            topology
                .Targets.SelectMany(t =>
                    t.Channels.Select(c => new StimulusElectrodeAssignment(
                        c.SiteId,
                        ids[t.TargetKey],
                        c.PhysicalChannelId,
                        c.Role
                    ))
                )
                .ToArray(),
            topology.Acquisition.Select(x => x.SiteId).ToArray(),
            topology.Reference,
            topology.Ground,
            SampleRate,
            new ExperimentTimingTemplate(
                Mode,
                AcquisitionSeconds * 1000,
                BlankingSeconds * 1000,
                StimulationSeconds * 1000,
                RecoverySeconds * 1000,
                Mode == ExperimentRunMode.Manual ? 1 : Cycles
            ),
            "数据生成器"
        );
        if (
            !DateTimeOffset.TryParse(
                StartedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start
            )
        )
            throw new ArgumentException("开始时间格式示例：2026-09-10 10:00:00 +08:00。");
        return new SimulationRequest(
            template,
            Count,
            SubjectPrefix,
            start,
            IntervalMinutes,
            Seed,
            Randomized32,
            Amplitude,
            Noise,
            RhythmHz,
            topology
                .Acquisition.Select(x => new EegPhysicalChannelMapping(
                    x.SiteId,
                    x.PhysicalChannelId
                ))
                .ToArray(),
            UseRandomInterval
                ? new RecordIntervalRange(MinimumIntervalMinutes, MaximumIntervalMinutes)
                : null,
            CreateGenerationSchedule()
        );
    }

    private DailyGenerationSchedule CreateGenerationSchedule()
    {
        if (GenerationTimeRanges.Count == 0)
            throw new ArgumentException("请至少配置一个每日允许时段。");
        var ranges = GenerationTimeRanges
            .Select(
                (row, index) =>
                {
                    if (
                        row.Start is not { } start
                        || row.End is not { } end
                        || start < TimeSpan.Zero
                        || start >= TimeSpan.FromDays(1)
                        || end < TimeSpan.Zero
                        || end >= TimeSpan.FromDays(1)
                    )
                        throw new ArgumentException($"请选择有效的第 {index + 1} 个允许时段。");
                    return new DailyGenerationTimeRange(
                        TimeOnly.FromTimeSpan(start),
                        TimeOnly.FromTimeSpan(end)
                    );
                }
            )
            .ToArray();
        var weekdays = GenerationWeekdays.None;
        if (AllowMonday)
            weekdays |= GenerationWeekdays.Monday;
        if (AllowTuesday)
            weekdays |= GenerationWeekdays.Tuesday;
        if (AllowWednesday)
            weekdays |= GenerationWeekdays.Wednesday;
        if (AllowThursday)
            weekdays |= GenerationWeekdays.Thursday;
        if (AllowFriday)
            weekdays |= GenerationWeekdays.Friday;
        if (AllowSaturday)
            weekdays |= GenerationWeekdays.Saturday;
        if (AllowSunday)
            weekdays |= GenerationWeekdays.Sunday;
        return new DailyGenerationSchedule(ranges, weekdays);
    }

    [RelayCommand]
    private void ValidateConfiguration()
    {
        try
        {
            var request = CreateRequest();
            _generator.Validate(request);
            var stages = SimulationGenerationService.BuildStages(request.Template.Timing);
            var starts = SimulationGenerationService.BuildStartTimes(request);
            var samples =
                stages
                    .Where(x => x.Stage == ExperimentRunStage.Acquisition)
                    .Sum(x => x.EndSeconds - x.StartSeconds)
                * SampleRate
                * request.Template.AcquisitionChannels.Count
                * Count;
            var adjustment =
                starts[0] == request.StartedAt
                    ? string.Empty
                    : $" · 首条已从 {request.StartedAt:yyyy-MM-dd HH:mm zzz} 顺延至 {starts[0]:yyyy-MM-dd HH:mm zzz}";
            Status =
                $"校验通过 · {Count}例 · 每例 {stages.Last().EndSeconds:0.###} 秒"
                + $" · 共 {samples:N0} 个采样值（不含刺激间隔）"
                + $" · 实际开始 {starts[0]:yyyy-MM-dd HH:mm zzz} 至 {starts[^1]:yyyy-MM-dd HH:mm zzz}"
                + adjustment;
        }
        catch (Exception e)
        {
            Status = $"配置无效：{e.Message}";
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsGenerating)
            return;
        try
        {
            var request = CreateRequest();
            _generator.Validate(request);
            _cancellation = new CancellationTokenSource();
            IsGenerating = true;
            Results.Clear();
            Progress = 0;
            var progress = new Progress<SimulationProgress>(p =>
            {
                Progress = 100d * p.Completed / p.Total;
                Status = p.Message;
                if (p.Message.StartsWith("已完成", StringComparison.Ordinal))
                    Results.Add(p.Message);
            });
            var result = await Task.Run(() =>
                _generator.GenerateAsync(request, progress, _cancellation.Token)
            );
            Progress = 100d * result.RunIds.Count / request.Count;
            Status =
                $"{(result.Canceled ? "已取消" : result.Error is not null ? "生成失败" : "生成完成")} · 已保存 {result.RunIds.Count}/{request.Count} 例"
                + (result.Error is null ? "" : $" · {result.Error}");
            Results.Add($"批次：{result.BatchId} · 种子：{request.Seed}");
            Generated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            Status = $"生成失败：{e.Message}";
        }
        finally
        {
            IsGenerating = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand]
    public void Cancel() => _cancellation?.Cancel();
}
