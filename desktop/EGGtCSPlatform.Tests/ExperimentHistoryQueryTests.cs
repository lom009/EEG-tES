using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ExperimentHistoryQueryTests
{
    [Fact]
    public async Task QueryHistorySupportsStablePagingSearchAndCombinedFilters()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "eggtcs-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new TestContextFactory(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(
                        $"Data Source={Path.Combine(directory, "history-query.db")};Pooling=False"
                    )
                    .Options
            );
            await SeedHistoryAsync(factory);
            var service = new ExperimentPersistenceService(
                factory,
                new CurrentOperatorContext(),
                new EegPhysicalChannelMappingService(Path.Combine(directory, "mapping.json"))
            );

            var secondPage = await service.QueryHistoryAsync(
                new ExperimentHistoryQuery(PageNumber: 2, PageSize: 5)
            );

            Assert.Equal(15, secondPage.TotalCount);
            Assert.Equal(3, secondPage.TotalPages);
            Assert.Equal(2, secondPage.PageNumber);
            Assert.Equal(
                Enumerable.Range(6, 5).Reverse().Select(index => $"EXP-202609{index:00}-001"),
                secondPage.Items.Select(item => item.ExperimentCode)
            );

            var clampedLastPage = await service.QueryHistoryAsync(
                new ExperimentHistoryQuery(PageNumber: 99, PageSize: 5)
            );

            Assert.Equal(3, clampedLastPage.PageNumber);
            Assert.Equal(
                Enumerable.Range(1, 5).Reverse().Select(index => $"EXP-202609{index:00}-001"),
                clampedLastPage.Items.Select(item => item.ExperimentCode)
            );

            var searched = await service.QueryHistoryAsync(
                new ExperimentHistoryQuery(SearchText: "target-patient")
            );

            Assert.Equal("EXP-20260907-001", Assert.Single(searched.Items).ExperimentCode);

            var searchedByExperiment = await service.QueryHistoryAsync(
                new ExperimentHistoryQuery(SearchText: "0908-00")
            );

            Assert.Equal(
                "EXP-20260908-001",
                Assert.Single(searchedByExperiment.Items).ExperimentCode
            );

            var boundaryDate = await service.QueryHistoryAsync(
                new ExperimentHistoryQuery(
                    StartDate: new DateOnly(2026, 9, 5),
                    EndDate: new DateOnly(2026, 9, 5)
                )
            );

            Assert.Equal("EXP-20260905-001", Assert.Single(boundaryDate.Items).ExperimentCode);

            var filtered = await service.QueryHistoryAsync(
                new ExperimentHistoryQuery(
                    StimulusKind: "tdcs",
                    StartDate: new DateOnly(2026, 9, 5),
                    EndDate: new DateOnly(2026, 9, 12),
                    RunStatus: ExperimentRunStatus.InterruptedByUser,
                    PageSize: 10
                )
            );

            Assert.Equal(2, filtered.TotalCount);
            Assert.Equal(
                ["EXP-20260912-001", "EXP-20260906-001"],
                filtered.Items.Select(item => item.ExperimentCode)
            );
            Assert.All(filtered.Items, item => Assert.Equal(2, item.Runs.Count));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task SeedHistoryAsync(TestContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var createdAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var operatorEntity = new OperatorEntity
        {
            Username = "history-query",
            NormalizedUsername = "HISTORY-QUERY",
            PasswordSalt = [1],
            PasswordHash = [1],
            PasswordIterations = 1,
            CreatedAtUtc = createdAt,
        };
        var session = new ApplicationSessionEntity
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = createdAt,
            LastHeartbeatAtUtc = createdAt,
            ClosedAtUtc = createdAt.AddDays(20),
        };
        db.Operators.Add(operatorEntity);
        db.ApplicationSessions.Add(session);

        for (var index = 1; index <= 15; index++)
        {
            var scheduledAt = createdAt.AddDays(index - 1);
            var subjectCode = index == 7 ? "TARGET-PATIENT" : $"SUB-{index:000}";
            var experiment = new ExperimentEntity
            {
                ExperimentCode = $"EXP-202609{index:00}-001",
                ScheduledAt = scheduledAt,
                Remarks = string.Empty,
                Status = ExperimentStatus.Completed,
                CreatedAtUtc = scheduledAt,
                UpdatedAtUtc = scheduledAt,
                Operator = operatorEntity,
                Subject = new SubjectEntity
                {
                    SubjectCode = subjectCode,
                    NormalizedSubjectCode = subjectCode.ToUpperInvariant(),
                    CreatedAtUtc = scheduledAt,
                },
                StimulusParadigm = new StimulusParadigmEntity
                {
                    Kind = index % 2 == 0 ? "TDcs" : "TAcs",
                    ArrayMode = "DualChannel",
                    Direction = "Positive",
                    ShamMode = "Direct",
                },
                Electrodes = [new ExperimentElectrodeEntity { SiteId = "Cz", IsStimulus = true }],
            };
            experiment.Runs.Add(
                CreateRun(
                    experiment,
                    session,
                    index % 3 == 0
                        ? ExperimentRunStatus.InterruptedByUser
                        : ExperimentRunStatus.Completed,
                    scheduledAt
                )
            );
            if (index % 3 == 0)
            {
                experiment.Runs.Add(
                    CreateRun(
                        experiment,
                        session,
                        ExperimentRunStatus.Completed,
                        scheduledAt.AddMinutes(5)
                    )
                );
            }
            db.Experiments.Add(experiment);
        }
        await db.SaveChangesAsync();
    }

    private static ExperimentRunEntity CreateRun(
        ExperimentEntity experiment,
        ApplicationSessionEntity session,
        ExperimentRunStatus status,
        DateTimeOffset createdAt
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            Experiment = experiment,
            ApplicationSession = session,
            Status = status,
            RunMode = "Manual",
            SampleRateHz = 500,
            CreatedAtUtc = createdAt,
            StartedAtUtc = createdAt,
            EndedAtUtc = createdAt.AddMinutes(1),
            LastHeartbeatAtUtc = createdAt.AddMinutes(1),
        };

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
