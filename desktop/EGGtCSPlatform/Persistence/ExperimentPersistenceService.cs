using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Persistence;

public sealed record ExperimentDraft(long Id, string ExperimentCode, string SubjectCode);

public sealed record ImpedanceSnapshotValue(
    string SiteId,
    ImpedanceMeasurementKind Kind,
    double? KiloOhms,
    bool Passed,
    DateTimeOffset MeasuredAtUtc
);

public sealed record ExperimentHistoryItem(
    long ExperimentId,
    string ExperimentCode,
    string SubjectCode,
    string StimulusKind,
    double TotalCurrentMilliAmps,
    IReadOnlyList<string> ElectrodeSites,
    int RunCount,
    DateTimeOffset ScheduledAt,
    IReadOnlyList<ExperimentHistoryRunItem> Runs
);

public sealed record ExperimentHistoryRunItem(
    Guid RunId,
    ExperimentRunStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? RawFilePath,
    EegPacketStatistics? PacketStatistics = null
);

public sealed record ExperimentHistoryQuery(
    string SearchText = "",
    string? StimulusKind = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    ExperimentRunStatus? RunStatus = null,
    int PageNumber = 1,
    int PageSize = 10
);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize
)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public interface IExperimentPersistenceService
{
    Task<string> PreviewExperimentCodeAsync(
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken = default
    );
    Task<ExperimentDraft> CreateOrUpdateDraftAsync(
        long? draftId,
        DateTimeOffset scheduledAt,
        string subjectCode,
        string remarks,
        CancellationToken cancellationToken = default
    );
    Task SaveConfigurationAsync(
        long experimentId,
        ExperimentConfigurationTemplate template,
        IReadOnlyList<ImpedanceSnapshotValue> impedances,
        CancellationToken cancellationToken = default
    );
    Task<ExperimentConfigurationTemplate> LoadTemplateAsync(
        long experimentId,
        CancellationToken cancellationToken = default
    );
    Task<IReadOnlyList<ExperimentHistoryItem>> ListHistoryAsync(
        CancellationToken cancellationToken = default
    );
    async Task<PagedResult<ExperimentHistoryItem>> QueryHistoryAsync(
        ExperimentHistoryQuery query,
        CancellationToken cancellationToken = default
    ) => ExperimentHistoryQueryEvaluator.Apply(await ListHistoryAsync(cancellationToken), query);
    Task<ExperimentRunRouteData> LoadHistoricalRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromException<ExperimentRunRouteData>(
            new NotSupportedException("Historical run loading is not available.")
        );
    string CreateRandomSubjectCode();
}

