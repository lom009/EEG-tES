using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public enum EnvelopeTrainingStage { Ready, Running, Scoring, Saved, Aborted, Results }
public sealed record EnvelopeTrainingProgress(double AudioSeconds, double StimulationSeconds);
public sealed record EnvelopeTrainingCharacterScore(string Character, bool Correct);
public sealed record EnvelopeTrainingTrial(
    int SentenceNumber, string Sentence, string Corpus, double CharactersPerSecond,
    DateTimeOffset CompletedAtUtc, double AudioDurationSeconds, string AudioSha256,
    IReadOnlyList<EnvelopeTrainingCharacterScore> Characters)
{
    public int CorrectCount => Characters.Count(character => character.Correct);
    public string AccuracyText => $"{(Characters.Count == 0 ? 0 : (double)CorrectCount / Characters.Count):P0}";
    public string CountText => $"{CorrectCount}/{Characters.Count}";
}
public sealed record EnvelopeTrainingSession(
    Guid SessionId, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc,
    EnvelopeTrainingConfigurationSnapshot Configuration, IReadOnlyList<EnvelopeTrainingTrial> Trials,
    int InterruptedTrials, string AlgorithmVersion = "pcm-rms-50hz-v1");

// A serialization DTO rather than the router's constructor-bound live model.
public sealed record EnvelopeTrainingConfigurationSnapshot(string ExperimentId, string SubjectId,
    string DeviceId, long ExperimentDatabaseId, ExperimentStimulusConfigurationSnapshot StimulusConfiguration,
    IReadOnlyList<StimulusElectrodeAssignment> StimulusElectrodes, PreRunImpedanceSnapshot? PreRunImpedance,
    string DetectionStatus);

/// <summary>The execution service owns audio/device lifetime. Completion means both actually finished.</summary>
public interface IEnvelopeTrainingExecutionService
{
    Task ExecuteAsync(ExperimentRunRouteData route, EnvelopeTrainingAudio audio, bool stimulate,
        IProgress<EnvelopeTrainingProgress> progress, CancellationToken token);
    Task ConfirmStoppedAsync(ExperimentRunRouteData route, CancellationToken token) =>
        Task.FromException(new NotSupportedException("执行服务不支持停止确认。"));
}
public sealed class EnvelopeTrainingStopUnconfirmedException(Exception inner)
    : Exception("设备停止未确认，请重试停止并检查设备。", inner);
public interface IEnvelopeTrainingResultStore
{
    Task SaveAsync(EnvelopeTrainingSession session, CancellationToken token = default);
}
