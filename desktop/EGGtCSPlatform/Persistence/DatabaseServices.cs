using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Services;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Persistence;

public interface ICurrentOperatorContext
{
    long OperatorId { get; }
    string Username { get; }
    bool IsAuthenticated { get; }
    void Set(long operatorId, string username);
}

public sealed class CurrentOperatorContext : ICurrentOperatorContext
{
    public long OperatorId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public bool IsAuthenticated => OperatorId > 0;

    public void Set(long operatorId, string username)
    {
        if (operatorId <= 0)
            throw new ArgumentOutOfRangeException(nameof(operatorId));
        OperatorId = operatorId;
        Username = username;
    }
}

public interface IAuthenticationService
{
    Task<bool> SignInAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default
    );
}

public sealed class AuthenticationService(
    IDbContextFactory<AppDbContext> contextFactory,
    ICurrentOperatorContext currentOperator,
    IApplicationLogger? logger = null
) : IAuthenticationService
{
    public async Task<bool> SignInAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default
    )
    {
        var log = logger ?? ApplicationLog.Current;
        try
        {
            var normalized = Normalize(username);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var entity = await db
                .Operators.AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.NormalizedUsername == normalized && x.IsEnabled,
                    cancellationToken
                );
            if (entity is null || !PasswordHashing.Verify(password, entity))
            {
                log.Write(
                    ApplicationLogLevel.Warning,
                    nameof(AuthenticationService),
                    "Login.Rejected",
                    "reason=invalid-credentials-or-disabled"
                );
                return false;
            }
            currentOperator.Set(entity.Id, entity.Username);
            log.Write(
                ApplicationLogLevel.Info,
                nameof(AuthenticationService),
                "Login.Succeeded",
                "登录成功",
                entity.Id.ToString()
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            log.Write(
                ApplicationLogLevel.Info,
                nameof(AuthenticationService),
                "Login.Canceled",
                "登录已取消"
            );
            throw;
        }
        catch (Exception exception)
        {
            // Do not serialize authentication exceptions: providers may embed input values.
            log.Write(
                ApplicationLogLevel.Error,
                nameof(AuthenticationService),
                "Login.Failed",
                $"reason={exception.GetType().Name}"
            );
            throw;
        }
    }

    internal static string Normalize(string value) => value.Trim().ToUpperInvariant();
}

internal static class PasswordHashing
{
    public const int DefaultIterations = 210_000;

    public static OperatorEntity CreateOperator(
        string username,
        string password,
        DateTimeOffset createdAtUtc
    )
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new OperatorEntity
        {
            Username = username,
            NormalizedUsername = AuthenticationService.Normalize(username),
            PasswordSalt = salt,
            PasswordHash = Derive(password, salt, DefaultIterations),
            PasswordIterations = DefaultIterations,
            CreatedAtUtc = createdAtUtc,
        };
    }

    public static bool Verify(string password, OperatorEntity entity)
    {
        var actual = Derive(password, entity.PasswordSalt, entity.PasswordIterations);
        return CryptographicOperations.FixedTimeEquals(actual, entity.PasswordHash);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
}

public sealed class ApplicationSessionState
{
    public Guid Id { get; set; }
}

public interface IDatabaseInitializer
{
    Task InitializeAsync(
        bool recoverStaleMigrationLock = false,
        CancellationToken cancellationToken = default
    );
}

public sealed class DatabaseInitializer(
    IDbContextFactory<AppDbContext> contextFactory,
    ApplicationSessionState sessionState,
    IExperimentRecoveryService recoveryService,
    IManagedEegMigrationService managedEegMigration
) : IDatabaseInitializer
{
    public async Task InitializeAsync(
        bool recoverStaleMigrationLock = false,
        CancellationToken cancellationToken = default
    )
    {
        System.IO.Directory.CreateDirectory(AppPaths.DatabaseDirectory);
        System.IO.Directory.CreateDirectory(AppPaths.RecordingsDirectory);
        System.IO.Directory.CreateDirectory(AppPaths.TemporaryEegDirectory);
        System.IO.Directory.CreateDirectory(AppPaths.ExportDirectory);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (recoverStaleMigrationLock)
            await SqliteMigrationLockRecovery.RecoverAsync(db, cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken);

        var now = DateTimeOffset.UtcNow;
        await recoveryService.RecoverAsync(now, cancellationToken);
        await managedEegMigration.MigrateAsync(cancellationToken);
        if (!await db.Operators.AnyAsync(x => x.NormalizedUsername == "1", cancellationToken))
            db.Operators.Add(PasswordHashing.CreateOperator("1", "1", now));

        var appSession = new ApplicationSessionEntity
        {
            Id = Guid.NewGuid(),
            StartedAtUtc = now,
            LastHeartbeatAtUtc = now,
        };
        db.ApplicationSessions.Add(appSession);
        await db.SaveChangesAsync(cancellationToken);
        sessionState.Id = appSession.Id;
    }
}