internal static class ExperimentHistoryQueryEvaluator
{
    public static PagedResult<ExperimentHistoryItem> Apply(
        IReadOnlyList<ExperimentHistoryItem> source,
        ExperimentHistoryQuery query
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        var searchText = query.SearchText.Trim();
        var filtered = source
            .Where(item =>
                (
                    searchText.Length == 0
                    || item.ExperimentCode.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    || item.SubjectCode.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                )
                && (
                    string.IsNullOrWhiteSpace(query.StimulusKind)
                    || string.Equals(
                        item.StimulusKind,
                        query.StimulusKind.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                && (query.RunStatus is null || item.Runs.Any(run => run.Status == query.RunStatus))
                && IsWithinDateRange(item.ScheduledAt, query.StartDate, query.EndDate)
            )
            .OrderByDescending(item => item.ScheduledAt)
            .ThenByDescending(item => item.ExperimentId)
            .ToArray();
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var totalPages =
            filtered.Length == 0 ? 0 : (int)Math.Ceiling(filtered.Length / (double)pageSize);
        var pageNumber = totalPages == 0 ? 1 : Math.Min(Math.Max(1, query.PageNumber), totalPages);
        return new PagedResult<ExperimentHistoryItem>(
            filtered.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToArray(),
            filtered.Length,
            pageNumber,
            pageSize
        );
    }

    private static bool IsWithinDateRange(
        DateTimeOffset scheduledAt,
        DateOnly? startDate,
        DateOnly? endDate
    )
    {
        var localDate = DateOnly.FromDateTime(scheduledAt.ToLocalTime().DateTime);
        return (startDate is null || localDate >= startDate)
            && (endDate is null || localDate <= endDate);
    }
}

public interface IExperimentConfigurationTemplateService
{
    Task<ExperimentConfigurationTemplate> CreateFromHistoryAsync(
        long experimentId,
        CancellationToken cancellationToken = default
    );
}

public sealed class ExperimentConfigurationTemplateService(
    IExperimentPersistenceService experiments
) : IExperimentConfigurationTemplateService
{
    public Task<ExperimentConfigurationTemplate> CreateFromHistoryAsync(
        long experimentId,
        CancellationToken cancellationToken = default
    ) => experiments.LoadTemplateAsync(experimentId, cancellationToken);
}

public sealed class ExperimentPersistenceService(
    IDbContextFactory<AppDbContext> contextFactory,
    ICurrentOperatorContext currentOperator,
    IEegPhysicalChannelMappingService channelMappings
) : IExperimentPersistenceService
{
    public async Task<string> PreviewExperimentCodeAsync(
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await AllocateCodeAsync(db, scheduledAt, cancellationToken);
    }

    public async Task<ExperimentDraft> CreateOrUpdateDraftAsync(
        long? draftId,
        DateTimeOffset scheduledAt,
        string subjectCode,
        string remarks,
        CancellationToken cancellationToken = default
    )
    {
        if (!currentOperator.IsAuthenticated)
            throw new InvalidOperationException("请先登录操作员账号。");
        var trimmedSubject = subjectCode.Trim();
        if (trimmedSubject.Length is < 1 or > 64 || trimmedSubject.Any(char.IsControl))
            throw new ArgumentException("被试 ID 必须为 1–64 个可见字符。", nameof(subjectCode));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(
                cancellationToken
            );
            var normalized = SubjectLookupService.NormalizeSubject(trimmedSubject);
            var subject = await db.Subjects.SingleOrDefaultAsync(
                x => x.NormalizedSubjectCode == normalized,
                cancellationToken
            );
            subject ??= new SubjectEntity
            {
                SubjectCode = trimmedSubject,
                NormalizedSubjectCode = normalized,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            if (subject.Id == 0)
                db.Subjects.Add(subject);

            ExperimentEntity experiment;
            if (draftId is { } id)
            {
                experiment = await db.Experiments.SingleAsync(x => x.Id == id, cancellationToken);
                if (experiment.Status != ExperimentStatus.Draft)
                    throw new InvalidOperationException("实验已经开始，不能再修改基础信息。");
            }
            else
            {
                experiment = new ExperimentEntity
                {
                    ExperimentCode = await AllocateCodeAsync(db, scheduledAt, cancellationToken),
                    ScheduledAt = scheduledAt,
                    Remarks = remarks.Trim(),
                    Status = ExperimentStatus.Draft,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    OperatorId = currentOperator.OperatorId,
                    Subject = subject,
                };
                db.Experiments.Add(experiment);
            }

            var desiredPrefix = $"EXP-{scheduledAt:yyyyMMdd}-";
            if (!experiment.ExperimentCode.StartsWith(desiredPrefix, StringComparison.Ordinal))
                experiment.ExperimentCode = await AllocateCodeAsync(
                    db,
                    scheduledAt,
                    cancellationToken
                );
            experiment.ScheduledAt = scheduledAt;
            experiment.Subject = subject;
            experiment.Remarks = remarks.Trim();
            experiment.UpdatedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ExperimentDraft(
                    experiment.Id,
                    experiment.ExperimentCode,
                    subject.SubjectCode
                );
            }
            catch (DbUpdateException) when (attempt < 4)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }
        throw new InvalidOperationException("无法分配唯一实验 ID，请稍后重试。");
    }

    public async Task SaveConfigurationAsync(
        long experimentId,
        ExperimentConfigurationTemplate template,
        IReadOnlyList<ImpedanceSnapshotValue> impedances,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = new LoggedOperation(
            nameof(ExperimentPersistenceService),
            "Configuration.Save",
            "fields=StimulusParadigm,Electrodes,ImpedanceSnapshots",
            experimentId.ToString(),
            cancellationToken
        );
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var experiment = await db
            .Experiments.Include(x => x.StimulusParadigm)
                .ThenInclude(x => x!.Targets)
                    .ThenInclude(x => x.Channels)
            .Include(x => x.Electrodes)
            .Include(x => x.ImpedanceSnapshots)
            .SingleAsync(x => x.Id == experimentId, cancellationToken);
        if (experiment.StimulusParadigm is not null)
            db.StimulusParadigms.Remove(experiment.StimulusParadigm);
        db.ExperimentElectrodes.RemoveRange(experiment.Electrodes);
        db.ImpedanceSnapshots.RemoveRange(experiment.ImpedanceSnapshots);

        ApplyConfiguration(experiment, template, impedances, channelMappings.Load().Mappings);
        experiment.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        operation.Complete("fields=StimulusParadigm,Electrodes,ImpedanceSnapshots");
    }

    internal static void ApplyConfiguration(
        ExperimentEntity experiment,
        ExperimentConfigurationTemplate template,
        IReadOnlyList<ImpedanceSnapshotValue> impedances,
        IReadOnlyList<EegPhysicalChannelMapping> mappings
    )
    {
        template = AcquisitionOnlyConfiguration.Normalize(template);
        if (template.CreationMode == ExperimentCreationMode.AcquisitionOnly)
            impedances = impedances
                .Where(x => x.Kind == ImpedanceMeasurementKind.Acquisition)
                .ToArray();
        var paradigm = new StimulusParadigmEntity
        {
            Experiment = experiment,
            Kind = template.StimulusConfiguration.Kind.ToString(),
            EnvelopeJson = template.StimulusConfiguration.Envelope is null
                ? null
                : SimulationJson.Write(template.StimulusConfiguration.Envelope),
            ArrayMode = template.StimulusConfiguration.ArrayMode.ToString(),
            Direction = template.StimulusConfiguration.Direction.ToString(),
            ShamMode = template.StimulusConfiguration.ShamMode.ToString(),
            RampSeconds = template.StimulusConfiguration.RampSeconds,
            FrequencyHz = template.StimulusConfiguration.Frequency,
            DutyPercent = template.StimulusConfiguration.DutyPercent,
        };
        foreach (var target in template.StimulusConfiguration.Targets.OrderBy(x => x.DisplayOrder))
        {
            var targetEntity = new StimulusTargetEntity
            {
                DisplayOrder = target.DisplayOrder,
                PeakCurrentMilliAmps = target.PeakCurrent,
            };
            var channels = GetEffectiveChannels(target, template.StimulusElectrodes);
            foreach (var channel in channels)
            {
                var assignment = template.StimulusElectrodes.Single(x =>
                    x.TargetId == target.TargetId
                    && x.PhysicalChannelId == channel.PhysicalChannelId
                );
                targetEntity.Channels.Add(
                    new StimulusChannelEntity
                    {
                        SiteId = assignment.SiteId,
                        PhysicalChannelId = channel.PhysicalChannelId,
                        Role = channel.Role.ToString(),
                        CurrentMilliAmps = channel.Current,
                    }
                );
            }
            paradigm.Targets.Add(targetEntity);
        }
        experiment.StimulusParadigm = paradigm;
        experiment.CreationMode = template.CreationMode;

        var acquisitionMap = mappings.ToDictionary(
            x => x.ElectrodeId,
            x => x.PhysicalChannel,
            StringComparer.OrdinalIgnoreCase
        );
        var siteIds = template
            .StimulusElectrodes.Select(x => x.SiteId)
            .Concat(template.AcquisitionChannels)
            .Append(template.ReferenceChannel)
            .Append(template.GroundChannel)
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var siteId in siteIds)
        {
            var stimulus = template.StimulusElectrodes.FirstOrDefault(x =>
                string.Equals(x.SiteId, siteId, StringComparison.OrdinalIgnoreCase)
            );
            var usage =
                string.Equals(siteId, template.ReferenceChannel, StringComparison.OrdinalIgnoreCase)
                    ? ElectrodeUsage.Reference
                : string.Equals(siteId, template.GroundChannel, StringComparison.OrdinalIgnoreCase)
                    ? ElectrodeUsage.Ground
                : template.AcquisitionChannels.Contains(siteId, StringComparer.OrdinalIgnoreCase)
                    ? ElectrodeUsage.Acquisition
                : ElectrodeUsage.None;
            experiment.Electrodes.Add(
                new ExperimentElectrodeEntity
                {
                    SiteId = siteId,
                    AcquisitionUsage = usage,
                    IsStimulus = stimulus is not null,
                    AcquisitionPhysicalChannelId = acquisitionMap.GetValueOrDefault(siteId),
                    StimulationPhysicalChannelId = stimulus?.PhysicalChannelId,
                    StimulationRole = stimulus?.Role.ToString(),
                    TargetDisplayOrder = stimulus is null
                        ? null
                        : template
                            .StimulusConfiguration.Targets.Single(x =>
                                x.TargetId == stimulus.TargetId
                            )
                            .DisplayOrder,
                }
            );
        }
        foreach (var impedance in impedances)
        {
            experiment.ImpedanceSnapshots.Add(
                new ImpedanceSnapshotEntity
                {
                    SiteId = impedance.SiteId,
                    Kind = impedance.Kind,
                    KiloOhms = impedance.KiloOhms,
                    Passed = impedance.Passed,
                    MeasuredAtUtc = impedance.MeasuredAtUtc,
                }
            );
        }
    }

