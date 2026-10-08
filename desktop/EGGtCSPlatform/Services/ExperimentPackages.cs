using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed record ExperimentPackageV1(
    string Format,
    int Version,
    DateTimeOffset ExportedAtUtc,
    string ApplicationVersion,
    ExperimentPackageStimulus Stimulus,
    ExperimentPackageElectrodes Electrodes,
    ExperimentPackageTiming Timing,
    [property:
        System.Text.Json.Serialization.JsonPropertyName("generationParameters"),
        System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        )
    ]
        SimulationProvenance? Simulation = null,
    ExperimentCreationMode CreationMode = ExperimentCreationMode.AcquisitionAndStimulation
)
{
    public const string ExpectedFormat = "EGGtCSPlatform.ExperimentPackage";
    public const int CurrentVersion = 1;

    [JsonPropertyName("simulation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SimulationProvenance? LegacyParameters
    {
        get => null;
        init => Simulation ??= value;
    }
}

public sealed record ExperimentPackageStimulus(
    StimulusKind Kind,
    StimulusArrayMode ArrayMode,
    StimulusDirection Direction,
    ShamWaveformMode ShamMode,
    double RampSeconds,
    double FrequencyHz,
    double DutyPercent,
    IReadOnlyList<ExperimentPackageTarget> Targets
);

public sealed record ExperimentPackageTarget(
    int TargetKey,
    int DisplayOrder,
    double PeakCurrentMilliAmps,
    IReadOnlyList<ExperimentPackageChannel> Channels
);

public sealed record ExperimentPackageChannel(
    string SiteId,
    int PhysicalChannelId,
    StimulationChannelRole Role,
    double CurrentMilliAmps
);

public sealed record ExperimentPackageElectrodes(
    IReadOnlyList<ExperimentPackageAcquisitionChannel> Acquisition,
    string ReferenceSiteId,
    string GroundSiteId,
    int SampleRateHz
);

public sealed record ExperimentPackageAcquisitionChannel(string SiteId, int? PhysicalChannelId);

public sealed record ExperimentPackageTiming(
    ExperimentRunMode Mode,
    double AcquisitionMilliseconds,
    double BlankingMilliseconds,
    double StimulationMilliseconds,
    double RecoveryMilliseconds,
    int CycleCount
);

public interface IExperimentPackageSerializer
{
    Task<ExperimentConfigurationTemplate> ReadAsync(
        string path,
        CancellationToken cancellationToken = default
    );
    Task WriteAsync(
        string path,
        ExperimentConfigurationTemplate template,
        CancellationToken cancellationToken = default
    );
}

public interface IExperimentPackageValidator
{
    ExperimentConfigurationTemplate Validate(ExperimentPackageV1 package, string sourceDisplayName);
}

public sealed class ExperimentPackageValidationException(string message) : Exception(message);

public sealed class ExperimentPackageSerializer(
    StimulusCapabilityProfile capability,
    IElectrodePositionCatalog electrodeCatalog,
    IEegPhysicalChannelMappingService channelMappingService,
    IApplicationVersionProvider applicationVersion
) : IExperimentPackageSerializer, IExperimentPackageValidator
{
    public const long MaximumPackageBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<ExperimentConfigurationTemplate> ReadAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        if (!string.Equals(Path.GetExtension(path), ".expp", StringComparison.OrdinalIgnoreCase))
            throw new ExperimentPackageValidationException("请选择 .expp 实验配置文件。");
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("实验配置文件不存在。", path);
        if (info.Length > MaximumPackageBytes)
            throw new ExperimentPackageValidationException("实验配置文件超过 1 MiB 限制。");

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        ExperimentPackageV1 package;
        try
        {
            package =
                await JsonSerializer.DeserializeAsync<ExperimentPackageV1>(
                    stream,
                    JsonOptions,
                    cancellationToken
                ) ?? throw new ExperimentPackageValidationException("实验配置文件内容为空。");
        }
        catch (JsonException exception)
        {
            throw new ExperimentPackageValidationException(
                $"实验配置 JSON 无效：{exception.Message}"
            );
        }
        return Validate(package, Path.GetFileName(path));
    }

    public async Task WriteAsync(
        string path,
        ExperimentConfigurationTemplate template,
        CancellationToken cancellationToken = default
    )
    {
        template = AcquisitionOnlyConfiguration.Normalize(template);
        if (
            template.StimulusConfiguration.Kind == StimulusKind.EnvelopeTAcs
            || template.StimulusConfiguration.Envelope is not null
        )
            throw new ExperimentPackageValidationException(
                "包络-tACS 不能导出为设备实验配置；可导出 EEG 数据。"
            );
        if (StimulusParameterPolicy.ValidateExecutableTemplate(template) is { } error)
            throw new ExperimentPackageValidationException(error);
        var package = CreatePackage(template);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough
        );
        await JsonSerializer.SerializeAsync(stream, package, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public ExperimentConfigurationTemplate Validate(
        ExperimentPackageV1 package,
        string sourceDisplayName
    ) => ValidateCore(package, sourceDisplayName, false);

    private ExperimentConfigurationTemplate ValidateCore(
        ExperimentPackageV1 package,
        string sourceDisplayName,
        bool simulation
    )
    {
        var acquisitionOnly = package.CreationMode == ExperimentCreationMode.AcquisitionOnly;
        if (acquisitionOnly)
            package = package with
            {
                Stimulus = new ExperimentPackageStimulus(
                    StimulusKind.TDcs,
                    StimulusArrayMode.DualChannel,
                    StimulusDirection.Positive,
                    ShamWaveformMode.Direct,
                    0,
                    0,
                    0,
                    []
                ),
                Timing = package.Timing with
                {
                    StimulationMilliseconds = 0,
                    RecoveryMilliseconds = 0,
                },
            };
        var stimulusOnly = package.CreationMode == ExperimentCreationMode.StimulusOnly;
        if (!Enum.IsDefined(package.CreationMode))
            throw new ExperimentPackageValidationException("实验模式无效。");
        if (
            stimulusOnly
            && (
                package.Electrodes.Acquisition.Count != 0
                || !string.IsNullOrEmpty(package.Electrodes.ReferenceSiteId)
                || !string.IsNullOrEmpty(package.Electrodes.GroundSiteId)
                || package.Timing.AcquisitionMilliseconds != 0
                || package.Timing.BlankingMilliseconds != 0
                || package.Timing.RecoveryMilliseconds != 0
                || package.Timing.CycleCount != 1
                || package.Timing.Mode != ExperimentRunMode.Manual
            )
        )
            throw new ExperimentPackageValidationException("单刺激配置只能包含一个刺激阶段。");
        if (!simulation && package.Stimulus.Kind == StimulusKind.EnvelopeTAcs)
            throw new ExperimentPackageValidationException("包络-tACS 仅支持数据生成和回放。");
        if (package.Format != ExperimentPackageV1.ExpectedFormat)
            throw new ExperimentPackageValidationException("文件不是 EGGtCSPlatform 实验配置包。");
        if (package.Version != ExperimentPackageV1.CurrentVersion)
            throw new ExperimentPackageValidationException(
                $"不支持配置包版本 {package.Version}，当前仅支持版本 1。"
            );
        if (
            stimulusOnly
            && (
                package.Timing.StimulationMilliseconds < 1000
                || package.Timing.StimulationMilliseconds > 65535000
                || package.Timing.StimulationMilliseconds % 1000 != 0
            )
        )
            throw new ExperimentPackageValidationException("单刺激时长必须为 1～65535 的整数秒。");
        if (!acquisitionOnly && package.Stimulus.Targets.Count == 0)
            throw new ExperimentPackageValidationException("配置包没有刺激靶点。");
        if (package.Electrodes.SampleRateHz is not (250 or 500))
            throw new ExperimentPackageValidationException("采样率必须为 250 或 500 Hz。");
        if (acquisitionOnly && package.Electrodes.Acquisition.Count == 0)
            throw new ExperimentPackageValidationException("请至少配置一个采集通道。");
        if (
            (
                !stimulusOnly
                && (
                    package.Timing.AcquisitionMilliseconds <= 0
                    || package.Timing.BlankingMilliseconds <= 0
                    || (!acquisitionOnly && package.Timing.RecoveryMilliseconds <= 0)
                )
            )
            || (!acquisitionOnly && package.Timing.StimulationMilliseconds <= 0)
            || !Enum.IsDefined(package.Timing.Mode)
            || package.Timing.CycleCount is < 0 or > 200
        )
            throw new ExperimentPackageValidationException("运行时序参数无效。");
        if (
            new[]
            {
                package.Timing.AcquisitionMilliseconds,
                package.Timing.BlankingMilliseconds,
                package.Timing.StimulationMilliseconds,
                package.Timing.RecoveryMilliseconds,
            }.Any(x => !double.IsFinite(x) || x > TimeSpan.FromHours(24).TotalMilliseconds)
        )
            throw new ExperimentPackageValidationException("单阶段时长不能超过 24 小时。");
        var stimulus = package.Stimulus;
        var parameterError = acquisitionOnly
            ? null
            : StimulusParameterPolicy.Validate(
                stimulus.Kind,
                stimulus.ShamMode,
                stimulus.FrequencyHz,
                stimulus.RampSeconds,
                stimulus.DutyPercent,
                !simulation
            );
        if (parameterError is not null)
            throw new ExperimentPackageValidationException(parameterError);
        var durationError = acquisitionOnly
            ? null
            : StimulusParameterPolicy
                .For(stimulus.Kind, stimulus.ShamMode)
                .ValidateDuration(
                    package.Timing.StimulationMilliseconds / 1000,
                    stimulus.FrequencyHz,
                    stimulus.RampSeconds
                );
        if (durationError is not null)
            throw new ExperimentPackageValidationException(durationError);
        if (
            package.Stimulus.Targets.Select(x => x.TargetKey).Distinct().Count()
                != package.Stimulus.Targets.Count
            || package.Stimulus.Targets.Select(x => x.DisplayOrder).Distinct().Count()
                != package.Stimulus.Targets.Count
        )
            throw new ExperimentPackageValidationException("靶点键或显示顺序存在重复。");
        if (!capability.MultiTargetEnabled && package.Stimulus.Targets.Count > 1)
            throw new ExperimentPackageValidationException("当前设备不支持多靶点刺激。");

        var knownSites = electrodeCatalog
            .Positions.Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allSites = package
            .Stimulus.Targets.SelectMany(x => x.Channels)
            .Select(x => x.SiteId)
            .Concat(package.Electrodes.Acquisition.Select(x => x.SiteId))
            .Concat(
                stimulusOnly
                    ? Array.Empty<string>()
                    : new[] { package.Electrodes.ReferenceSiteId, package.Electrodes.GroundSiteId }
            )
            .ToArray();
        var missing = allSites
            .Where(x => !knownSites.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
            throw new ExperimentPackageValidationException(
                $"当前软件缺少点位：{string.Join("、", missing)}。"
            );
        var stimulusSites = package
            .Stimulus.Targets.SelectMany(x => x.Channels)
            .Select(x => x.SiteId)
            .ToArray();
        if (stimulusSites.Any(x => !capability.CanStimulate(x)))
            throw new ExperimentPackageValidationException("配置包包含当前设备不允许刺激的点位。");
        if (allSites.Any(string.IsNullOrWhiteSpace))
            throw new ExperimentPackageValidationException("点位名称不能为空。");
        if (
            package
                .Stimulus.Targets.SelectMany(x => x.Channels)
                .GroupBy(x => x.PhysicalChannelId)
                .Any(x => x.Count() > 1)
        )
            throw new ExperimentPackageValidationException("刺激物理通道存在重复分配。");
        if (
            package
                .Stimulus.Targets.SelectMany(x => x.Channels)
                .Any(x =>
                    x.PhysicalChannelId < 1
                    || x.PhysicalChannelId > capability.StimulationPhysicalChannelCount
                )
        )
            throw new ExperimentPackageValidationException("刺激物理通道超出当前设备能力。");
        if (
            package.Stimulus.Targets.Sum(x => x.PeakCurrentMilliAmps)
                > capability.MaximumTotalPeakCurrent + 0.0001
            || package.Stimulus.Targets.Any(x =>
                !capability.CurrentPolicy.IsValidMilliAmps(x.PeakCurrentMilliAmps)
            )
        )
            throw new ExperimentPackageValidationException(
                "刺激峰值电流超出当前设备能力或不符合电流步进。"
            );

        foreach (var target in package.Stimulus.Targets)
        {
            var selectable = target.Channels.Count(x =>
                x.Role == StimulationChannelRole.Selectable
            );
            var expected =
                package.Stimulus.ArrayMode == StimulusArrayMode.Hd
                    ? capability.HdSelectableChannelCount
                    : capability.DualChannelSelectableCount;
            if (selectable != expected || target.Channels.Count != expected + 1)
                throw new ExperimentPackageValidationException(
                    $"靶点 {target.DisplayOrder} 的刺激通道数量与阵列模式不匹配。"
                );
            if (
                target.Channels.Any(x =>
                    !double.IsFinite(x.CurrentMilliAmps)
                    || x.CurrentMilliAmps <= 0
                    || x.CurrentMilliAmps > capability.CurrentPolicy.MaximumMilliAmps + 0.0001
                )
            )
                throw new ExperimentPackageValidationException(
                    $"靶点 {target.DisplayOrder} 的通道电流无效。"
                );
            var fixedCurrent =
                target
                    .Channels.SingleOrDefault(x => x.Role == StimulationChannelRole.FixedActive)
                    ?.CurrentMilliAmps
                ?? 0;
            var selectableCurrent = target
                .Channels.Where(x => x.Role == StimulationChannelRole.Selectable)
                .Sum(x => x.CurrentMilliAmps);
            if (Math.Abs(fixedCurrent - selectableCurrent) > 0.0001)
                throw new ExperimentPackageValidationException(
                    $"靶点 {target.DisplayOrder} 的刺激电流不平衡。"
                );
        }

        var mapping = channelMappingService
            .Load()
            .Mappings.ToDictionary(
                x => x.ElectrodeId,
                x => x.PhysicalChannel,
                StringComparer.OrdinalIgnoreCase
            );
        foreach (var acquisition in package.Electrodes.Acquisition)
        {
            if (
                simulation
                    ? acquisition.PhysicalChannelId is null or < 1 or > 32
                    : !mapping.TryGetValue(acquisition.SiteId, out var current)
                        || current != acquisition.PhysicalChannelId
            )
                throw new ExperimentPackageValidationException(
                    $"采集点位 {acquisition.SiteId} 的物理通道与当前配置不一致。"
                );
        }
        if (
            package
                .Electrodes.Acquisition.Select(x => x.SiteId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != package.Electrodes.Acquisition.Count
            || package
                .Electrodes.Acquisition.Select(x => x.PhysicalChannelId)
                .Where(x => x.HasValue)
                .Distinct()
                .Count() != package.Electrodes.Acquisition.Count(x => x.PhysicalChannelId.HasValue)
            || (
                !stimulusOnly
                && string.Equals(
                    package.Electrodes.ReferenceSiteId,
                    package.Electrodes.GroundSiteId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            || package.Electrodes.Acquisition.Any(x =>
                string.Equals(
                    x.SiteId,
                    package.Electrodes.ReferenceSiteId,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    x.SiteId,
                    package.Electrodes.GroundSiteId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
            throw new ExperimentPackageValidationException("采集、REF、GND 配置存在重复或冲突。");

        var targetIds = package.Stimulus.Targets.ToDictionary(
            x => x.TargetKey,
            _ => Guid.NewGuid()
        );
        var targets = package
            .Stimulus.Targets.OrderBy(x => x.DisplayOrder)
            .Select(target =>
            {
                if (target.Channels.Count(x => x.Role == StimulationChannelRole.FixedActive) != 1)
                    throw new ExperimentPackageValidationException(
                        $"靶点 {target.DisplayOrder} 必须有且仅有一个固定刺激通道。"
                    );
                return new StimulusTargetSnapshot(
                    targetIds[target.TargetKey],
                    target.DisplayOrder,
                    target.PeakCurrentMilliAmps,
                    target
                        .Channels.Single(x => x.Role == StimulationChannelRole.FixedActive)
                        .PhysicalChannelId,
                    target
                        .Channels.Select(x => new StimulationPhysicalChannelSnapshot(
                            x.PhysicalChannelId,
                            x.Role,
                            x.CurrentMilliAmps
                        ))
                        .ToArray()
                );
            })
            .ToArray();
        var stimulusSnapshot = new StimulusConfigurationSnapshot(
            package.Stimulus.Kind,
            package.Stimulus.ArrayMode,
            package.Stimulus.Direction,
            package.Stimulus.ShamMode,
            package.Stimulus.RampSeconds,
            package.Stimulus.FrequencyHz,
            package.Stimulus.DutyPercent,
            targets
        );
        var assignments = package
            .Stimulus.Targets.SelectMany(target =>
                target.Channels.Select(channel => new StimulusElectrodeAssignment(
                    channel.SiteId,
                    targetIds[target.TargetKey],
                    channel.PhysicalChannelId,
                    channel.Role
                ))
            )
            .ToArray();
        return new ExperimentConfigurationTemplate(
            stimulusSnapshot,
            assignments,
            package.Electrodes.Acquisition.Select(x => x.SiteId).ToArray(),
            package.Electrodes.ReferenceSiteId,
            package.Electrodes.GroundSiteId,
            package.Electrodes.SampleRateHz,
            new ExperimentTimingTemplate(
                package.Timing.Mode,
                package.Timing.AcquisitionMilliseconds,
                package.Timing.BlankingMilliseconds,
                package.Timing.StimulationMilliseconds,
                package.Timing.RecoveryMilliseconds,
                package.Timing.CycleCount
            ),
            sourceDisplayName,
            package
                .Electrodes.Acquisition.Select(x => new EegPhysicalChannelMapping(
                    x.SiteId,
                    x.PhysicalChannelId
                ))
                .ToArray(),
            package.Simulation,
            package.CreationMode
        );
    }

    public void ValidateSimulationTemplate(
        ExperimentConfigurationTemplate template,
        IReadOnlyList<EegPhysicalChannelMapping>? physicalChannels = null
    )
    {
        var s = template.StimulusConfiguration;
        if (
            !Enum.IsDefined(s.Kind)
            || !Enum.IsDefined(s.ArrayMode)
            || !Enum.IsDefined(s.Direction)
            || !Enum.IsDefined(s.ShamMode)
            || !Enum.IsDefined(template.Timing.Mode)
        )
            throw new ExperimentPackageValidationException("配置包含未知枚举值。");
        if (template.AcquisitionChannels.Count == 0 || template.Timing.CycleCount is < 1 or > 200)
            throw new ExperimentPackageValidationException(
                "至少选择一个采集通道，循环次数为 1–200。"
            );
        if (template.Timing.Mode == ExperimentRunMode.Manual && template.Timing.CycleCount != 1)
            throw new ExperimentPackageValidationException("手动模式固定为一个周期和末次采集。");
        if (template.Timing.AcquisitionMilliseconds * template.SampleRateHz / 1000 < 1)
            throw new ExperimentPackageValidationException("采集阶段必须至少包含一个采样点。");
        if (
            template
                .StimulusElectrodes.Select(x => x.SiteId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != template.StimulusElectrodes.Count
        )
            throw new ExperimentPackageValidationException("刺激电极点位不能重复分配。");
        if (s.Kind == StimulusKind.EnvelopeTAcs && s.Envelope is null)
            throw new ExperimentPackageValidationException("包络参数不能为空。");
        if (
            s.Envelope is { } e
            && (
                s.Kind != StimulusKind.EnvelopeTAcs
                || !double.IsFinite(e.ModulationHz)
                || e.ModulationHz is <= 0 or > 100
                || !double.IsFinite(e.Depth)
                || e.Depth is < 0 or > 1
                || !double.IsFinite(e.DelayMilliseconds)
                || Math.Abs(e.DelayMilliseconds) > 60000
            )
        )
            throw new ExperimentPackageValidationException(
                "包络频率须在 0–100 Hz，深度在 0–1，时延在 ±60000 ms。"
            );
        if (
            s.Kind is StimulusKind.TAcs or StimulusKind.EnvelopeTAcs or StimulusKind.TPcs
            && s.Frequency <= 0
        )
            throw new ExperimentPackageValidationException("频率必须大于零。");
        ValidateCore(CreatePackage(template, physicalChannels), "生成配置", true);
    }

    private ExperimentPackageV1 CreatePackage(
        ExperimentConfigurationTemplate template,
        IReadOnlyList<EegPhysicalChannelMapping>? mappings = null
    )
    {
        template = AcquisitionOnlyConfiguration.Normalize(template);
        var assignments = template.StimulusElectrodes.ToLookup(x => x.TargetId);
        var targets = template
            .StimulusConfiguration.Targets.OrderBy(x => x.DisplayOrder)
            .Select(
                (target, index) =>
                {
                    var channels =
                        target.Channels.Count > 0
                            ? target.Channels
                            : assignments[target.TargetId]
                                .Select(assignment => new StimulationPhysicalChannelSnapshot(
                                    assignment.PhysicalChannelId,
                                    assignment.Role,
                                    target.PeakCurrent
                                ))
                                .ToArray();
                    return new ExperimentPackageTarget(
                        index + 1,
                        target.DisplayOrder,
                        target.PeakCurrent,
                        channels
                            .Select(channel =>
                            {
                                var assignment = assignments[target.TargetId]
                                    .Single(x => x.PhysicalChannelId == channel.PhysicalChannelId);
                                return new ExperimentPackageChannel(
                                    assignment.SiteId,
                                    channel.PhysicalChannelId,
                                    channel.Role,
                                    channel.Current
                                );
                            })
                            .ToArray()
                    );
                }
            )
            .ToArray();
        var physicalMappings = (
            mappings ?? template.PhysicalChannels ?? channelMappingService.Load().Mappings
        ).ToDictionary(
            x => x.ElectrodeId,
            x => x.PhysicalChannel,
            StringComparer.OrdinalIgnoreCase
        );
        var mapping = template
            .AcquisitionChannels.Select(site => new ExperimentPackageAcquisitionChannel(
                site,
                physicalMappings.GetValueOrDefault(site)
            ))
            .ToArray();
        return new ExperimentPackageV1(
            ExperimentPackageV1.ExpectedFormat,
            ExperimentPackageV1.CurrentVersion,
            DateTimeOffset.UtcNow,
            applicationVersion.Version,
            new ExperimentPackageStimulus(
                template.StimulusConfiguration.Kind,
                template.StimulusConfiguration.ArrayMode,
                template.StimulusConfiguration.Direction,
                template.StimulusConfiguration.ShamMode,
                template.StimulusConfiguration.RampSeconds,
                template.StimulusConfiguration.Frequency,
                template.StimulusConfiguration.DutyPercent,
                targets
            ),
            new ExperimentPackageElectrodes(
                mapping,
                template.ReferenceChannel,
                template.GroundChannel,
                template.SampleRateHz
            ),
            new ExperimentPackageTiming(
                template.Timing.Mode,
                template.Timing.AcquisitionMilliseconds,
                template.Timing.BlankingMilliseconds,
                template.Timing.StimulationMilliseconds,
                template.Timing.RecoveryMilliseconds,
                template.Timing.CycleCount
            ),
            CreationMode: template.CreationMode
        );
    }
}
