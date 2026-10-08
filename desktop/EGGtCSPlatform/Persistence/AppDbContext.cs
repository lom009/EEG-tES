using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EGGtCSPlatform.Persistence;

public static class AppPaths
{
    public const string ProductName = "EGGtCSPlatform";

    public static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductName
        );
    public static string DefaultConfigurationPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.default.json");
    public static string LegacyConfigurationPath =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    public static string UserConfigurationPath => Path.Combine(Root, "appsettings.json");
    public static string ConfigurationBackupDirectory => Path.Combine(Root, "config-backups");
    public static string DeviceBackendConfigurationBackupPath =>
        Path.Combine(Root, "appsettings.DeviceBackend.backup.json");
    public static string DatabaseDirectory => Path.Combine(Root, "data");
    public static string DatabasePath => Path.Combine(DatabaseDirectory, "eggtcs.db");
    public static string RecordingsDirectory => Path.Combine(Root, "recordings");
    public static string TemporaryEegDirectory => Path.Combine(Root, "temp", "eeg");
    public static string ExportDirectory => Path.Combine(Root, "export");
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<OperatorEntity> Operators => Set<OperatorEntity>();
    public DbSet<SubjectEntity> Subjects => Set<SubjectEntity>();
    public DbSet<ExperimentEntity> Experiments => Set<ExperimentEntity>();
    public DbSet<StimulusParadigmEntity> StimulusParadigms => Set<StimulusParadigmEntity>();
    public DbSet<StimulusTargetEntity> StimulusTargets => Set<StimulusTargetEntity>();
    public DbSet<StimulusChannelEntity> StimulusChannels => Set<StimulusChannelEntity>();
    public DbSet<ExperimentElectrodeEntity> ExperimentElectrodes =>
        Set<ExperimentElectrodeEntity>();
    public DbSet<ImpedanceSnapshotEntity> ImpedanceSnapshots => Set<ImpedanceSnapshotEntity>();
    public DbSet<ApplicationSessionEntity> ApplicationSessions => Set<ApplicationSessionEntity>();
    public DbSet<ExperimentRunEntity> ExperimentRuns => Set<ExperimentRunEntity>();
    public DbSet<ExperimentEventEntity> ExperimentEvents => Set<ExperimentEventEntity>();
    public DbSet<ExperimentIncidentEntity> ExperimentIncidents => Set<ExperimentIncidentEntity>();
    public DbSet<EegFileEntity> EegFiles => Set<EegFileEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OperatorEntity>(entity =>
        {
            entity.ToTable("Operators");
            entity.HasIndex(x => x.NormalizedUsername).IsUnique();
            entity.Property(x => x.Username).HasMaxLength(64);
            entity.Property(x => x.NormalizedUsername).HasMaxLength(64);
        });
        modelBuilder.Entity<SubjectEntity>(entity =>
        {
            entity.ToTable("Subjects");
            entity.HasIndex(x => x.NormalizedSubjectCode).IsUnique();
            entity.Property(x => x.SubjectCode).HasMaxLength(64);
            entity.Property(x => x.NormalizedSubjectCode).HasMaxLength(64);
        });
        modelBuilder.Entity<ExperimentEntity>(entity =>
        {
            entity.ToTable("Experiments");
            entity
                .Property(x => x.CreationMode)
                .HasDefaultValue(
                    EGGtCSPlatform.ViewModels.Pages.ExperimentCreationMode.AcquisitionAndStimulation
                )
                .HasSentinel(
                    EGGtCSPlatform.ViewModels.Pages.ExperimentCreationMode.AcquisitionAndStimulation
                );
            entity.HasIndex(x => x.ExperimentCode).IsUnique();
            entity.Property(x => x.ExperimentCode).HasMaxLength(32);
            entity.Property(x => x.Remarks).HasMaxLength(1000);
            entity
                .HasOne(x => x.Operator)
                .WithMany(x => x.Experiments)
                .HasForeignKey(x => x.OperatorId);
            entity
                .HasOne(x => x.Subject)
                .WithMany(x => x.Experiments)
                .HasForeignKey(x => x.SubjectId);
        });
        modelBuilder.Entity<StimulusParadigmEntity>(entity =>
        {
            entity.ToTable("StimulusParadigms");
            entity.HasIndex(x => x.ExperimentId).IsUnique();
            entity
                .HasOne(x => x.Experiment)
                .WithOne(x => x.StimulusParadigm)
                .HasForeignKey<StimulusParadigmEntity>(x => x.ExperimentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StimulusTargetEntity>().ToTable("StimulusTargets");
        modelBuilder.Entity<StimulusChannelEntity>(entity =>
        {
            entity.ToTable("StimulusChannels");
            entity.HasIndex(x => new { x.StimulusTargetId, x.PhysicalChannelId }).IsUnique();
        });
        modelBuilder.Entity<ExperimentElectrodeEntity>(entity =>
        {
            entity.ToTable("ExperimentElectrodes");
            entity.HasIndex(x => new { x.ExperimentId, x.SiteId }).IsUnique();
        });
        modelBuilder.Entity<ImpedanceSnapshotEntity>(entity =>
        {
            entity.ToTable("ImpedanceSnapshots");
            entity
                .HasIndex(x => new
                {
                    x.ExperimentId,
                    x.SiteId,
                    x.Kind,
                })
                .IsUnique();
        });
        modelBuilder.Entity<ApplicationSessionEntity>().ToTable("ApplicationSessions");
        modelBuilder.Entity<ExperimentRunEntity>(entity =>
        {
            entity.ToTable("ExperimentRuns");
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity
                .HasOne(x => x.ApplicationSession)
                .WithMany(x => x.Runs)
                .HasForeignKey(x => x.ApplicationSessionId);
        });
        modelBuilder.Entity<ExperimentEventEntity>(entity =>
        {
            entity.ToTable("ExperimentEvents");
            entity.HasIndex(x => new { x.ExperimentRunId, x.Sequence }).IsUnique();
        });
        modelBuilder.Entity<ExperimentIncidentEntity>().ToTable("ExperimentIncidents");
        modelBuilder.Entity<EegFileEntity>(entity =>
        {
            entity.ToTable("EegFiles");
            entity.HasIndex(x => x.Path);
            entity
                .HasOne(x => x.SourceFile)
                .WithMany()
                .HasForeignKey(x => x.SourceFileId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        Directory.CreateDirectory(AppPaths.DatabaseDirectory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppPaths.DatabasePath};Foreign Keys=True;Default Timeout=5")
            .AddInterceptors(SqlitePragmaConnectionInterceptor.Instance)
            .Options;
        return new AppDbContext(options);
    }
}

public sealed class SqlitePragmaConnectionInterceptor : DbConnectionInterceptor
{
    public static SqlitePragmaConnectionInterceptor Instance { get; } = new();

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData
    ) => Configure(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default
    ) => await ConfigureAsync(connection, cancellationToken);

    private static void Configure(DbConnection connection)
    {
        foreach (var pragma in Pragmas)
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
    }

    private static async Task ConfigureAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        foreach (var pragma in Pragmas)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static readonly string[] Pragmas =
    [
        "PRAGMA foreign_keys=ON;",
        "PRAGMA busy_timeout=5000;",
        "PRAGMA synchronous=FULL;",
    ];
}
