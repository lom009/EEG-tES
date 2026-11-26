using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Persistence;

public sealed class SpeechExperimentEntity
{
    public Guid Id { get; set; }
    public long ExperimentId { get; set; }
    public ExperimentEntity Experiment { get; set; } = null!;
    public string SourceJson { get; set; } = "";
    public int Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public ExperimentRunStatus Status { get; set; }
    public List<SpeechTrainingRoundEntity> Rounds { get; set; } = [];
}

public sealed class SpeechTrainingRoundEntity
{
    public Guid Id { get; set; }
    public Guid SpeechExperimentId { get; set; }
    public SpeechExperimentEntity Experiment { get; set; } = null!;
    public int Number { get; set; }
    public double DelayMilliseconds { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public SpeechRoundStatus Status { get; set; }
    public string ResultJson { get; set; } = "";
}

public interface ISpeechExperimentService
{
    Task SaveRoundAsync(
        Guid experimentId,
        SpeechExperimentSource source,
        SpeechTrainingRound round,
        CancellationToken token = default
    );
    Task EndAsync(
        Guid experimentId,
        ExperimentRunStatus status,
        CancellationToken token = default,
        SpeechExperimentSource? source = null
    );
    Task<SpeechExperimentResult> LoadAsync(Guid experimentId, CancellationToken token = default);
}

public sealed class SpeechExperimentService(IDbContextFactory<AppDbContext> factory)
    : ISpeechExperimentService
{
    public async Task SaveRoundAsync(
        Guid experimentId,
        SpeechExperimentSource source,
        SpeechTrainingRound round,
        CancellationToken token = default
    )
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var experiment = await db
            .SpeechExperiments.Include(x => x.Experiment)
            .Include(x => x.Rounds)
            .SingleOrDefaultAsync(x => x.Id == experimentId, token);
        if (experiment is null)
        {
            var identity = await db.Experiments.SingleAsync(x => x.Id == source.DatabaseId, token);
            if (
                identity.Status != ExperimentStatus.Draft
                || await db.SpeechExperiments.AnyAsync(
                    x => x.ExperimentId == source.DatabaseId,
                    token
                )
            )
                throw new InvalidOperationException("该实验已经开始或结束，请创建新实验。");
            experiment = new()
            {
                Id = experimentId,
                ExperimentId = identity.Id,
                Experiment = identity,
                SourceJson = SimulationJson.Write(source),
                StartedAt = round.Result.StartedAt,
                Status = ExperimentRunStatus.Running,
            };
            db.SpeechExperiments.Add(experiment);
        }
        if (experiment.Status != ExperimentRunStatus.Running)
            throw new InvalidOperationException("实验已结束，不能继续写入训练。");
        var row = experiment.Rounds.SingleOrDefault(x => x.Id == round.Id);
        if (row is null)
        {
            if (
                round.Number != experiment.Rounds.Count + 1
                || experiment.Rounds.Any(x => x.Status == SpeechRoundStatus.Running)
            )
                throw new InvalidOperationException("上一轮训练尚未结束或训练序号无效。");
            row = new()
            {
                Id = round.Id,
                Number = round.Number,
                DelayMilliseconds = round.DelayMilliseconds,
                SpeechExperimentId = experiment.Id,
                Experiment = experiment,
            };
            experiment.Rounds.Add(row);
            db.SpeechTrainingRounds.Add(row);
        }
        else if (row.Status != SpeechRoundStatus.Running)
        {
            // A successful commit followed by a lost acknowledgement is safe to retry.
            if (row.Status == round.Status && row.ResultJson == SimulationJson.Write(round.Result))
                return;
            throw new InvalidOperationException("已结束的训练不能修改。");
        }
        if (row.ResultJson.Length > 0)
        {
            var saved = SimulationJson.Read<SpeechTrainingResult>(row.ResultJson)!;
            if (
                row.DelayMilliseconds != round.DelayMilliseconds
                || row.Number != round.Number
                || SimulationJson.Write(saved.Settings)
                    != SimulationJson.Write(round.Result.Settings)
                || round.Result.Sentences.Count < saved.Sentences.Count
                || saved
                    .Sentences.Where(
                        (sentence, index) =>
                            SimulationJson.Write(sentence)
                            != SimulationJson.Write(round.Result.Sentences[index])
                    )
                    .Any()
            )
                throw new InvalidOperationException("已提交的逐句结果和初始参数不能修改。");
        }
        row.ResultJson = SimulationJson.Write(round.Result);
        row.EndedAt = round.EndedAt;
        row.Status = round.Status;
        experiment.Experiment.Status = ExperimentStatus.Running;
        experiment.Experiment.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task EndAsync(
        Guid experimentId,
        ExperimentRunStatus status,
        CancellationToken token = default,
        SpeechExperimentSource? source = null
    )
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var experiment = await db
            .SpeechExperiments.Include(x => x.Experiment)
            .Include(x => x.Rounds)
            .SingleOrDefaultAsync(x => x.Id == experimentId, token);
        var now = DateTimeOffset.UtcNow;
        if (experiment is null && source is not null)
        {
            var identity = await db.Experiments.SingleAsync(x => x.Id == source.DatabaseId, token);
            if (
                identity.Status != ExperimentStatus.Draft
                || await db.SpeechExperiments.AnyAsync(
                    x => x.ExperimentId == source.DatabaseId,
                    token
                )
            )
                throw new InvalidOperationException("该实验已经开始或结束，请创建新实验。");
            experiment = new()
            {
                Id = experimentId,
                ExperimentId = identity.Id,
                Experiment = identity,
                SourceJson = SimulationJson.Write(source),
                StartedAt = now,
                Status = ExperimentRunStatus.Running,
            };
            db.SpeechExperiments.Add(experiment);
        }
        if (experiment is null || experiment.EndedAt is not null)
            return;
        foreach (var round in experiment.Rounds.Where(x => x.Status == SpeechRoundStatus.Running))
        {
            round.Status = SpeechRoundStatus.Interrupted;
            round.EndedAt = now;
        }
        experiment.EndedAt = now;
        experiment.Status = status;
        experiment.Experiment.Status =
            status == ExperimentRunStatus.Completed
                ? ExperimentStatus.Completed
                : ExperimentStatus.Interrupted;
        experiment.Experiment.UpdatedAtUtc = now;
        await db.SaveChangesAsync(token);
    }

    public async Task<SpeechExperimentResult> LoadAsync(
        Guid experimentId,
        CancellationToken token = default
    )
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var e = await db
            .SpeechExperiments.AsNoTracking()
            .Include(x => x.Rounds)
            .SingleAsync(x => x.Id == experimentId, token);
        if (e.Version != 1)
            throw new InvalidDataException("不支持的言语实验记录版本。");
        return new(
            e.Id,
            SimulationJson.Read<SpeechExperimentSource>(e.SourceJson)
                ?? throw new InvalidDataException("缺少实验配置。"),
            e.StartedAt,
            e.EndedAt,
            e.Status,
            e.Rounds.OrderBy(x => x.Number)
                .Select(x => new SpeechTrainingRound(
                    x.Id,
                    x.Number,
                    x.DelayMilliseconds,
                    x.EndedAt,
                    x.Status,
                    SimulationJson.Read<SpeechTrainingResult>(x.ResultJson)
                        ?? throw new InvalidDataException("缺少训练结果。")
                ))
                .ToArray()
        );
    }
}
