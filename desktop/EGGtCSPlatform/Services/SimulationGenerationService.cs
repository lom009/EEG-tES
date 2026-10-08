using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Services;

public sealed class SimulationGenerationService(
    IDbContextFactory<AppDbContext> contextFactory,
    ICurrentOperatorContext currentOperator,
    ApplicationSessionState session,
    IEegPhysicalChannelMappingService mappings,
    ExperimentPackageSerializer validator,
    string? recordingsDirectory = null
) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await _gate.WaitAsync();
        _gate.Release();
    }

    public void Validate(SimulationRequest request)
    {
        if (!currentOperator.IsAuthenticated)
            throw new InvalidOperationException("请先登录。");
        validator.ValidateSimulationTemplate(request.Template, request.PhysicalChannels);
        if (request.Count is < 1 or > 1000 || request.Randomized32 && request.Count != 32)
            throw new ArgumentException("生成数量为 1–1000；随机对照预设必须为32例。");
        if (
            string.IsNullOrWhiteSpace(request.SubjectPrefix)
            || request.SubjectPrefix.Length > 24
            || request.SubjectPrefix.Any(char.IsControl)
        )
            throw new ArgumentException("被试前缀须为 1–24 个可见字符。");
        if (
            !double.IsFinite(request.EegAmplitudeMicrovolts)
            || request.EegAmplitudeMicrovolts is <= 0 or > 1000
            || !double.IsFinite(request.NoiseMicrovolts)
            || request.NoiseMicrovolts is < 0 or > 1000
            || !double.IsFinite(request.RhythmHz)
            || request.RhythmHz is <= 0 or > 100
        )
            throw new ArgumentException("时间间隔或 EEG 信号参数超出范围。");
        if (
            request.Randomized32
            && request.Template.StimulusConfiguration.Kind != StimulusKind.EnvelopeTAcs
        )
            throw new ArgumentException("32例预设需要选择包络-tACS。");
        var duration = BuildStages(request.Template.Timing).Last().EndSeconds;
        if (duration > 86400)
            throw new ArgumentException("单例总时长不能超过24小时。");
        _ = BuildStartTimes(request)[^1].AddSeconds(duration);
        var map = (
            request.PhysicalChannels
            ?? request.Template.PhysicalChannels
            ?? mappings.Load().Mappings
        ).ToDictionary(
            x => x.ElectrodeId,
            x => x.PhysicalChannel,
            StringComparer.OrdinalIgnoreCase
        );
        if (
            request.Template.AcquisitionChannels.Any(x =>
                !map.TryGetValue(x, out var p) || p is null or <= 0
            )
        )
            throw new ArgumentException("所有采集电极必须具有有效物理通道映射。");
    }

    public async Task<SimulationResult> GenerateAsync(
        SimulationRequest request,
        IProgress<SimulationProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        Validate(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token
        );
        cancellationToken = linked.Token;
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("已有生成任务正在执行。");
        var batchId = Guid.NewGuid();
        var completed = new List<Guid>();
        try
        {
            var groups = SimulationSignal.Allocate(request.Seed);
            var startTimes = BuildStartTimes(request);
            var snapshot = (
                request.PhysicalChannels
                ?? request.Template.PhysicalChannels
                ?? mappings.Load().Mappings
            ).ToArray();
            for (var i = 0; i < request.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sham = request.Randomized32 && !groups[i];
                var source = new SimulationProvenance(
                    batchId,
                    request.Seed,
                    i + 1,
                    request.Randomized32
                        ? sham
                            ? "假刺激"
                            : "包络刺激"
                        : "自定义",
                    DateTimeOffset.UtcNow,
                    EegAmplitudeMicrovolts: request.EegAmplitudeMicrovolts,
                    NoiseMicrovolts: request.NoiseMicrovolts,
                    RhythmHz: request.RhythmHz
                );
                var template = request.Template;
                if (request.Randomized32)
                    template = template with
                    {
                        StimulusConfiguration = template.StimulusConfiguration with
                        {
                            Envelope = template.StimulusConfiguration.Envelope! with
                            {
                                IsSham = sham,
                            },
                        },
                    };
                var index = i;
                var runId = await GenerateOneAsync(
                    request,
                    template,
                    source,
                    snapshot,
                    startTimes[i],
                    percent =>
                        progress?.Report(
                            new SimulationProgress(
                                index,
                                request.Count,
                                $"第 {index + 1}/{request.Count} 例 · {source.Group} · {percent:0}%"
                            )
                        ),
                    cancellationToken
                );
                completed.Add(runId);
                progress?.Report(
                    new SimulationProgress(
                        completed.Count,
                        request.Count,
                        $"已完成 {completed.Count}/{request.Count} · {source.Group} · {runId}"
                    )
                );
            }
            return new SimulationResult(batchId, completed, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SimulationResult(batchId, completed, true, null);
        }
        catch (Exception e)
        {
            return new SimulationResult(batchId, completed, false, e.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Guid> GenerateOneAsync(
        SimulationRequest request,
        ExperimentConfigurationTemplate template,
        SimulationProvenance source,
        IReadOnlyList<EegPhysicalChannelMapping> map,
        DateTimeOffset start,
        Action<double> progress,
        CancellationToken token
    )
    {
        var id = Guid.NewGuid();
        var subject =
            $"{request.SubjectPrefix.Trim()}-{source.BatchId:N}-{source.SubjectIndex:000}";
        var code = $"EXP-{id:N}"[..32];
        var stages = BuildStages(template.Timing);
        var end = start.AddSeconds(stages.Last().EndSeconds);
        var channels = map.Where(x =>
                template.AcquisitionChannels.Contains(
                    x.ElectrodeId,
                    StringComparer.OrdinalIgnoreCase
                )
            )
            .ToDictionary(x => x.PhysicalChannel!.Value, x => x.ElectrodeId);
        // No file registry: publish the file index atomically with the completed database graph.
        var store = new FileEegRawPacketStore(
            recordingsDirectory ?? AppPaths.RecordingsDirectory,
            queueCapacity: 8,
            waitForSampleWrites: true
        );
        var committed = false;
        try
        {
            await store.BeginAsync(
                new EegRecordingMetadata(
                    id,
                    code,
                    subject,
                    "offline",
                    template.SampleRateHz,
                    template.AcquisitionChannels,
                    start,
                    new EegDisplayFilterSettings(null, null, null),
                    channels
                ),
                token
            );
            long sequence = 0;
            foreach (var stage in stages.Where(x => x.Stage == ExperimentRunStage.Acquisition))
            {
                var sampleCount = (long)
                    Math.Round((stage.EndSeconds - stage.StartSeconds) * template.SampleRateHz);
                for (long offset = 0; offset < sampleCount; offset += template.SampleRateHz)
                {
                    token.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(template.SampleRateHz, sampleCount - offset);
                    var firstSample =
                        (long)Math.Round(stage.StartSeconds * template.SampleRateHz) + offset;
                    var data = channels
                        .Keys.Select(channel => new EegRecordedChannelSamples(
                            channel,
                            Enumerable
                                .Range(0, count)
                                .Select(n =>
                                    SimulationSignal.Eeg(
                                        source,
                                        channel,
                                        firstSample + n,
                                        template.SampleRateHz
                                    )
                                )
                                .ToArray()
                        ))
                        .ToArray();
                    var seconds = stage.StartSeconds + offset / (double)template.SampleRateHz;
                    await store.AppendSamplesAsync(
                        new EegRecordedSampleBatch(
                            id,
                            ++sequence,
                            start.AddSeconds(seconds),
                            firstSample,
                            seconds,
                            template.SampleRateHz,
                            1d / template.SampleRateHz,
                            stage.Cycle,
                            "Acquisition",
                            data
                        ),
                        token
                    );
                    if (offset % (template.SampleRateHz * 5L) == 0)
                        progress(100 * seconds / stages.Last().EndSeconds);
                }
            }
            var summary = await store.CompleteAsync(
                id,
                EegRecordingCompletionStatus.Completed,
                token
            );
            if (
                !summary.IsComplete
                || summary.SampleBatchCount != sequence
                || !File.Exists(summary.FilePath)
            )
                throw new IOException("原始文件完整性检查失败。");
            var verified = await store.GetSummaryAsync(id, token);
            if (verified?.SampleBatchCount != sequence)
                throw new IOException("原始文件回读校验失败。");
            await using var db = await contextFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var experiment = new ExperimentEntity
            {
                ExperimentCode = code,
                ScheduledAt = start,
                Remarks = $"{source.Group} · {source.BatchId}",
                Status = ExperimentStatus.Completed,
                CreatedAtUtc = source.GeneratedAtUtc,
                UpdatedAtUtc = source.GeneratedAtUtc,
                OperatorId = currentOperator.OperatorId,
                Subject = new SubjectEntity
                {
                    SubjectCode = subject,
                    NormalizedSubjectCode = subject.ToUpperInvariant(),
                    CreatedAtUtc = source.GeneratedAtUtc,
                },
            };
            var impedance = template
                .AcquisitionChannels.Select(site => new ImpedanceSnapshotValue(
                    site,
                    ImpedanceMeasurementKind.Acquisition,
                    5,
                    true,
                    start
                ))
                .Concat(
                    template.StimulusElectrodes.Select(x => new ImpedanceSnapshotValue(
                        x.SiteId,
                        ImpedanceMeasurementKind.Stimulation,
                        5,
                        true,
                        start
                    ))
                )
                .ToArray();
            ExperimentPersistenceService.ApplyConfiguration(experiment, template, impedance, map);
            var timing = template.Timing;
            var run = new ExperimentRunEntity
            {
                Id = id,
                Experiment = experiment,
                ApplicationSessionId = session.Id,
                Status = ExperimentRunStatus.Completed,
                RunMode = timing.Mode.ToString(),
                CycleCount = timing.CycleCount,
                SampleRateHz = template.SampleRateHz,
                AcquisitionDurationMilliseconds = timing.AcquisitionMilliseconds,
                BlankingDurationMilliseconds = timing.BlankingMilliseconds,
                StimulationDurationMilliseconds = timing.StimulationMilliseconds,
                RecoveryDurationMilliseconds = timing.RecoveryMilliseconds,
                CreatedAtUtc = source.GeneratedAtUtc,
                StartedAtUtc = start,
                EndedAtUtc = end,
                LastHeartbeatAtUtc = end,
                LastKnownCycle = stages.Last().Cycle,
                LastKnownStage = "Acquisition",
                LastKnownElapsedMilliseconds = stages.Last().EndSeconds * 1000,
                EegReceivedPacketCount = sequence,
                EegLostPacketCount = 0,
                EegOutOfOrderPacketCount = 0,
                EegDuplicatePacketCount = 0,
                EegLateDiscardedPacketCount = 0,
                EegPacketReorderingEnabled = false,
                Events = stages
                    .Select(
                        (s, index) =>
                            new ExperimentEventEntity
                            {
                                Kind = Enum.Parse<ExperimentEventKind>(s.Stage.ToString()),
                                Cycle = s.Cycle,
                                Sequence = index + 1,
                                RequestedAtUtc = start.AddSeconds(s.StartSeconds),
                                StartedAtUtc = start.AddSeconds(s.StartSeconds),
                                EndedAtUtc = start.AddSeconds(s.EndSeconds),
                                TimelineStartMilliseconds = s.StartSeconds * 1000,
                                TimelineEndMilliseconds = s.EndSeconds * 1000,
                                Status = ExperimentEventStatus.Completed,
                                EndTimeAccuracy = EventEndTimeAccuracy.Exact,
                            }
                    )
                    .ToList(),
                Files =
                [
                    new EegFileEntity
                    {
                        Format = EegFileFormat.Staging,
                        Location = EegFileLocation.Managed,
                        State = EegFileState.Available,
                        Path = summary.FilePath,
                        SizeBytes = new FileInfo(summary.FilePath).Length,
                        CreatedAtUtc = source.GeneratedAtUtc,
                    },
                ],
            };
            db.ExperimentRuns.Add(run);
            await db.SaveChangesAsync(token);
            token.ThrowIfCancellationRequested();
            // Once committed, this case is retained even if cancellation arrives concurrently.
            await transaction.CommitAsync(CancellationToken.None);
            committed = true;
            return id;
        }
        finally
        {
            try
            {
                await store.DisposeAsync();
            }
            finally
            {
                if (!committed)
                    await store.DeleteAsync(id, CancellationToken.None);
            }
        }
    }

    public static DateTimeOffset[] BuildStartTimes(SimulationRequest request)
    {
        if (request.Count is < 1 or > 1000)
            throw new ArgumentException("生成数量为 1–1000。");
        var minimum = request.IntervalRange?.MinimumMinutes ?? request.IntervalMinutes;
        var maximum = request.IntervalRange?.MaximumMinutes ?? request.IntervalMinutes;
        if (
            !double.IsFinite(minimum)
            || !double.IsFinite(maximum)
            || minimum < 0
            || maximum > 525600
            || minimum > maximum
        )
            throw new ArgumentException("记录间隔须在 0–525600 分钟内，最小值不能大于最大值。");
        var schedule = request.EffectiveGenerationSchedule;
        var unknownWeekdays = schedule.AllowedWeekdays & ~GenerationWeekdays.All;
        if (schedule.AllowedWeekdays == GenerationWeekdays.None || unknownWeekdays != 0)
            throw new ArgumentException("请至少选择一个有效的允许生成日。");
        var durationSeconds = BuildStages(request.Template.Timing).Last().EndSeconds;
        if (
            !double.IsFinite(durationSeconds)
            || schedule.TimeRanges.All(range =>
                durationSeconds > (range.End - range.Start).TotalSeconds
            )
        )
            throw new ArgumentException("单例总时长必须能够完整放入至少一个每日允许时段。");

        var starts = new DateTimeOffset[request.Count];
        starts[0] = MoveIntoAllowedSchedule(request.StartedAt, durationSeconds, schedule);
        for (var i = 1; i < starts.Length; i++)
        {
            // A separate deterministic stream leaves group allocation and EEG unchanged.
            var fraction = (SimulationSignal.Noise(request.Seed ^ 0x163a9b25, 0, 1701, i) + 1) / 2;
            var interval = minimum == maximum ? minimum : minimum + (maximum - minimum) * fraction;
            var candidate = starts[i - 1].AddMinutes(interval);
            starts[i] = MoveIntoAllowedSchedule(candidate, durationSeconds, schedule);
        }
        return starts;
    }

    private static DateTimeOffset MoveIntoAllowedSchedule(
        DateTimeOffset candidate,
        double durationSeconds,
        DailyGenerationSchedule schedule
    )
    {
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var date = candidate.Date.AddDays(dayOffset);
            if (!schedule.Allows(date.DayOfWeek))
                continue;
            foreach (var range in schedule.TimeRanges)
            {
                var windowStart = new DateTimeOffset(
                    date + range.Start.ToTimeSpan(),
                    candidate.Offset
                );
                var windowEnd = new DateTimeOffset(date + range.End.ToTimeSpan(), candidate.Offset);
                var proposed = dayOffset == 0 && candidate > windowStart ? candidate : windowStart;
                if (proposed.AddSeconds(durationSeconds) <= windowEnd)
                    return proposed;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(candidate),
            "无法在日期范围内找到下一个允许生成时段。"
        );
    }

    public static IReadOnlyList<HistoricalExperimentStageInterval> BuildStages(
        ExperimentTimingTemplate timing
    )
    {
        var stages = new List<HistoricalExperimentStageInterval>();
        double offset = 0;
        void Add(ExperimentRunStage stage, int cycle, double milliseconds)
        {
            stages.Add(new(stage, cycle, offset, offset + milliseconds / 1000));
            offset += milliseconds / 1000;
        }
        var cycles = timing.Mode == ExperimentRunMode.Manual ? 1 : timing.CycleCount;
        for (var i = 1; i <= cycles; i++)
        {
            Add(ExperimentRunStage.Acquisition, i, timing.AcquisitionMilliseconds);
            Add(ExperimentRunStage.Blanking, i, timing.BlankingMilliseconds);
            Add(ExperimentRunStage.Stimulation, i, timing.StimulationMilliseconds);
            Add(ExperimentRunStage.Recovery, i, timing.RecoveryMilliseconds);
        }
        Add(ExperimentRunStage.Acquisition, cycles + 1, timing.AcquisitionMilliseconds);
        return stages;
    }
}