internal static class SqliteMigrationLockRecovery
{
    internal const string LockTableName = "__EFMigrationsLock";

    internal static Task RecoverAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default
    ) =>
        db.Database.ExecuteSqlRawAsync(
            $"DROP TABLE IF EXISTS \"{LockTableName}\";",
            cancellationToken
        );
}

public interface IExperimentRecoveryService
{
    Task RecoverAsync(DateTimeOffset recoveredAtUtc, CancellationToken cancellationToken = default);
}

public sealed class ExperimentRecoveryService(IDbContextFactory<AppDbContext> contextFactory)
    : IExperimentRecoveryService
{
    public async Task RecoverAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var sessions = await db
            .ApplicationSessions.Where(x => x.ClosedAtUtc == null)
            .ToListAsync(cancellationToken);
        var sessionIds = sessions.Select(x => x.Id).ToArray();
        if (sessionIds.Length == 0)
            return;

        var runs = await db
            .ExperimentRuns.Include(x => x.Events)
            .Include(x => x.Incidents)
            .Include(x => x.Experiment)
            .Where(x =>
                sessionIds.Contains(x.ApplicationSessionId)
                && (
                    x.Status == ExperimentRunStatus.Preparing
                    || x.Status == ExperimentRunStatus.Running
                )
            )
            .ToListAsync(cancellationToken);
        foreach (var run in runs)
        {
            var end = run.LastHeartbeatAtUtc == default ? recoveredAtUtc : run.LastHeartbeatAtUtc;
            foreach (
                var activeEvent in run.Events.Where(x =>
                    x.Status is ExperimentEventStatus.Starting or ExperimentEventStatus.Running
                )
            )
            {
                activeEvent.EndedAtUtc = end;
                if (activeEvent.TimelineStartMilliseconds.HasValue)
                {
                    activeEvent.TimelineEndMilliseconds = Math.Max(
                        activeEvent.TimelineStartMilliseconds.Value,
                        Math.Max(0d, run.LastKnownElapsedMilliseconds)
                    );
                }
                activeEvent.Status = ExperimentEventStatus.Interrupted;
                activeEvent.EndTimeAccuracy = EventEndTimeAccuracy.LastDurableHeartbeat;
            }
            run.Status = ExperimentRunStatus.RecoveredAfterCrash;
            run.EndedAtUtc = end;
            run.Revision++;
            run.Experiment.Status = ExperimentStatus.Interrupted;
            run.Experiment.UpdatedAtUtc = recoveredAtUtc;
            run.Incidents.Add(
                new ExperimentIncidentEntity
                {
                    Kind = ExperimentIncidentKind.CrashRecovery,
                    OccurredAtUtc = recoveredAtUtc,
                    Source = "startup-recovery",
                    Message = "检测到上次程序未正常结束，已按最后一次持久化心跳收口实验。",
                }
            );
        }
        foreach (var session in sessions)
            session.ClosedAtUtc = session.LastHeartbeatAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }
}

public interface ISubjectLookupService
{
    Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default
    );
    Task<bool> ExistsAsync(string subjectCode, CancellationToken cancellationToken = default);
}

public sealed class SubjectLookupService(IDbContextFactory<AppDbContext> contextFactory)
    : ISubjectLookupService
{
    public async Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default
    )
    {
        var normalized = NormalizeSubject(query);
        if (normalized.Length == 0)
            return [];
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db
            .Subjects.AsNoTracking()
            .Where(x =>
                x.Experiments.Any(e => e.Runs.Any()) && x.NormalizedSubjectCode.Contains(normalized)
            )
            .OrderBy(x => x.NormalizedSubjectCode.StartsWith(normalized) ? 0 : 1)
            .ThenBy(x => x.SubjectCode)
            .Select(x => x.SubjectCode)
            .Take(Math.Clamp(limit, 1, 50))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        string subjectCode,
        CancellationToken cancellationToken = default
    )
    {
        var normalized = NormalizeSubject(subjectCode);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Subjects.AnyAsync(
            x => x.NormalizedSubjectCode == normalized,
            cancellationToken
        );
    }

    public static string NormalizeSubject(string value) => value.Trim().ToUpperInvariant();
}