    private static IReadOnlyList<StimulationPhysicalChannelSnapshot> GetEffectiveChannels(
        StimulusTargetSnapshot target,
        IReadOnlyList<StimulusElectrodeAssignment> assignments
    ) =>
        target.Channels.Count > 0
            ? target.Channels
            : assignments
                .Where(x => x.TargetId == target.TargetId)
                .Select(x => new StimulationPhysicalChannelSnapshot(
                    x.PhysicalChannelId,
                    x.Role,
                    target.PeakCurrent
                ))
                .ToArray();

    public async Task<ExperimentConfigurationTemplate> LoadTemplateAsync(
        long experimentId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var experiment = await db
            .Experiments.AsNoTracking()
            .Include(x => x.StimulusParadigm)
                .ThenInclude(x => x!.Targets)
                    .ThenInclude(x => x.Channels)
            .Include(x => x.Electrodes)
            .Include(x => x.Runs)
            .SingleAsync(x => x.Id == experimentId, cancellationToken);
        var paradigm =
            experiment.StimulusParadigm
            ?? throw new InvalidOperationException("历史实验没有可复制的刺激配置。");
        var targetIds = paradigm.Targets.ToDictionary(x => x.DisplayOrder, _ => Guid.NewGuid());
        var targets = paradigm
            .Targets.OrderBy(x => x.DisplayOrder)
            .Select(target => new StimulusTargetSnapshot(
                targetIds[target.DisplayOrder],
                target.DisplayOrder,
                target.PeakCurrentMilliAmps,
                target
                    .Channels.Single(x => x.Role == StimulationChannelRole.FixedActive.ToString())
                    .PhysicalChannelId,
                target
                    .Channels.Select(x => new StimulationPhysicalChannelSnapshot(
                        x.PhysicalChannelId,
                        Enum.Parse<StimulationChannelRole>(x.Role),
                        x.CurrentMilliAmps
                    ))
                    .ToArray()
            ))
            .ToArray();
        var configuration = new StimulusConfigurationSnapshot(
            Enum.Parse<StimulusKind>(paradigm.Kind),
            Enum.Parse<StimulusArrayMode>(paradigm.ArrayMode),
            Enum.Parse<StimulusDirection>(paradigm.Direction),
            Enum.Parse<ShamWaveformMode>(paradigm.ShamMode),
            paradigm.RampSeconds,
            paradigm.FrequencyHz,
            paradigm.DutyPercent,
            targets,
            SimulationJson.Read<EnvelopeParameters>(paradigm.EnvelopeJson)
        );
        var assignments = paradigm
            .Targets.SelectMany(target =>
                target.Channels.Select(channel => new StimulusElectrodeAssignment(
                    channel.SiteId,
                    targetIds[target.DisplayOrder],
                    channel.PhysicalChannelId,
                    Enum.Parse<StimulationChannelRole>(channel.Role)
                ))
            )
            .ToArray();
        var latestRun = experiment.Runs.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        var timing = latestRun is null
            ? experiment.CreationMode == ExperimentCreationMode.StimulusOnly
                ? new ExperimentTimingTemplate(ExperimentRunMode.Manual, 0, 0, 600_000, 0, 1)
                : new ExperimentTimingTemplate(
                    ExperimentRunMode.Manual,
                    10_000,
                    1_000,
                    15_000,
                    1_000,
                    1
                )
            : new ExperimentTimingTemplate(
                Enum.Parse<ExperimentRunMode>(latestRun.RunMode),
                latestRun.AcquisitionDurationMilliseconds,
                latestRun.BlankingDurationMilliseconds,
                latestRun.StimulationDurationMilliseconds,
                latestRun.RecoveryDurationMilliseconds,
                latestRun.CycleCount
            );
        return AcquisitionOnlyConfiguration.Normalize(
            new ExperimentConfigurationTemplate(
                configuration,
                assignments,
                experiment
                    .Electrodes.Where(x => x.AcquisitionUsage == ElectrodeUsage.Acquisition)
                    .Select(x => x.SiteId)
                    .ToArray(),
                experiment.CreationMode == ExperimentCreationMode.StimulusOnly
                    ? string.Empty
                    : experiment
                        .Electrodes.Single(x => x.AcquisitionUsage == ElectrodeUsage.Reference)
                        .SiteId,
                experiment.CreationMode == ExperimentCreationMode.StimulusOnly
                    ? string.Empty
                    : experiment
                        .Electrodes.Single(x => x.AcquisitionUsage == ElectrodeUsage.Ground)
                        .SiteId,
                latestRun?.SampleRateHz ?? 500,
                timing,
                $"历史实验 {experiment.ExperimentCode}",
                experiment
                    .Electrodes.Where(x => x.AcquisitionUsage == ElectrodeUsage.Acquisition)
                    .Select(x => new EegPhysicalChannelMapping(
                        x.SiteId,
                        x.AcquisitionPhysicalChannelId
                    ))
                    .ToArray(),
                null,
                experiment.CreationMode
            )
        );
    }

