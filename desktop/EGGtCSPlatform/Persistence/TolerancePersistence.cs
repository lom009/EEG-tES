using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Persistence;

public sealed class ToleranceConfigurationEntity
{
    public Guid Id { get; set; }
    public long SubjectId { get; set; }
    public SubjectEntity Subject { get; set; } = null!;
    public string ConfigurationKey { get; set; } = "";
    public string ConfigurationJson { get; set; } = "";
    public StimulusKind Kind { get; set; }
    public List<ToleranceSnapshotEntity> Snapshots { get; set; } = [];
}

public sealed class ToleranceSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid ConfigurationId { get; set; }
    public ToleranceConfigurationEntity Configuration { get; set; } = null!;
    public long OperatorId { get; set; }
    public OperatorEntity Operator { get; set; } = null!;
    public long SavedAtUtcTicks { get; set; }
    public string SnapshotJson { get; set; } = "";
}

public static class ToleranceSchema
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ToleranceConfigurationEntity>(e =>
        {
            e.ToTable("ToleranceConfigurations");
            e.HasKey(x => x.Id);
            e.Property(x => x.ConfigurationKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.ConfigurationJson).IsRequired();
            e.HasIndex(x => new { x.SubjectId, x.ConfigurationKey }).IsUnique();
            e.HasOne(x => x.Subject)
                .WithMany()
                .HasForeignKey(x => x.SubjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ToleranceSnapshotEntity>(e =>
        {
            e.ToTable("ToleranceSnapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.SnapshotJson).IsRequired();
            e.HasIndex(x => new { x.ConfigurationId, x.SavedAtUtcTicks });
            e.HasOne(x => x.Configuration)
                .WithMany(x => x.Snapshots)
                .HasForeignKey(x => x.ConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Operator)
                .WithMany()
                .HasForeignKey(x => x.OperatorId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed record ToleranceResultSnapshot(
    Guid Id,
    DateTimeOffset SavedAt,
    long OperatorId,
    string OperatorName,
    double Current,
    bool IsSimulated,
    ToleranceTestRouteData Test,
    StimulusConfigurationSnapshot AppliedConfiguration
)
{
    public string CurrentText => $"{Current:0.00} mA";
    public string TimeText => SavedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
    public string Remarks => Test.Context.Remarks;
}

public sealed record ToleranceResultRow(int Number, ToleranceResultSnapshot Result);

public sealed record ToleranceHistoryQuery(
    string SubjectId = "",
    StimulusKind? Kind = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    int Page = 1,
    int PageSize = 10
);

public sealed record ToleranceHistoryGroup(
    Guid Id,
    string SubjectId,
    IReadOnlyList<ToleranceResultSnapshot> Results
)
{
    public IReadOnlyList<ToleranceResultRow> Rows =>
        Results.Select((r, i) => new ToleranceResultRow(Results.Count - i, r)).ToArray();
    public int Count => Results.Count;
    public ToleranceResultSnapshot Latest => Results[0];
    public string Summary => ToleranceDisplay.Configuration(Latest.Test.Configuration);
    public string Sites => ToleranceDisplay.Sites(Latest.Test);
}

public sealed record ToleranceHistoryPage(
    IReadOnlyList<ToleranceHistoryGroup> Groups,
    int TotalCount
);

public sealed record ToleranceSaveData(
    double Current,
    bool IsSimulated,
    StimulusConfigurationSnapshot Configuration
);

public interface ITolerancePersistenceService
{
    Task<ToleranceResultSnapshot> SaveAsync(
        ToleranceTestRouteData route,
        double current,
        CancellationToken cancellationToken = default
    );
    Task<ToleranceResultSnapshot> SaveAsync(
        ToleranceTestRouteData route,
        ToleranceSaveData data,
        CancellationToken cancellationToken = default
    ) =>
        data.IsSimulated
            ? SaveAsync(route, data.Current, cancellationToken)
            : throw new NotSupportedException("此存储实现不支持真实耐受度结果。");
    Task<ToleranceHistoryPage> QueryAsync(
        ToleranceHistoryQuery query,
        CancellationToken cancellationToken = default
    );
}

public static class ToleranceDisplay
{
    public static string Configuration(StimulusConfigurationSnapshot s) =>
        $"{StimulusParameterPolicy.ModeName(s.Kind, s.ShamMode)} · {s.ArrayMode switch { StimulusArrayMode.DualChannel => "双通道", StimulusArrayMode.Hd => "HD", _ => "多靶点" }}";

    public static string Sites(ToleranceTestRouteData route, bool includeChannel = true) =>
        string.Join(
            "  ",
            route
                .Electrodes.OrderBy(e =>
                    route.Configuration.Targets.Single(t => t.TargetId == e.TargetId).DisplayOrder
                )
                .ThenBy(e => e.PhysicalChannelId)
                .Select(e =>
                {
                    var roles = StimulationElectrodeRoleResolver.Resolve(
                        route.Configuration.Direction
                    );
                    var token = ElectrodeRoleDisplay.Token(
                        e.Role == StimulationChannelRole.FixedActive
                            ? roles.FixedActive
                            : roles.Selectable
                    );
                    var site = $"{e.SiteId}·{token}";
                    return includeChannel ? $"{site} (CH{e.PhysicalChannelId})" : site;
                })
        );

    public static string ConfigurationKey(ToleranceTestRouteData route)
    {
        var s = route.Configuration;
        var canonical = JsonSerializer.Serialize(
            new
            {
                s.Kind,
                s.ArrayMode,
                s.Direction,
                s.ShamMode,
                s.RampSeconds,
                s.Frequency,
                s.DutyPercent,
                Targets = s
                    .Targets.OrderBy(t => t.DisplayOrder)
                    .Select(t => new
                    {
                        t.DisplayOrder,
                        Channels = route
                            .Electrodes.Where(e => e.TargetId == t.TargetId)
                            .OrderBy(e => e.PhysicalChannelId)
                            .Select(e => new
                            {
                                Site = e.SiteId.ToUpperInvariant(),
                                e.PhysicalChannelId,
                                e.Role,
                            }),
                    }),
            }
        );
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed class TolerancePersistenceService(
    IDbContextFactory<AppDbContext> factory,
    ICurrentOperatorContext currentOperator,
    StimulusCapabilityProfile capability
) : ITolerancePersistenceService
{
    public Task<ToleranceResultSnapshot> SaveAsync(
        ToleranceTestRouteData route,
        double current,
        CancellationToken cancellationToken = default
    )
    {
        using var model = new ToleranceCurrentModel(route, capability);
        model.SetCurrent(current);
        return SaveAsync(
            route,
            new ToleranceSaveData(current, true, model.Configuration),
            cancellationToken
        );
    }

    public async Task<ToleranceResultSnapshot> SaveAsync(
        ToleranceTestRouteData route,
        ToleranceSaveData data,
        CancellationToken cancellationToken = default
    )
    {
        var current = data.Current;
        var subjectCode = route.Context.SubjectId.Trim();
        if (
            subjectCode.Length is 0 or > 64
            || subjectCode.Any(char.IsControl)
            || route.Context.Remarks.Length > 1000
        )
            throw new ArgumentException("被试 ID 或备注无效。");
        if (!currentOperator.IsAuthenticated)
            throw new InvalidOperationException("请先登录。");
        using var model = new ToleranceCurrentModel(
            route,
            capability,
            protocolStep: !data.IsSimulated
        );
        model.SetCurrent(current);
        if (
            JsonSerializer.Serialize(model.Configuration)
            != JsonSerializer.Serialize(data.Configuration)
        )
            throw new ArgumentException("保存的电流与会话配置不一致。");
        // Freeze all nested collections before any asynchronous database operation.
        var frozen = JsonSerializer.Deserialize<ToleranceTestRouteData>(
            JsonSerializer.Serialize(route)
        )!;
        var snapshot = new ToleranceResultSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            currentOperator.OperatorId,
            currentOperator.Username,
            current,
            data.IsSimulated,
            frozen,
            JsonSerializer.Deserialize<StimulusConfigurationSnapshot>(
                JsonSerializer.Serialize(data.Configuration)
            )!
        );
        var key = ToleranceDisplay.ConfigurationKey(frozen);
        var normalized = SubjectLookupService.NormalizeSubject(subjectCode);
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(
                cancellationToken
            );
            var subject = await db.Subjects.SingleOrDefaultAsync(
                s => s.NormalizedSubjectCode == normalized,
                cancellationToken
            );
            if (subject is null)
            {
                subject = new SubjectEntity
                {
                    SubjectCode = subjectCode,
                    NormalizedSubjectCode = normalized,
                    CreatedAtUtc = snapshot.SavedAt,
                };
                db.Subjects.Add(subject);
            }
            var group =
                subject.Id == 0
                    ? null
                    : await db.Set<ToleranceConfigurationEntity>()
                        .SingleOrDefaultAsync(
                            g => g.SubjectId == subject.Id && g.ConfigurationKey == key,
                            cancellationToken
                        );
            if (group is null)
            {
                group = new ToleranceConfigurationEntity
                {
                    Id = Guid.NewGuid(),
                    Subject = subject,
                    ConfigurationKey = key,
                    ConfigurationJson = JsonSerializer.Serialize(frozen),
                    Kind = route.Configuration.Kind,
                };
                db.Add(group);
            }
            db.Add(
                new ToleranceSnapshotEntity
                {
                    Id = snapshot.Id,
                    Configuration = group,
                    OperatorId = snapshot.OperatorId,
                    SavedAtUtcTicks = snapshot.SavedAt.UtcTicks,
                    SnapshotJson = JsonSerializer.Serialize(snapshot),
                }
            );
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return snapshot;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }
    }

    public async Task<ToleranceHistoryPage> QueryAsync(
        ToleranceHistoryQuery query,
        CancellationToken cancellationToken = default
    )
    {
        if (
            query.Page < 1
            || query.PageSize is < 1 or > 100
            || query.StartDate?.Date > query.EndDate?.Date
        )
            throw new ArgumentException("日期范围或分页参数无效。");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = db.Set<ToleranceSnapshotEntity>().AsNoTracking();
        var subject = SubjectLookupService.NormalizeSubject(query.SubjectId);
        if (subject.Length > 0)
            rows = rows.Where(r => r.Configuration.Subject.NormalizedSubjectCode.Contains(subject));
        if (query.Kind is { } kind)
            rows = rows.Where(r => r.Configuration.Kind == kind);
        if (query.StartDate is { } start)
        {
            var ticks = new DateTimeOffset(
                DateTime.SpecifyKind(start.Date, DateTimeKind.Local)
            ).UtcTicks;
            rows = rows.Where(r => r.SavedAtUtcTicks >= ticks);
        }
        if (query.EndDate is { } end)
        {
            var ticks = new DateTimeOffset(
                DateTime.SpecifyKind(end.Date.AddDays(1), DateTimeKind.Local)
            ).UtcTicks;
            rows = rows.Where(r => r.SavedAtUtcTicks < ticks);
        }
        var groups = rows.GroupBy(r => r.ConfigurationId)
            .Select(g => new { Id = g.Key, Latest = g.Max(r => r.SavedAtUtcTicks) });
        var count = await groups.CountAsync(cancellationToken);
        var ids = await groups
            .OrderByDescending(g => g.Latest)
            .ThenBy(g => g.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(g => g.Id)
            .ToArrayAsync(cancellationToken);
        var selected = await rows.Where(r => ids.Contains(r.ConfigurationId))
            .Select(r => new
            {
                r.ConfigurationId,
                r.Configuration.Subject.SubjectCode,
                r.SnapshotJson,
                r.SavedAtUtcTicks,
            })
            .ToListAsync(cancellationToken);
        return new(
            ids.Select(id => new ToleranceHistoryGroup(
                    id,
                    selected.First(r => r.ConfigurationId == id).SubjectCode,
                    selected
                        .Where(r => r.ConfigurationId == id)
                        .OrderByDescending(r => r.SavedAtUtcTicks)
                        .Select(r =>
                            JsonSerializer.Deserialize<ToleranceResultSnapshot>(r.SnapshotJson)!
                        )
                        .ToArray()
                ))
                .ToArray(),
            count
        );
    }
}
