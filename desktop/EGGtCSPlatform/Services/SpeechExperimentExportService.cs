using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;

namespace EGGtCSPlatform.Services;

public static class SpeechExperimentExportService
{
    public static Task<string> ExportAsync(
        SpeechExperimentResult result,
        string directory,
        CancellationToken token = default
    ) =>
        Task.Run(
            () =>
            {
                Directory.CreateDirectory(directory);
                var code = string.Concat(
                    result.Source.ExperimentCode.Select(c =>
                        Path.GetInvalidFileNameChars().Contains(c) ? '_' : c
                    )
                );
                var path = Path.Combine(
                    directory,
                    $"{code}_言语实验_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip"
                );
                try
                {
                    using var file = new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    );
                    using var archive = new ZipArchive(file, ZipArchiveMode.Create);
                    Write(archive, "experiment.json", SimulationJson.Write(result), token);
                    var rounds = new StringBuilder(
                        "训练序号,开始时间,结束时间,状态,计入有效统计,语料库,句表,模式,延时ms,完成句数,计划句数,正确字数,已评字数,正确率,语速SRT,信噪比SRT\r\n"
                    );
                    var sentences = new StringBuilder(
                        "训练序号,训练状态,计入有效统计,句次,题目,正确字数,已评字数,正确率,语速,信噪比,播放完成\r\n"
                    );
                    foreach (var r in result.Rounds.OrderBy(x => x.Number))
                    {
                        token.ThrowIfCancellationRequested();
                        var v = r.Result;
                        var valid = r.Status == SpeechRoundStatus.Completed;
                        Line(
                            rounds,
                            r.Number,
                            v.StartedAt,
                            r.EndedAt,
                            r.Status,
                            valid,
                            v.Settings.Corpus,
                            v.Settings.SentenceList,
                            v.Settings.Mode,
                            r.DelayMilliseconds,
                            v.Sentences.Count,
                            v.SentenceCount,
                            v.CorrectCount,
                            v.RatedCount,
                            v.Accuracy is { } a ? a * 100 : null,
                            valid ? v.AdaptiveSummary?.MeanTargetRate : null,
                            valid ? v.SnrSummary?.MeanSnrDb : null
                        );
                        foreach (var s in v.Sentences)
                            Line(
                                sentences,
                                r.Number,
                                r.Status,
                                valid,
                                s.Number,
                                s.Text,
                                s.Correct,
                                s.Total,
                                s.Total > 0 ? 100d * s.Correct / s.Total : null,
                                s.Adaptive?.Timing.TargetSpeechRate ?? (double?)s.SpeechRate,
                                s.AdaptiveSnr?.Submission.SnrDb ?? s.Noise?.Settings.SnrDb,
                                s.PlaybackCompleted
                            );
                    }
                    Write(archive, "训练汇总.csv", rounds.ToString(), token);
                    Write(archive, "逐句结果.csv", sentences.ToString(), token);
                    return path;
                }
                catch
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    throw;
                }
            },
            token
        );

    private static void Line(StringBuilder text, params object?[] values) =>
        text.AppendLine(
            string.Join(
                ',',
                values.Select(value =>
                {
                    var s = value is DateTimeOffset date
                        ? date.ToString("O")
                        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    if (value is string && s.Length > 0 && "=+-@\t\r".Contains(s[0]))
                        s = "'" + s;
                    return "\"" + s.Replace("\"", "\"\"") + "\"";
                })
            )
        );

    private static void Write(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        using var stream = archive.CreateEntry(name).Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(name.EndsWith(".csv", StringComparison.Ordinal))
        );
        writer.Write(content);
    }
}