    public async Task<IReadOnlyList<ExperimentHistoryItem>> ListHistoryAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var experiments = await db
            .Experiments.AsNoTracking()
            .Where(x =>
                x.Runs.Any(r =>
                    r.Status != ExperimentRunStatus.Preparing
                    && r.Status != ExperimentRunStatus.Running
                )
            )
            .Include(x => x.Subject)
            .Include(x => x.StimulusParadigm)
                .ThenInclude(x => x!.Targets)
            .Include(x => x.Electrodes)
            .Include(x => x.Runs)
                .ThenInclude(x => x.Files)
            .ToListAsync(cancellationToken);
        var items = experiments
            .Select(x =>
            {
                var runs = x
                    .Runs.Where(r =>
                        r.Status != ExperimentRunStatus.Preparing
                        && r.Status != ExperimentRunStatus.Running
                    )
                    .OrderByDescending(r => r.CreatedAtUtc)
                    .Select(r => new ExperimentHistoryRunItem(
                        r.Id,
                        r.Status,
                        r.CreatedAtUtc,
                        r.StartedAtUtc,
                        r.EndedAtUtc,
                        r.Files.Where(f =>
                                f.Format == EegFileFormat.Staging
                                && f.Location == EegFileLocation.Managed
                                && f.State == EegFileState.Available
                            )
                            .OrderByDescending(f => f.CreatedAtUtc)
                            .Select(f => f.Path)
                            .FirstOrDefault(),
                        CreatePacketStatistics(r)
                    ))
                    .ToArray();
                return new ExperimentHistoryItem(
                    x.Id,
                    x.ExperimentCode,
                    x.Subject.SubjectCode,
                    x.StimulusParadigm == null ? "-" : x.StimulusParadigm.Kind,
                    x.StimulusParadigm == null
                        ? 0
                        : x.StimulusParadigm.Targets.Sum(t => t.PeakCurrentMilliAmps),
                    x.Electrodes.Where(e =>
                            e.IsStimulus || e.AcquisitionUsage != ElectrodeUsage.None
                        )
                        .Select(e => e.SiteId)
                        .ToArray(),
                    runs.Length,
                    x.ScheduledAt,
                    runs
                );
            })
            .ToList();
        // SQLite stores DateTimeOffset as TEXT and cannot translate ORDER BY for it.
        // Keep filtering/projection in SQL, then order the small history result in memory.
        return items.OrderByDescending(x => x.ScheduledAt).ToArray();
    }

    public async Task<PagedResult<ExperimentHistoryItem>> QueryHistoryAsync(
        ExperimentHistoryQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageNumber = Math.Max(1, query.PageNumber);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var searchText = query.SearchText.Trim().ToUpperInvariant();
        var stimulusKind = query.StimulusKind?.Trim().ToUpperInvariant();

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidateQuery = db
            .Experiments.AsNoTracking()
            .Where(x =>
                x.Runs.Any(r =>
                    r.Status != ExperimentRunStatus.Preparing
                    && r.Status != ExperimentRunStatus.Running
                )
            );
        if (searchText.Length > 0)
        {
            candidateQuery = candidateQuery.Where(x =>
                x.ExperimentCode.ToUpper().Contains(searchText)
                || x.Subject.NormalizedSubjectCode.Contains(searchText)
            );
        }
        if (!string.IsNullOrEmpty(stimulusKind))
        {
            candidateQuery = candidateQuery.Where(x =>
                x.StimulusParadigm != null && x.StimulusParadigm.Kind.ToUpper() == stimulusKind
            );
        }
        if (query.RunStatus is { } runStatus)
        {
            candidateQuery = candidateQuery.Where(x => x.Runs.Any(r => r.Status == runStatus));
        }

        // SQLite cannot order or range-filter DateTimeOffset directly. Materialize only
        // the lightweight keys, then apply local-calendar filtering and stable ordering.
        var candidates = await candidateQuery
            .Select(x => new HistoryCandidate(x.Id, x.ScheduledAt))
            .ToListAsync(cancellationToken);
        var orderedCandidates = candidates
            .Where(x => IsWithinDateRange(x.ScheduledAt, query.StartDate, query.EndDate))
            .OrderByDescending(x => x.ScheduledAt)
            .ThenByDescending(x => x.Id)
            .ToArray();
        var totalCount = orderedCandidates.Length;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        pageNumber = totalPages == 0 ? 1 : Math.Min(pageNumber, totalPages);
        var pageIds = orderedCandidates
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Id)
            .ToArray();
        if (pageIds.Length == 0)
            return new PagedResult<ExperimentHistoryItem>([], totalCount, pageNumber, pageSize);

        var experiments = await db
            .Experiments.AsNoTracking()
            .Where(x => pageIds.Contains(x.Id))
            .Include(x => x.Subject)
            .Include(x => x.StimulusParadigm)
                .ThenInclude(x => x!.Targets)
            .Include(x => x.Electrodes)
            .Include(x => x.Runs)
                .ThenInclude(x => x.Files)
            .ToListAsync(cancellationToken);
        var byId = experiments.ToDictionary(x => x.Id);
        var items = pageIds
            .Where(byId.ContainsKey)
            .Select(id => CreateHistoryItem(byId[id]))
            .ToArray();
        return new PagedResult<ExperimentHistoryItem>(items, totalCount, pageNumber, pageSize);
    }

    private static ExperimentHistoryItem CreateHistoryItem(ExperimentEntity experiment)
    {
        var runs = experiment
            .Runs.Where(r =>
                r.Status != ExperimentRunStatus.Preparing && r.Status != ExperimentRunStatus.Running
            )
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => new ExperimentHistoryRunItem(
                r.Id,
                r.Status,
                r.CreatedAtUtc,
                r.StartedAtUtc,
                r.EndedAtUtc,
                r.Files.Where(f =>
                        f.Format == EegFileFormat.Staging
                        && f.Location == EegFileLocation.Managed
                        && f.State == EegFileState.Available
                    )
                    .OrderByDescending(f => f.CreatedAtUtc)
                    .Select(f => f.Path)
                    .FirstOrDefault(),
                CreatePacketStatistics(r)
            ))
            .ToArray();
        return new ExperimentHistoryItem(
            experiment.Id,
            experiment.ExperimentCode,
            experiment.Subject.SubjectCode,
            experiment.StimulusParadigm == null ? "-" : experiment.StimulusParadigm.Kind,
            experiment.StimulusParadigm == null
                ? 0
                : experiment.StimulusParadigm.Targets.Sum(t => t.PeakCurrentMilliAmps),
            experiment
                .Electrodes.Where(e => e.IsStimulus || e.AcquisitionUsage != ElectrodeUsage.None)
                .Select(e => e.SiteId)
                .ToArray(),
            runs.Length,
            experiment.ScheduledAt,
            runs
        );
    }

    private static bool IsWithinDateRange(
        DateTimeOffset scheduledAt,
        DateOnly? startDate,
        DateOnly? endDate
    )
    {
        var localDate = DateOnly.FromDateTime(scheduledAt.ToLocalTime().DateTime);
        return (startDate is null || localDate >= startDate)
            && (endDate is null || localDate <= endDate);
    }

    private sealed record HistoryCandidate(long Id, DateTimeOffset ScheduledAt);

    public async Task<ExperimentRunRouteData> LoadHistoricalRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await db
            .ExperimentRuns.AsNoTracking()
            .Include(x => x.Experiment)
                .ThenInclude(x => x.Subject)
            .Include(x => x.Experiment)
                .ThenInclude(x => x.StimulusParadigm)
                    .ThenInclude(x => x!.Targets)
                        .ThenInclude(x => x.Channels)
            .Include(x => x.Experiment)
                .ThenInclude(x => x.Electrodes)
            .Include(x => x.Events)
            .Include(x => x.Files)
            .SingleAsync(x => x.Id == runId, cancellationToken);
        if (run.Status is ExperimentRunStatus.Preparing or ExperimentRunStatus.Running)
            throw new InvalidOperationException("实验尚未结束，不能查看历史结果。");
        var rawPath =
            run.Files.Where(x =>
                    x.Format == EegFileFormat.Staging
                    && x.Location == EegFileLocation.Managed
                    && x.State == EegFileState.Available
                )
                .OrderByDescending(x => x.CreatedAtUtc)
                .Select(x => x.Path)
                .FirstOrDefault()
            ?? string.Empty;
        var experiment = run.Experiment;
        var paradigm =
            experiment.StimulusParadigm
            ?? throw new InvalidOperationException("历史实验没有刺激配置。");
        var targetIds = paradigm.Targets.ToDictionary(x => x.DisplayOrder, _ => Guid.NewGuid());
        var targets = paradigm
            .Targets.OrderBy(x => x.DisplayOrder)
            .Select(target => new ExperimentStimulusTargetSnapshot(
                targetIds[target.DisplayOrder],
                target.DisplayOrder,
                target.PeakCurrentMilliAmps,
                target
                    .Channels.Single(x => x.Role == StimulationChannelRole.FixedActive.ToString())
                    .PhysicalChannelId,
                target
                    .Channels.Select(x => new ExperimentStimulationChannelSnapshot(
                        x.PhysicalChannelId,
                        Enum.Parse<StimulationChannelRole>(x.Role),
                        x.CurrentMilliAmps
                    ))
                    .ToArray()
            ))
            .ToArray();
        var assignments = paradigm
            .Targets.SelectMany(target =>
                target.Channels.Select(channel => new StimulusElectrodeAssignment(
                    channel.SiteId,
                    targetIds[target.DisplayOrder],
                    channel.PhysicalChannelId,
                    Enum.Parse<StimulationChannelRole>(channel.Role)
                ))
            )
            .ToArray();
        var acquisition = experiment
            .Electrodes.Where(x => x.AcquisitionUsage == ElectrodeUsage.Acquisition)
            .ToArray();
        var channelNames = acquisition
            .Where(x => x.AcquisitionPhysicalChannelId.HasValue)
            .ToDictionary(x => x.AcquisitionPhysicalChannelId!.Value, x => x.SiteId);
        var started = run.StartedAtUtc ?? run.CreatedAtUtc;
        var ended = run.EndedAtUtc ?? run.LastHeartbeatAtUtc;
        var logicalTimelineEndSeconds = ResolveHistoricalLogicalTimelineEnd(run.Events);
        var intervals = run
            .Events.OrderBy(x => x.Sequence)
            .Select(x =>
            {
                var timeline = ResolveHistoricalEventTimeline(x, started, ended);
                return new HistoricalExperimentStageInterval(
                    x.Kind switch
                    {
                        ExperimentEventKind.Acquisition => ExperimentRunStage.Acquisition,
                        ExperimentEventKind.Blanking => ExperimentRunStage.Blanking,
                        ExperimentEventKind.Stimulation => ExperimentRunStage.Stimulation,
                        _ => ExperimentRunStage.Recovery,
                    },
                    x.Cycle,
                    timeline.StartSeconds,
                    timeline.EndSeconds
                );
            })
            .ToArray();
        if (
            run.ConfigurationJson is { } configurationJson
            && JsonSerializer.Deserialize<ExperimentConfigurationTemplate>(configurationJson)
                is { CreationMode: ExperimentCreationMode.StimulusOnly } saved
        )
        {
            return new ExperimentRunRouteData(
                experiment.ExperimentCode,
                experiment.Subject.SubjectCode,
                ExperimentStimulusConfigurationSnapshot.From(
                    saved.StimulusConfiguration,
                    saved.StimulusElectrodes
                ),
                saved.StimulusElectrodes,
                [],
                "",
                "",
                run.SampleRateHz,
                "history",
                experiment.Id,
                experiment.ScheduledAt,
                experiment.Remarks,
                saved.Timing,
                historicalResult: new HistoricalExperimentRunContext(
                    run.Id,
                    string.Empty,
                    run.Status,
                    started,
                    ended,
                    new Dictionary<int, string>(),
                    intervals,
                    LogicalTimelineEndSeconds: logicalTimelineEndSeconds
                ),
                creationMode: saved.CreationMode
            );
        }
        return new ExperimentRunRouteData(
            experiment.ExperimentCode,
            experiment.Subject.SubjectCode,
            new ExperimentStimulusConfigurationSnapshot(
                Enum.Parse<StimulusKind>(paradigm.Kind),
                Enum.Parse<StimulusArrayMode>(paradigm.ArrayMode),
                Enum.Parse<StimulusDirection>(paradigm.Direction),
                Enum.Parse<ShamWaveformMode>(paradigm.ShamMode),
                paradigm.RampSeconds,
                paradigm.FrequencyHz,
                paradigm.DutyPercent,
                targets,
                SimulationJson.Read<EnvelopeParameters>(paradigm.EnvelopeJson)
            ),
            assignments,
            acquisition.Select(x => x.SiteId),
            experiment.CreationMode == ExperimentCreationMode.StimulusOnly
                ? string.Empty
                : experiment
                    .Electrodes.Single(x => x.AcquisitionUsage == ElectrodeUsage.Reference)
                    .SiteId,
            experiment.CreationMode == ExperimentCreationMode.StimulusOnly
                ? string.Empty
                : experiment
                    .Electrodes.Single(x => x.AcquisitionUsage == ElectrodeUsage.Ground)
                    .SiteId,
            run.SampleRateHz,
            "history",
            experiment.Id,
            experiment.ScheduledAt,
            experiment.Remarks,
            new ExperimentTimingTemplate(
                Enum.Parse<ExperimentRunMode>(run.RunMode),
                run.AcquisitionDurationMilliseconds,
                run.BlankingDurationMilliseconds,
                run.StimulationDurationMilliseconds,
                run.RecoveryDurationMilliseconds,
                run.CycleCount
            ),
            historicalResult: new HistoricalExperimentRunContext(
                run.Id,
                rawPath,
                run.Status,
                started,
                ended,
                channelNames,
                intervals,
                CreatePacketStatistics(run),
                logicalTimelineEndSeconds,
                null
            ),
            physicalChannelNames: channelNames,
            creationMode: experiment.CreationMode
        );
    }

    internal static double? ResolveHistoricalLogicalTimelineEnd(
        IReadOnlyCollection<ExperimentEventEntity> events
    )
    {
        if (
            events.Count == 0
            || events.Any(x =>
                !x.TimelineStartMilliseconds.HasValue || !x.TimelineEndMilliseconds.HasValue
            )
        )
        {
            return null;
        }

        return Math.Max(0d, events.Max(x => x.TimelineEndMilliseconds!.Value) / 1000d);
    }

    internal static (double StartSeconds, double EndSeconds) ResolveHistoricalEventTimeline(
        ExperimentEventEntity experimentEvent,
        DateTimeOffset runStartedAtUtc,
        DateTimeOffset fallbackEndedAtUtc
    )
    {
        if (
            experimentEvent.TimelineStartMilliseconds is { } logicalStart
            && experimentEvent.TimelineEndMilliseconds is { } logicalEnd
        )
        {
            var startSeconds = Math.Max(0d, logicalStart / 1000d);
            return (startSeconds, Math.Max(startSeconds, logicalEnd / 1000d));
        }

        var eventStart = experimentEvent.StartedAtUtc ?? experimentEvent.RequestedAtUtc;
        var eventEnd = experimentEvent.EndedAtUtc ?? fallbackEndedAtUtc;
        return (
            Math.Max(0d, (eventStart - runStartedAtUtc).TotalSeconds),
            Math.Max(0d, (eventEnd - runStartedAtUtc).TotalSeconds)
        );
    }

    private static EegPacketStatistics? CreatePacketStatistics(ExperimentRunEntity run) =>
        run.EegPacketReorderingEnabled is not { } enabled
        || run.EegReceivedPacketCount is not { } received
        || run.EegLostPacketCount is not { } lost
            ? null
            : new EegPacketStatistics(
                enabled,
                received,
                lost,
                run.EegOutOfOrderPacketCount ?? 0,
                run.EegDuplicatePacketCount ?? 0,
                run.EegLateDiscardedPacketCount ?? 0
            );

    public string CreateRandomSubjectCode()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt32(bytes) % 100_000_000;
        return value.ToString("D8", CultureInfo.InvariantCulture);
    }

    private static async Task<string> AllocateCodeAsync(
        AppDbContext db,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken
    )
    {
        var prefix = $"EXP-{scheduledAt:yyyyMMdd}-";
        var codes = await db
            .Experiments.AsNoTracking()
            .Where(x => x.ExperimentCode.StartsWith(prefix))
            .Select(x => x.ExperimentCode)
            .ToListAsync(cancellationToken);
        var next =
            codes
                .Select(x => int.TryParse(x[prefix.Length..], out var index) ? index : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;
        return $"{prefix}{next:D3}";
    }
}
