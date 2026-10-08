using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SimulationGenerationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(40.1)]
    [InlineData(100)]
    public async Task PulseConfigurationRoundTripsWithinSupportedRange(double frequency)
    {
        await using var fixture = await Fixture.Create();
        var template = fixture.Request.Template;
        template = template with
        {
            Timing = template.Timing with { StimulationMilliseconds = 1000 },
            StimulusConfiguration = template.StimulusConfiguration with
            {
                Kind = StimulusKind.TPcs,
                Envelope = null,
                Frequency = frequency,
            },
        };
        var path = Path.Combine(fixture.Root, "pulse.expp");
        await fixture.Serializer.WriteAsync(path, template);
        var imported = await fixture.Serializer.ReadAsync(path);
        Assert.Equal(frequency, imported.StimulusConfiguration.Frequency);
        Assert.Equal(
            template.StimulusConfiguration.RampSeconds,
            imported.StimulusConfiguration.RampSeconds
        );
        Assert.Equal(
            template.StimulusConfiguration.DutyPercent,
            imported.StimulusConfiguration.DutyPercent
        );
    }

    [Theory]
    [InlineData("frequencyHz", 100.1, "频率")]
    [InlineData("frequencyHz", 40.05, "频率")]
    [InlineData("rampSeconds", 31, "设备协议兼容性字段无效")]
    [InlineData("dutyPercent", 0, "设备协议兼容性字段无效")]
    public async Task ImportRejectsInvalidEffectiveOrWireFieldsWithoutRewritingFile(
        string field,
        double value,
        string message
    )
    {
        await using var fixture = await Fixture.Create();
        var template = fixture.Request.Template;
        template = template with
        {
            StimulusConfiguration = template.StimulusConfiguration with
            {
                Kind = StimulusKind.TAcs,
                Envelope = null,
            },
        };
        var path = Path.Combine(fixture.Root, "invalid.expp");
        await fixture.Serializer.WriteAsync(path, template);
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["stimulus"]![field] = value;
        var original = document.ToJsonString();
        await File.WriteAllTextAsync(path, original);
        var error = await Assert.ThrowsAsync<ExperimentPackageValidationException>(() =>
            fixture.Serializer.ReadAsync(path)
        );
        Assert.Contains(message, error.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(100.1)]
    [InlineData(1500)]
    public async Task PulseImportAndGeneratorRejectOutOfRangeFrequency(double frequency)
    {
        await using var fixture = await Fixture.Create();
        var template = fixture.Request.Template;
        template = template with
        {
            StimulusConfiguration = template.StimulusConfiguration with
            {
                Kind = StimulusKind.TPcs,
                Envelope = null,
                Frequency = 100,
            },
        };
        var path = Path.Combine(fixture.Root, "pulse-invalid.expp");
        await fixture.Serializer.WriteAsync(path, template);
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["stimulus"]!["frequencyHz"] = frequency;
        await File.WriteAllTextAsync(path, document.ToJsonString());
        var error = await Assert.ThrowsAsync<ExperimentPackageValidationException>(() =>
            fixture.Serializer.ReadAsync(path)
        );
        Assert.Contains("频率", error.Message);
        var invalid = template with
        {
            StimulusConfiguration = template.StimulusConfiguration with { Frequency = frequency },
        };
        Assert.Throws<ExperimentPackageValidationException>(() =>
            fixture.Generator.Validate(fixture.Request with { Template = invalid })
        );
    }

    [Fact]
    public async Task GeneratorKeepsIndependentEditsAndOrdinaryValidation()
    {
        await using var fixture = await Fixture.Create();
        var vm = new SimulationGeneratorViewModel(
            fixture.Generator,
            fixture.Mapping,
            StimulusCapabilityProfile.Default
        );
        vm.Frequency = 77.7;
        Assert.False(vm.ParameterPolicy.ShowRamp);
        Assert.False(vm.ParameterPolicy.ShowDutyPercent);
        Assert.False(vm.ShowShamMode);
        vm.SelectedKind = vm.Kinds.Single(x => x.Kind == StimulusKind.EnvelopeTAcs);
        vm.Frequency = 900;
        vm.RampSeconds = 59.5;
        Assert.True(vm.ParameterPolicy.ShowRamp);
        Assert.False(vm.ParameterPolicy.ShowDutyPercent);
        fixture.Generator.Validate(vm.CreateRequest());
        vm.SelectedKind = vm.Kinds.Single(x => x.Kind == StimulusKind.TAcs);
        Assert.Equal(77.7, vm.Frequency);
        Assert.Equal(7, vm.RampSeconds);
        vm.Frequency = 100.1;
        Assert.Throws<ExperimentPackageValidationException>(() =>
            fixture.Generator.Validate(vm.CreateRequest())
        );
        vm.SelectedKind = vm.Kinds.Single(x => x.Kind == StimulusKind.TPcs);
        Assert.Equal(1, vm.ParameterPolicy.Frequency.Minimum);
        Assert.Equal(100, vm.ParameterPolicy.Frequency.Maximum);
        vm.Frequency = 100;
        vm.DutyPercent = 1;
        fixture.Generator.Validate(vm.CreateRequest());
        vm.DutyPercent = 1.5;
        Assert.Throws<ExperimentPackageValidationException>(() =>
            fixture.Generator.Validate(vm.CreateRequest())
        );
        vm.SelectedKind = vm.Kinds.Single(x => x.Kind == StimulusKind.EnvelopeTAcs);
        Assert.Equal(900, vm.Frequency);
        Assert.Equal(59.5, vm.RampSeconds);
        vm.SelectedKind = vm.Kinds.Single(x => x.Kind == StimulusKind.Sham);
        vm.ShamMode = ShamWaveformMode.Direct;
        vm.RampSeconds = 15;
        Assert.True(vm.ShowShamMode);
        Assert.True(vm.ParameterPolicy.ShowRamp);
        Assert.Throws<ExperimentPackageValidationException>(() =>
            fixture.Generator.Validate(vm.CreateRequest())
        );
        vm.ShamMode = ShamWaveformMode.Alternating;
        Assert.False(vm.ParameterPolicy.ShowRamp);
        Assert.True(vm.ParameterPolicy.ShowFrequency);
        vm.Frequency = 88;
        vm.ShamMode = ShamWaveformMode.Direct;
        Assert.Equal(15, vm.RampSeconds);
        vm.ShamMode = ShamWaveformMode.Alternating;
        Assert.Equal(88, vm.Frequency);
    }

    [Fact]
    public async Task RandomIntervalsStayWithinBoundsAndPersistConsistentTimestamps()
    {
        await using var fixture = await Fixture.Create();
        var request = fixture.Request with { Count = 6, IntervalRange = new(3.5, 7.25) };
        var starts = SimulationGenerationService.BuildStartTimes(request);
        Assert.Equal(request.StartedAt, starts[0]);
        var gaps = starts.Zip(starts.Skip(1), (a, b) => (b - a).TotalMinutes).ToArray();
        Assert.All(gaps, gap => Assert.InRange(gap, 3.5, 7.25));
        Assert.True(gaps.Distinct().Count() > 1);
        Assert.Equal(starts, SimulationGenerationService.BuildStartTimes(request));
        Assert.NotEqual(
            starts[1],
            SimulationGenerationService.BuildStartTimes(request with { Seed = request.Seed + 1 })[1]
        );
        var result = await fixture.Generator.GenerateAsync(request);
        Assert.Null(result.Error);
        Assert.Equal(6, result.RunIds.Count);
        await using var reader = new FileEegRawPacketStore(fixture.Recordings);
        await using var db = fixture.Factory.CreateDbContext();
        for (var i = 0; i < result.RunIds.Count; i++)
        {
            var runId = result.RunIds[i];
            var run = await db
                .ExperimentRuns.Include(x => x.Experiment)
                .Include(x => x.Events)
                .SingleAsync(x => x.Id == runId);
            Assert.Equal(starts[i], run.StartedAtUtc);
            Assert.Equal(starts[i], run.Experiment.ScheduledAt);
            Assert.Equal(starts[i], run.Events.OrderBy(x => x.Sequence).First().StartedAtUtc);
            Assert.Equal(starts[i], (await reader.GetSummaryAsync(runId))!.Metadata.StartedAtUtc);
        }
        var fixedRequest = request with { IntervalRange = null, IntervalMinutes = 5 };
        var fixedStarts = SimulationGenerationService.BuildStartTimes(fixedRequest);
        Assert.Equal(request.StartedAt.AddMinutes(25), fixedStarts[^1]);
        Assert.Equal(
            fixedStarts,
            SimulationGenerationService.BuildStartTimes(request with { IntervalRange = new(5, 5) })
        );
        Assert.All(
            SimulationGenerationService.BuildStartTimes(request with { IntervalRange = new(0, 0) }),
            time => Assert.Equal(request.StartedAt, time)
        );
    }

    [Fact]
    public async Task DailyScheduleMovesNightAndWeekendCandidatesToNextAllowedWindow()
    {
        await using var fixture = await Fixture.Create();
        var friday = new DateTimeOffset(2026, 9, 11, 18, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(DayOfWeek.Friday, friday.DayOfWeek);
        var request = fixture.Request with
        {
            Count = 3,
            StartedAt = friday,
            IntervalMinutes = 60,
            IntervalRange = null,
            GenerationSchedule = null,
        };

        var starts = SimulationGenerationService.BuildStartTimes(request);

        Assert.Equal(friday, starts[0]);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 9, 30, 0, TimeSpan.FromHours(8)), starts[1]);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.FromHours(8)), starts[2]);
        Assert.Equal(starts, SimulationGenerationService.BuildStartTimes(request));

        var beforeWindow = SimulationGenerationService.BuildStartTimes(
            request with
            {
                Count = 1,
                StartedAt = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.FromHours(8)),
            }
        );
        Assert.Equal(
            new DateTimeOffset(2026, 9, 11, 9, 30, 0, TimeSpan.FromHours(8)),
            beforeWindow[0]
        );

        var weekends = new DailyGenerationSchedule(
            new TimeOnly(9, 30),
            new TimeOnly(18, 30),
            GenerationWeekdays.Weekend
        );
        var weekendStart = SimulationGenerationService.BuildStartTimes(
            request with
            {
                Count = 1,
                StartedAt = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(8)),
                GenerationSchedule = weekends,
            }
        );
        Assert.Equal(
            new DateTimeOffset(2026, 9, 12, 9, 30, 0, TimeSpan.FromHours(8)),
            weekendStart[0]
        );
    }

    [Fact]
    public async Task DailyScheduleRequiresWholeRunToFitAndRejectsInvalidConfiguration()
    {
        await using var fixture = await Fixture.Create();
        var oneHourTemplate = fixture.Request.Template with
        {
            Timing = new ExperimentTimingTemplate(
                ExperimentRunMode.Manual,
                30 * 60 * 1000,
                0,
                0,
                0,
                1
            ),
        };
        var exactFit = fixture.Request with
        {
            Template = oneHourTemplate,
            StartedAt = new DateTimeOffset(2026, 9, 11, 17, 30, 0, TimeSpan.FromHours(8)),
        };

        Assert.Equal(
            exactFit.StartedAt,
            Assert.Single(SimulationGenerationService.BuildStartTimes(exactFit))
        );
        var tooLate = SimulationGenerationService.BuildStartTimes(
            exactFit with
            {
                StartedAt = exactFit.StartedAt.AddSeconds(1),
            }
        );
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 9, 30, 0, TimeSpan.FromHours(8)), tooLate[0]);

        Assert.Throws<ArgumentException>(() =>
            SimulationGenerationService.BuildStartTimes(
                exactFit with
                {
                    GenerationSchedule = new DailyGenerationSchedule(
                        new TimeOnly(9, 30),
                        new TimeOnly(18, 30),
                        GenerationWeekdays.None
                    ),
                }
            )
        );
        Assert.Throws<ArgumentException>(() =>
            SimulationGenerationService.BuildStartTimes(
                exactFit with
                {
                    GenerationSchedule = new DailyGenerationSchedule(
                        new TimeOnly(18, 30),
                        new TimeOnly(9, 30),
                        GenerationWeekdays.Weekdays
                    ),
                }
            )
        );
        Assert.Throws<ArgumentException>(() =>
            SimulationGenerationService.BuildStartTimes(
                exactFit with
                {
                    Template = oneHourTemplate with
                    {
                        Timing = oneHourTemplate.Timing with
                        {
                            AcquisitionMilliseconds = 5 * 60 * 60 * 1000,
                        },
                    },
                }
            )
        );
    }

    [Fact]
    public async Task MultipleDailyTimeRangesAreNormalizedAndCandidatesUseTheNextFittingRange()
    {
        await using var fixture = await Fixture.Create();
        var oneHourTemplate = fixture.Request.Template with
        {
            Timing = new ExperimentTimingTemplate(
                ExperimentRunMode.Manual,
                30 * 60 * 1000,
                0,
                0,
                0,
                1
            ),
        };
        var schedule = new DailyGenerationSchedule(
            [
                new(new TimeOnly(13, 30), new TimeOnly(18, 30)),
                new(new TimeOnly(9, 30), new TimeOnly(12, 0)),
                new(new TimeOnly(11, 30), new TimeOnly(12, 30)),
                new(new TimeOnly(12, 30), new TimeOnly(13, 0)),
                new(new TimeOnly(13, 30), new TimeOnly(18, 30)),
            ],
            GenerationWeekdays.Weekdays
        );

        Assert.Collection(
            schedule.TimeRanges,
            range =>
            {
                Assert.Equal(new TimeOnly(9, 30), range.Start);
                Assert.Equal(new TimeOnly(13, 0), range.End);
            },
            range =>
            {
                Assert.Equal(new TimeOnly(13, 30), range.Start);
                Assert.Equal(new TimeOnly(18, 30), range.End);
            }
        );

        var friday = new DateTimeOffset(2026, 9, 11, 12, 30, 1, TimeSpan.FromHours(8));
        var request = fixture.Request with
        {
            Count = 2,
            Template = oneHourTemplate,
            StartedAt = friday,
            IntervalMinutes = 300,
            IntervalRange = null,
            GenerationSchedule = schedule,
        };
        var starts = SimulationGenerationService.BuildStartTimes(request);

        Assert.Equal(new DateTimeOffset(2026, 9, 11, 13, 30, 0, TimeSpan.FromHours(8)), starts[0]);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 9, 30, 0, TimeSpan.FromHours(8)), starts[1]);

        var exactEnd = SimulationGenerationService.BuildStartTimes(
            request with
            {
                Count = 1,
                StartedAt = new DateTimeOffset(2026, 9, 11, 17, 30, 0, TimeSpan.FromHours(8)),
            }
        );
        Assert.Equal(
            new DateTimeOffset(2026, 9, 11, 17, 30, 0, TimeSpan.FromHours(8)),
            exactEnd[0]
        );

        var persistedRequest = fixture.Request with
        {
            Count = 1,
            StartedAt = new DateTimeOffset(2026, 9, 11, 13, 10, 0, TimeSpan.FromHours(8)),
            GenerationSchedule = schedule,
        };
        var persistedStart = Assert.Single(
            SimulationGenerationService.BuildStartTimes(persistedRequest)
        );
        var result = await fixture.Generator.GenerateAsync(persistedRequest);
        var runId = Assert.Single(result.RunIds);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var run = await db
                .ExperimentRuns.Include(x => x.Experiment)
                .Include(x => x.Events)
                .SingleAsync(x => x.Id == runId);
            Assert.Equal(persistedStart, run.StartedAtUtc);
            Assert.Equal(persistedStart, run.Experiment.ScheduledAt);
            Assert.Equal(persistedStart, run.Events.OrderBy(x => x.Sequence).First().StartedAtUtc);
        }
        await using (var reader = new FileEegRawPacketStore(fixture.Recordings))
            Assert.Equal(
                persistedStart,
                (await reader.GetSummaryAsync(runId))!.Metadata.StartedAtUtc
            );

        var ninetyMinuteTemplate = oneHourTemplate with
        {
            Timing = oneHourTemplate.Timing with { AcquisitionMilliseconds = 45 * 60 * 1000 },
        };
        Assert.Throws<ArgumentException>(() =>
            SimulationGenerationService.BuildStartTimes(
                request with
                {
                    Count = 1,
                    Template = ninetyMinuteTemplate,
                    GenerationSchedule = new DailyGenerationSchedule(
                        [
                            new(new TimeOnly(9, 0), new TimeOnly(10, 0)),
                            new(new TimeOnly(11, 0), new TimeOnly(12, 0)),
                        ],
                        GenerationWeekdays.Weekdays
                    ),
                }
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new DailyGenerationSchedule(
                Array.Empty<DailyGenerationTimeRange>(),
                GenerationWeekdays.Weekdays
            )
        );
    }

    [Fact]
    public async Task GeneratorViewModelUsesWeekdayDefaultsAndReportsAdjustedFirstStart()
    {
        await using var fixture = await Fixture.Create();
        var model = new SimulationGeneratorViewModel(
            fixture.Generator,
            fixture.Mapping,
            StimulusCapabilityProfile.Default
        )
        {
            StartedAt = "2026-09-13 02:00:00 +08:00",
        };

        var request = model.CreateRequest();

        var defaultRange = Assert.Single(request.EffectiveGenerationSchedule.TimeRanges);
        Assert.Equal(new TimeOnly(9, 30), defaultRange.Start);
        Assert.Equal(new TimeOnly(18, 30), defaultRange.End);
        Assert.Equal(
            GenerationWeekdays.Weekdays,
            request.EffectiveGenerationSchedule.AllowedWeekdays
        );
        Assert.Single(model.GenerationTimeRanges);
        Assert.False(
            model.RemoveGenerationTimeRangeCommand.CanExecute(model.GenerationTimeRanges[0])
        );
        model.AddGenerationTimeRangeCommand.Execute(null);
        Assert.Equal(2, model.GenerationTimeRanges.Count);
        Assert.True(
            model.RemoveGenerationTimeRangeCommand.CanExecute(model.GenerationTimeRanges[0])
        );
        model.GenerationTimeRanges[0].End = new TimeSpan(12, 0, 0);
        model.GenerationTimeRanges[1].Start = new TimeSpan(11, 0, 0);
        var normalizedRange = Assert.Single(
            model.CreateRequest().EffectiveGenerationSchedule.TimeRanges
        );
        Assert.Equal(new TimeOnly(9, 30), normalizedRange.Start);
        Assert.Equal(new TimeOnly(18, 30), normalizedRange.End);
        Assert.Equal(2, model.GenerationTimeRanges.Count);
        model.RemoveGenerationTimeRangeCommand.Execute(model.GenerationTimeRanges[1]);
        Assert.Single(model.GenerationTimeRanges);
        Assert.False(
            model.RemoveGenerationTimeRangeCommand.CanExecute(model.GenerationTimeRanges[0])
        );
        Assert.True(model.AllowMonday);
        Assert.True(model.AllowFriday);
        Assert.False(model.AllowSaturday);
        Assert.False(model.AllowSunday);

        model.ValidateConfigurationCommand.Execute(null);

        Assert.Contains(
            "首条已从 2026-09-13 02:00 +08:00 顺延至 2026-09-14 09:30 +08:00",
            model.Status
        );
        Assert.Contains("实际开始 2026-09-14 09:30 +08:00", model.Status);
    }

    [Fact]
    public async Task InvalidIntervalRangesFailBeforeWritingData()
    {
        await using var fixture = await Fixture.Create();
        foreach (
            var range in new[]
            {
                new RecordIntervalRange(10, 5),
                new(-1, 5),
                new(0, 525601),
                new(double.NaN, 5),
                new(0, double.PositiveInfinity),
            }
        )
            Assert.Throws<ArgumentException>(() =>
                fixture.Generator.Validate(fixture.Request with { IntervalRange = range })
            );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Generator.Validate(
                fixture.Request with
                {
                    Count = 2,
                    StartedAt = DateTimeOffset.MaxValue.AddDays(-1),
                    IntervalRange = new(1440, 1440),
                }
            )
        );
        Assert.False(Directory.Exists(fixture.Recordings));
    }

    [Fact]
    public async Task Randomized32PersistsCompleteGraphsAndReplaysAfterReopening()
    {
        await using var fixture = await Fixture.Create();
        var request = fixture.Request with { Count = 32, Randomized32 = true };
        var result = await fixture.Generator.GenerateAsync(request);
        Assert.Null(result.Error);
        Assert.False(result.Canceled);
        Assert.Equal(32, result.RunIds.Count);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
        var records = await db
            .Experiments.Include(x => x.Subject)
            .Include(x => x.Runs)
                .ThenInclude(x => x.Events)
            .Include(x => x.ImpedanceSnapshots)
            .Include(x => x.StimulusParadigm)
            .ToArrayAsync();
        Assert.Equal(32, records.Select(x => x.Subject.SubjectCode).Distinct().Count());
        Assert.Equal(
            16,
            records.Count(x =>
                SimulationJson.Read<EnvelopeParameters>(x.StimulusParadigm!.EnvelopeJson)!.IsSham
            )
        );
        Assert.Equal(
            16,
            records.Count(x =>
                !SimulationJson.Read<EnvelopeParameters>(x.StimulusParadigm!.EnvelopeJson)!.IsSham
            )
        );
        Assert.All(
            records,
            x =>
            {
                Assert.Equal(ExperimentStatus.Completed, x.Status);
                Assert.Equal(5, Assert.Single(x.Runs).Events.Count);
                Assert.NotEmpty(x.ImpedanceSnapshots);
                Assert.NotNull(x.StimulusParadigm!.EnvelopeJson);
                Assert.Null(x.GenerationJson);
            }
        );
        var history = fixture.History;
        Assert.Equal(32, (await history.QueryHistoryAsync(new())).TotalCount);
        var route = await history.LoadHistoricalRunAsync(result.RunIds[0]);
        var context = route.HistoricalResult!;
        Assert.Equal(StimulusKind.EnvelopeTAcs, route.StimulusConfiguration.Kind);
        Assert.NotNull(route.StimulusConfiguration.Envelope);
        Assert.Equal(request.StartedAt, context.StartedAtUtc);
        var template = await history.LoadTemplateAsync(route.ExperimentDatabaseId);
        Assert.Equal(route.StimulusConfiguration.Envelope, template.StimulusConfiguration.Envelope);
        Assert.Null(template.Simulation);
        Assert.Null(context.Simulation);
        await using var reader = new FileEegRawPacketStore(fixture.Recordings);
        var data = await new EegHistoryWindowProvider(reader).LoadAsync(
            new(
                context.RunId,
                0,
                .04,
                250,
                new(null, null, null),
                context.PhysicalChannelNames,
                1,
                context.LogicalTimelineEndSeconds!.Value,
                RawFilePath: context.RawFilePath
            )
        );
        var first = data.Channels.First(x => x.ChannelId == "C3").Samples.First();
        var parameters = new SimulationProvenance(
            result.BatchId,
            request.Seed,
            1,
            "",
            DateTimeOffset.UtcNow,
            EegAmplitudeMicrovolts: request.EegAmplitudeMicrovolts,
            NoiseMicrovolts: request.NoiseMicrovolts,
            RhythmHz: request.RhythmHz
        );
        Assert.Equal(SimulationSignal.Eeg(parameters, 1, 0, 250), first.Value, 8);
        var gap = await new EegHistoryWindowProvider(reader).LoadAsync(
            new(
                context.RunId,
                .06,
                .10,
                250,
                new(null, null, null),
                context.PhysicalChannelNames,
                1,
                context.LogicalTimelineEndSeconds.Value,
                RawFilePath: context.RawFilePath
            )
        );
        Assert.All(gap.Channels, channel => Assert.Empty(channel.Samples));
        var repeated = await fixture.Generator.GenerateAsync(
            request with
            {
                Count = 1,
                Randomized32 = false,
            }
        );
        Assert.Null(repeated.Error);
        var nextRoute = await history.LoadHistoricalRunAsync(repeated.RunIds[0]);
        Assert.NotEqual(route.SubjectId, nextRoute.SubjectId);
        var nextContext = nextRoute.HistoricalResult!;
        var repeatedData = await new EegHistoryWindowProvider(reader).LoadAsync(
            new(
                nextContext.RunId,
                0,
                .04,
                250,
                new(null, null, null),
                nextContext.PhysicalChannelNames,
                1,
                nextContext.LogicalTimelineEndSeconds!.Value,
                RawFilePath: nextContext.RawFilePath
            )
        );
        Assert.Equal(
            first.Value,
            repeatedData.Channels.First(x => x.ChannelId == "C3").Samples.First().Value,
            8
        );
    }

    [Fact]
    public async Task CancellationRetainsCompletedCaseAndRemovesCurrentFiles()
    {
        await using var fixture = await Fixture.Create();
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress<SimulationProgress>(p =>
        {
            if (p.Completed == 1)
                cancel.Cancel();
        });
        var result = await fixture.Generator.GenerateAsync(
            fixture.Request with
            {
                Count = 3,
            },
            progress,
            cancel.Token
        );
        Assert.True(result.Canceled);
        Assert.Single(result.RunIds);
        Assert.Single(Directory.GetFiles(fixture.Recordings, "*.eegraw"));
        using var during = new CancellationTokenSource();
        var partial = await fixture.Generator.GenerateAsync(
            fixture.Request,
            new ImmediateProgress<SimulationProgress>(_ => during.Cancel()),
            during.Token
        );
        Assert.True(partial.Canceled);
        Assert.Empty(partial.RunIds);
        Assert.Single(Directory.GetFiles(fixture.Recordings, "*.eegraw"));
        Assert.Empty(Directory.GetFiles(fixture.Recordings, "*.tmp"));
    }

    [Fact]
    public async Task OfflineWriterHandlesMoreBlocksThanQueueCapacityAndFinalCycleMatchesLiveRuns()
    {
        await using var fixture = await Fixture.Create();
        var template = fixture.Request.Template;
        var request = fixture.Request with
        {
            Template = template with
            {
                Timing = template.Timing with { AcquisitionMilliseconds = 20000, CycleCount = 2 },
            },
        };
        var result = await fixture.Generator.GenerateAsync(request);
        Assert.Null(result.Error);
        var route = await fixture.History.LoadHistoricalRunAsync(Assert.Single(result.RunIds));
        Assert.Equal(3, route.HistoricalResult!.StageIntervals.Last().Cycle);
        Assert.Equal(9, route.HistoricalResult.StageIntervals.Count);
        await using var reader = new FileEegRawPacketStore(fixture.Recordings);
        var summary = await reader.GetSummaryAsync(result.RunIds[0]);
        Assert.Equal(60, summary!.SampleBatchCount);
        Assert.Empty(Directory.GetFiles(fixture.Recordings, "*.tmp"));
    }

    [Fact]
    public async Task InvalidConfigurationsAreRejectedBeforeCreatingFiles()
    {
        await using var fixture = await Fixture.Create();
        var request = fixture.Request;
        var template = request.Template;
        var invalidEnvelope = template.StimulusConfiguration with { Envelope = new(Depth: 1.1) };
        Assert.Throws<ExperimentPackageValidationException>(() =>
            fixture.Generator.Validate(
                request with
                {
                    Template = template with { StimulusConfiguration = invalidEnvelope },
                }
            )
        );
        Assert.Throws<ExperimentPackageValidationException>(() =>
            fixture.Generator.Validate(
                request with
                {
                    PhysicalChannels = [new("C3", 1), new("C4", 1)],
                }
            )
        );
        Assert.Throws<ArgumentException>(() =>
            fixture.Generator.Validate(request with { Randomized32 = true })
        );
        Assert.Throws<ExperimentPackageValidationException>(() =>
            fixture.Generator.Validate(
                request with
                {
                    Template = template with
                    {
                        Timing = template.Timing with
                        {
                            Mode = ExperimentRunMode.Manual,
                            CycleCount = 2,
                        },
                    },
                }
            )
        );
        Assert.False(Directory.Exists(fixture.Recordings));
    }

    [Fact]
    public async Task FailedDatabaseCommitAndFileCreationLeaveNoSuccessfulHistory()
    {
        await using var fixture = await Fixture.Create();
        fixture.Session.Id = Guid.NewGuid(); // Invalid foreign key fails the database commit after file completion.
        var failed = await fixture.Generator.GenerateAsync(fixture.Request);
        Assert.NotNull(failed.Error);
        Assert.Empty(failed.RunIds);
        Assert.Empty(Directory.GetFiles(fixture.Recordings));
        Assert.Empty((await fixture.History.ListHistoryAsync()));
        var blockedPath = Path.Combine(fixture.Root, "blocked-file");
        await File.WriteAllTextAsync(blockedPath, "not a directory");
        var failingWriter = new SimulationGenerationService(
            fixture.Factory,
            fixture.Operator,
            fixture.Session,
            fixture.Mapping,
            fixture.Serializer,
            blockedPath
        );
        var fileFailed = await failingWriter.GenerateAsync(fixture.Request);
        Assert.NotNull(fileFailed.Error);
        Assert.Empty(fileFailed.RunIds);
        Assert.Empty(await fixture.History.ListHistoryAsync());
    }

    [Theory]
    [InlineData(StimulusKind.TDcs)]
    [InlineData(StimulusKind.TAcs)]
    [InlineData(StimulusKind.TRns)]
    [InlineData(StimulusKind.TPcs)]
    [InlineData(StimulusKind.Sham)]
    public async Task OrdinaryParadigmsRetainConfigurationAndExportNeutralMetadata(
        StimulusKind kind
    )
    {
        await using var fixture = await Fixture.Create();
        var t = fixture.Request.Template;
        var request = fixture.Request with
        {
            Template = t with
            {
                StimulusConfiguration = t.StimulusConfiguration with
                {
                    Kind = kind,
                    Envelope = null,
                    RampSeconds = 0,
                },
            },
        };
        var result = await fixture.Generator.GenerateAsync(request);
        Assert.Null(result.Error);
        var route = await fixture.History.LoadHistoricalRunAsync(result.RunIds[0]);
        Assert.Equal(kind, route.StimulusConfiguration.Kind);
        Assert.Equal(t.Timing, route.ImportedTiming);
        await using var reader = new FileEegRawPacketStore(fixture.Recordings);
        var exporter = new EegExportService(
            fixture.Factory,
            fixture.History,
            fixture.Serializer,
            new NoReveal(),
            new EegArtifactFinalizer(),
            reader
        );
        var output = await exporter.ExportAsync(
            result.RunIds[0],
            new(true, true, true),
            Path.Combine(fixture.Root, "exports"),
            revealResult: false
        );
        Assert.Equal(3, output.Paths.Count);
        foreach (var path in output.Paths)
        {
            AssertNeutralMetadata(Path.GetFileName(path));
            AssertNeutralMetadata(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        }
        var packagePath = output.Paths.Single(x => x.EndsWith(".expp"));
        Assert.DoesNotContain("generationParameters", await File.ReadAllTextAsync(packagePath));
        var imported = await fixture.Serializer.ReadAsync(packagePath);
        Assert.Null(imported.Simulation);
        var summary = await reader.GetSummaryAsync(result.RunIds[0]);
        AssertNeutralMetadata(SimulationJson.Write(summary!.Metadata));
        Assert.Null(summary.Metadata.Simulation);
        Assert.DoesNotContain("generationParameters", SimulationJson.Write(summary.Metadata));
        AssertNeutralMetadata(
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(summary.FilePath))
        );
        await using var db = fixture.Factory.CreateDbContext();
        var record = await db.Experiments.SingleAsync(x => x.Id == route.ExperimentDatabaseId);
        Assert.Null(record.GenerationJson);
        AssertNeutralMetadata(record.Remarks ?? "");
        Assert.StartsWith("EXP-", record.ExperimentCode);
        Assert.DoesNotContain(
            "DataSource",
            await File.ReadAllTextAsync(output.Paths.Single(x => x.EndsWith(".csv")))
        );
        Assert.DoesNotContain(
            "BatchId",
            await File.ReadAllTextAsync(output.Paths.Single(x => x.EndsWith(".csv")))
        );
    }

    private static void AssertNeutralMetadata(string text)
    {
        Assert.DoesNotContain("模拟", text);
        Assert.DoesNotContain("simulation", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("simulated", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnvelopeExportAndHardwareMappingAreExplicitlyBlocked()
    {
        await using var fixture = await Fixture.Create();
        await Assert.ThrowsAsync<ExperimentPackageValidationException>(() =>
            fixture.Serializer.WriteAsync(
                Path.Combine(fixture.Root, "envelope.expp"),
                fixture.Request.Template
            )
        );
        var source = ExperimentStimulusConfigurationSnapshot.From(
            fixture.Request.Template.StimulusConfiguration,
            fixture.Request.Template.StimulusElectrodes
        );
        await using var device = new EGGtCSPlatform.DeviceSdk.Simulation.SimulatedEggtCsDevice();
        Assert.Throws<InvalidOperationException>(() =>
            DeviceStimulationConfigurationMapper.Create(
                device,
                source,
                fixture.Request.Template.StimulusElectrodes
            )
        );
        Assert.Equal(16, SimulationSignal.Allocate(42).Count(x => x));
        Assert.Equal(SimulationSignal.Allocate(42), SimulationSignal.Allocate(42));
        Assert.NotEqual(SimulationSignal.Allocate(42), SimulationSignal.Allocate(43));
        Assert.Equal(
            0,
            SimulationSignal.Current(
                source with
                {
                    Envelope = source.Envelope! with { IsSham = true },
                    RampSeconds = 1,
                },
                5,
                10
            )
        );
        Assert.True(
            SimulationGeneratorController.IsOpenGesture(
                Key.G,
                KeyModifiers.Control | KeyModifiers.Shift
            )
        );
        Assert.False(SimulationGeneratorController.IsOpenGesture(Key.G, KeyModifiers.Control));
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class NoReveal : IFileRevealService
    {
        public void Reveal(string path) { }
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class Mapping : IEegPhysicalChannelMappingService
    {
        public EegPhysicalChannelMappingSnapshot Load() =>
            new(32, [new("C3", 1), new("C4", 2)], []);

        public void Save(int count, IReadOnlyList<EegPhysicalChannelMapping> values) =>
            throw new NotSupportedException();

        public string StoragePath => "memory";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } =
            Path.Combine(
                Path.GetTempPath(),
                "eggtcs-generator-tests",
                Guid.NewGuid().ToString("N")
            );
        public string Recordings => Path.Combine(Root, "recordings");
        public Factory Factory { get; private set; } = null!;
        public Mapping Mapping { get; } = new();
        public CurrentOperatorContext Operator { get; } = new();
        public ApplicationSessionState Session { get; } = new() { Id = Guid.NewGuid() };
        public ExperimentPackageSerializer Serializer { get; private set; } = null!;
        public SimulationGenerationService Generator { get; private set; } = null!;
        public SimulationRequest Request { get; private set; } = null!;
        public ExperimentPersistenceService History => new(Factory, Operator, Mapping);

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Root);
            f.Factory = new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(
                        $"Data Source={Path.Combine(f.Root, "test.db")};Pooling=False;Foreign Keys=True"
                    )
                    .Options
            );
            await using var db = f.Factory.CreateDbContext();
            await db.Database.MigrateAsync();
            var op = new OperatorEntity
            {
                Username = "test",
                NormalizedUsername = "TEST",
                PasswordHash = [],
                PasswordSalt = [],
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Operators.Add(op);
            db.ApplicationSessions.Add(
                new() { Id = f.Session.Id, StartedAtUtc = DateTimeOffset.UtcNow }
            );
            await db.SaveChangesAsync();
            f.Operator.Set(op.Id, op.Username);
            f.Serializer = new(
                StimulusCapabilityProfile.Default,
                new ElectrodePositionCatalog(
                    new ElectrodePositionOptions
                    {
                        Positions = StimulusCapabilityProfile
                            .Default.StimulusSiteIds.Concat(new[] { "C3", "C4", "FCz", "AFz" })
                            .Distinct()
                            .Select(x => new ElectrodePositionDefinition(x, x, 100, 100))
                            .ToArray(),
                    }
                ),
                f.Mapping,
                new ApplicationVersionProvider()
            );
            f.Generator = new(
                f.Factory,
                f.Operator,
                f.Session,
                f.Mapping,
                f.Serializer,
                f.Recordings
            );
            var vm = new SimulationGeneratorViewModel(
                f.Generator,
                f.Mapping,
                StimulusCapabilityProfile.Default
            );
            vm.ApplyRandomizedPresetCommand.Execute(null);
            vm.Count = 1;
            vm.Randomized32 = false;
            vm.SampleRate = 250;
            vm.StartedAt = "2026-09-10 10:00:00 +08:00";
            vm.AcquisitionSeconds = .04;
            vm.BlankingSeconds = .04;
            vm.StimulationSeconds = .08;
            vm.RecoverySeconds = .04;
            f.Request = vm.CreateRequest();
            return f;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
