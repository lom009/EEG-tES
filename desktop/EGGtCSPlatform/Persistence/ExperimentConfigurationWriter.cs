using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Persistence;

public sealed record ExperimentConfigurationSaveResult(
    long ExperimentId,
    string ExperimentCode,
    bool CreatedNew
);

internal static class ExperimentConfigurationWriter
{
    // Caller owns the transaction: identity selection and run creation must commit together.
    public static async Task<(ExperimentEntity Experiment, bool CreatedNew)> ResolveAsync(
        AppDbContext db,
        long id,
        ExperimentConfigurationTemplate configuration,
        CancellationToken token
    )
    {
        var source = await db
            .Experiments.Include(e => e.SpeechExperiment)
            .Include(e => e.Runs)
            .Include(e => e.StimulusParadigm)!
                .ThenInclude(p => p!.Targets)
                    .ThenInclude(t => t.Channels)
            .Include(e => e.Electrodes)
            .Include(e => e.ImpedanceSnapshots)
            .SingleAsync(e => e.Id == id, token);
        if (
            source.Runs.Any(r =>
                r.Status is ExperimentRunStatus.Preparing or ExperimentRunStatus.Running
            )
        )
            throw new InvalidOperationException("实验正在运行，不能修改配置或再次启动。");
        if (source.SpeechExperiment is not null)
            throw new InvalidOperationException(
                "言语实验已经开始，请通过结果页复制配置创建新实验。"
            );
        if (source.Runs.Count == 0)
            return (source, false);
        var key = ExperimentConfigurationIdentity.Key(configuration);
        // A missing or damaged snapshot cannot establish that this group is homogeneous.
        if (source.Runs.All(r => Matches(r.ConfigurationJson, key)))
            return (source, false);
        var separator = source.ExperimentCode.LastIndexOf('-');
        if (separator < 0)
            throw new InvalidOperationException("原实验编号缺少日期前缀，无法生成新编号。");
        var prefix = source.ExperimentCode[..(separator + 1)];
        if (
            !prefix.StartsWith("EXP-", StringComparison.Ordinal)
            || prefix.Length != 13
            || !DateTime.TryParseExact(
                prefix.Substring(4, 8),
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _
            )
        )
            throw new InvalidOperationException("原实验编号日期无效，无法生成新编号。");
        var codes = await db
            .Experiments.Where(e => e.ExperimentCode.StartsWith(prefix))
            .Select(e => e.ExperimentCode)
            .ToArrayAsync(token);
        var next = checked(
            codes
                .Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty()
                .Max() + 1
        );
        var result = new ExperimentEntity
        {
            ExperimentCode = $"{prefix}{next:D3}",
            SubjectId = source.SubjectId,
            OperatorId = source.OperatorId,
            ScheduledAt = source.ScheduledAt,
            Remarks = source.Remarks,
            CreationMode = configuration.CreationMode,
            Status = ExperimentStatus.Draft,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Experiments.Add(result);
        return (result, true);
    }

    private static bool Matches(string? json, string key)
    {
        if (json is null)
            return false;
        try
        {
            return ExperimentConfigurationIdentity.Key(ExperimentConfigurationIdentity.Read(json))
                == key;
        }
        catch (System.IO.InvalidDataException)
        {
            return false;
        }
    }

    public static void ReplaceDraft(
        AppDbContext db,
        ExperimentEntity experiment,
        ExperimentConfigurationTemplate configuration,
        System.Collections.Generic.IReadOnlyList<ImpedanceSnapshotValue> impedances
    )
    {
        if (experiment.Runs.Count != 0)
            return;
        if (experiment.StimulusParadigm is not null)
            db.StimulusParadigms.Remove(experiment.StimulusParadigm);
        db.ExperimentElectrodes.RemoveRange(experiment.Electrodes);
        db.ImpedanceSnapshots.RemoveRange(experiment.ImpedanceSnapshots);
        experiment.Electrodes.Clear();
        experiment.ImpedanceSnapshots.Clear();
        ExperimentPersistenceService.ApplyConfiguration(
            experiment,
            configuration,
            impedances,
            configuration.PhysicalChannels ?? []
        );
        experiment.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
