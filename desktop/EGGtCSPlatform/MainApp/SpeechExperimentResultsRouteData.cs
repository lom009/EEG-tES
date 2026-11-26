using System;
using System.Collections.Generic;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.MainApp;

// Durable source deliberately excludes live device, navigation and audio buffer objects.
public sealed record SpeechExperimentSource(
    long DatabaseId,
    string ExperimentCode,
    string SubjectId,
    DateTimeOffset ScheduledAt,
    string Remarks,
    ExperimentStimulusConfigurationSnapshot Stimulation,
    IReadOnlyList<StimulusElectrodeAssignment> Electrodes,
    int SampleRateHz
)
{
    public static SpeechExperimentSource From(ExperimentRunRouteData route) =>
        new(
            route.ExperimentDatabaseId,
            route.ExperimentId,
            route.SubjectId,
            route.ScheduledAt ?? DateTimeOffset.Now,
            route.Remarks,
            ExperimentStimulusConfigurationSnapshot.Freeze(route.StimulusConfiguration),
            new List<StimulusElectrodeAssignment>(route.StimulusElectrodes).AsReadOnly(),
            route.SampleRateHz
        );

    public EnvelopeStimulationRouteData ToRoute() =>
        new(
            new(
                ExperimentCode,
                SubjectId,
                Stimulation,
                Electrodes,
                [],
                "",
                "",
                SampleRateHz,
                experimentDatabaseId: DatabaseId,
                scheduledAt: ScheduledAt,
                remarks: Remarks,
                creationMode: ExperimentCreationMode.StimulusOnly
            ),
            "历史结果"
        );
}

public enum SpeechRoundStatus
{
    Running,
    Completed,
    Interrupted,
}

public sealed record SpeechTrainingRound(
    Guid Id,
    int Number,
    double DelayMilliseconds,
    DateTimeOffset? EndedAt,
    SpeechRoundStatus Status,
    SpeechTrainingResult Result
);

public sealed record SpeechExperimentResult(
    Guid Id,
    SpeechExperimentSource Source,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    ExperimentRunStatus Status,
    IReadOnlyList<SpeechTrainingRound> Rounds,
    int Version = 1
);

public sealed record SpeechExperimentResultsRouteData(
    Guid ExperimentId,
    SpeechExperimentResult? Snapshot = null,
    int Tab = 0,
    Guid? TrainingId = null
);

public static class SpeechResultRoutes
{
    public static ExperimentRunRouteData WithEnvelope(
        ExperimentRunRouteData c,
        EnvelopeParameters envelope
    ) =>
        new(
            c.ExperimentId,
            c.SubjectId,
            c.StimulusConfiguration with
            {
                Envelope = envelope,
            },
            c.StimulusElectrodes,
            c.AcquisitionChannels,
            c.ReferenceChannel,
            c.GroundChannel,
            c.SampleRateHz,
            c.DeviceId,
            c.ExperimentDatabaseId,
            c.ScheduledAt,
            c.Remarks,
            c.ImportedTiming,
            c.HistoricalResult,
            c.PhysicalChannelNames,
            c.PreRunImpedance,
            c.CreationMode
        );

    public static SpeechTrainingResultsRouteData Detail(
        SpeechExperimentSource source,
        SpeechTrainingRound round
    )
    {
        var result = round.Result;
        var s = result.Settings;
        var route = source.ToRoute();
        var c = route.Configuration;
        route = route with
        {
            Configuration = new ExperimentRunRouteData(
                c.ExperimentId,
                c.SubjectId,
                c.StimulusConfiguration with
                {
                    Envelope = c.StimulusConfiguration.Envelope! with
                    {
                        DelayMilliseconds = round.DelayMilliseconds,
                    },
                },
                c.StimulusElectrodes,
                [],
                "",
                "",
                c.SampleRateHz,
                experimentDatabaseId: c.ExperimentDatabaseId,
                creationMode: c.CreationMode
            ),
        };
        var sentences = new List<SpeechSentenceResult>();
        foreach (var x in result.Sentences)
            sentences.Add(
                new(
                    x.Number,
                    x.Text,
                    x.Correct,
                    x.Total,
                    x.SpeechRate,
                    x.AudioSnapshot,
                    Adaptive: x.Adaptive,
                    PlaybackCompleted: x.PlaybackCompleted,
                    AdaptiveSnr: x.AdaptiveSnr,
                    Noise: x.Noise,
                    Rir: x.Rir
                )
            );
        return new(
            route,
            new(
                s.Corpus,
                s.SentenceList,
                s.Mode,
                s.Carrier,
                s.SpeechRate,
                s.ListSize,
                s.ResourceStart,
                s.ResourceEnd,
                s.AudioSettings,
                s.TrainingMode,
                s.AdaptiveScoring,
                s.InitialSnrDb,
                s.FixedSnrDb,
                s.Rir
            ),
            result.StartedAt,
            result.SentenceCount,
            sentences,
            result.AdaptiveSummary,
            result.Playbacks,
            result.SnrSummary
        );
    }
}
