using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EGGtCSPlatform.Tests;

internal sealed class RecordingApplicationLogger : IApplicationLogger
{
    private readonly ConcurrentQueue<ApplicationLogEntry> _entries = new();
    public ApplicationLogEntry[] Entries => _entries.ToArray();

    public void Log(ApplicationLogEntry entry) => _entries.Enqueue(entry);

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class ApplicationLoggingTests
{
    [Fact]
    public async Task AuthenticationLogsOutcomesWithoutCredentials()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options
        );
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Operators.Add(
                PasswordHashing.CreateOperator(
                    "private-user",
                    "secret-password",
                    DateTimeOffset.UtcNow
                )
            );
            await db.SaveChangesAsync();
        }
        var logger = new RecordingApplicationLogger();
        var service = new AuthenticationService(factory, new CurrentOperatorContext(), logger);
        Assert.False(await service.SignInAsync("private-user", "wrong-password"));
        Assert.True(await service.SignInAsync("private-user", "secret-password"));
        Assert.Equal(
            new[] { "Login.Rejected", "Login.Succeeded" },
            logger.Entries.Select(x => x.EventName)
        );
        Assert.Equal(ApplicationLogLevel.Warning, logger.Entries[0].Level);
        Assert.NotNull(logger.Entries[1].CorrelationId);
        var text = string.Join("\n", logger.Entries.Select(x => x.ToString()));
        Assert.DoesNotContain("private-user", text);
        Assert.DoesNotContain("secret-password", text);
        Assert.DoesNotContain("wrong-password", text);
    }

    [Fact]
    public async Task AuthenticationInfrastructureFailureIsClassifiedWithoutProviderInput()
    {
        var logger = new RecordingApplicationLogger();
        var service = new AuthenticationService(
            new ThrowingFactory(),
            new CurrentOperatorContext(),
            logger
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SignInAsync("private-user", "secret-password")
        );
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Login.Failed", entry.EventName);
        Assert.Equal(ApplicationLogLevel.Error, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("secret-password", entry.Message);
    }

    [Fact]
    public void OperationFailureAndCancellationHaveExactlyOneFinalEvent()
    {
        var logger = new RecordingApplicationLogger();
        using (
            var operation = new LoggedOperation("test", "Export", "begin", "run-1", logger: logger)
        )
            operation.Fail(new InvalidOperationException("disk full"));
        using (
            var operation = new LoggedOperation("test", "Delete", "begin", "run-2", logger: logger)
        )
            operation.Fail(new OperationCanceledException());
        Assert.Equal(4, logger.Entries.Length);
        Assert.Equal("Export.Failed", logger.Entries[1].EventName);
        Assert.Equal("run-1", logger.Entries[1].CorrelationId);
        Assert.Equal("disk full", logger.Entries[1].Exception!.Message);
        Assert.Equal("Delete.Canceled", logger.Entries[3].EventName);
        Assert.Equal(ApplicationLogLevel.Info, logger.Entries[3].Level);
    }

    [Fact]
    public async Task SingleExportLogsWithoutExplorerAndBatchScopeSuppressesOnlyNestedExports()
    {
        var logger = new RecordingApplicationLogger();
        var service = new EegExportService(null!, null!, null!, null!, null!, null!, logger);
        var runId = Guid.NewGuid();
        var selection = new EegExportSelection(false, false, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportAsync(runId, selection, revealResult: false)
        );
        Assert.Equal(2, logger.Entries.Length);
        Assert.Equal("Export.Single.Failed", logger.Entries[1].EventName);
        Assert.Equal(runId.ToString(), logger.Entries[1].CorrelationId);
        using (new BatchExportLogScope())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ExportAsync(runId, selection)
            );
            Assert.Equal(2, logger.Entries.Length);
        }
        Assert.False(BatchExportLogScope.IsActive);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class ThrowingFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            throw new InvalidOperationException("provider input secret-password");
    }
}
