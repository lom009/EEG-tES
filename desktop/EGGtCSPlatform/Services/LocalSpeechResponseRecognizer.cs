using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.Services;

internal sealed record SpeechResponseSuggestion(
    string Transcript,
    bool?[] Ratings,
    int RecognizedCharacters,
    SpeechResponseReviewReason? ReviewReason
);

/// <summary>One stretch of speech between pauses, in recording time.</summary>
internal readonly record struct SpeechSegment(int StartMs, int EndMs, int StartFrame, int EndFrame);

/// <summary>
/// Picture naming result built from pause-separated segments.
/// Outcome: 正确 (target said first), 自我纠正 (target said after another word), 错误 (target never said),
/// 待确认 (needs the examiner), or null when only filler sounds were heard.
/// The two onsets are set only when the word begins at a known segment boundary.
/// </summary>
internal sealed record SpeechNamingSuggestion(
    string Transcript,
    string? Outcome,
    int? FirstOnsetMs,
    int? CorrectOnsetMs,
    bool Hesitated,
    SpeechResponseReviewReason? ReviewReason,
    string? ReviewNote
);

internal sealed class LocalSpeechResponseRecognizer
{
    private readonly string _directory;
    private readonly Lazy<Dictionary<string, string[]>> _readings;
    private readonly Lazy<Dictionary<string, string[][]>> _phraseReadings;
    private string Engine => Path.Combine(_directory, "llama-funasr-paraformer");
    private string Model => Path.Combine(_directory, "paraformer-q8.gguf");
    private string VoiceDetector => Path.Combine(_directory, "fsmn-vad.gguf");
    private string Readings => Path.Combine(_directory, "kTGHZ2013.txt");

    internal LocalSpeechResponseRecognizer(string? directory = null)
    {
        _directory = directory ?? Environment.GetEnvironmentVariable("EGGTCS_SPEECH_ASR_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "SpeechAsr");
        _readings = new(() => LoadReadings(Readings));
        _phraseReadings = new(() =>
        {
            var path = Path.Combine(_directory, "phrases_dict.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string[][]>>(File.ReadAllBytes(path)) ?? new()
                : new();
        });
    }

    internal bool IsAvailable =>
        OperatingSystem.IsMacOS() && File.Exists(Engine) && File.Exists(Model)
        && File.Exists(VoiceDetector) && File.Exists(Readings);

    internal async Task<SpeechResponseSuggestion?> RecognizeAsync(
        AudioBuffer recording, SpeechSentence sentence, CancellationToken cancellationToken
    )
    {
        var heard = await TranscribeRecordingAsync(recording, cancellationToken).ConfigureAwait(false);
        if (heard is null)
            return null;
        var (transcript, characters) = heard.Value;
        var ratings = SpeechResponseAlignment.Compare(
            sentence.ScoringCharacters, characters, _readings.Value, _phraseReadings.Value, out var reviewReason
        );
        return new(transcript, ratings, characters.Length, reviewReason);
    }

    /// <summary>
    /// Picture naming: splits the answer at pauses, recognizes each part, and finds where the target word was said.
    /// </summary>
    internal async Task<SpeechNamingSuggestion?> RecognizeNamingAsync(
        AudioBuffer recording, string target, CancellationToken cancellationToken
    )
    {
        if (!HasAudibleSignal(recording.Data))
            return null;
        var segments = SplitSegments(recording);
        if (segments.Count == 0)
            return null;

        var heard = new List<(int? StartMs, string Text)>();
        var temporary = Directory.CreateTempSubdirectory("eggtcs-naming-");
        try
        {
            for (var k = 0; k < segments.Count; k++)
            {
                var wave = Path.Combine(temporary.FullName, $"segment-{k}.wav");
                WriteWave(wave, PaddedSlice(recording, segments[k]));
                var text = await TranscribeAsync(wave, cancellationToken).ConfigureAwait(false);
                heard.Add((segments[k].StartMs, text.Normalize(NormalizationForm.FormKC)));
            }
            if (heard.All(h => SpeechSentence.GetScoringCharacters(h.Text).Length == 0))
            {
                // Segments too short for the model on their own: try the whole answer once.
                var whole = Path.Combine(temporary.FullName, "response.wav");
                WriteWave(whole, recording);
                var text = await TranscribeAsync(whole, cancellationToken).ConfigureAwait(false);
                heard = [(null, text.Normalize(NormalizationForm.FormKC))];
            }
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
        return ClassifyNaming(heard, target, _readings.Value, _phraseReadings.Value);
    }

    private static readonly string[] FillerWords = ["那个", "这个", "嗯", "啊", "呃", "哦", "噢", "唔"];

    /// <summary>Classifies the words separately from the availability of their onset times.</summary>
    internal static SpeechNamingSuggestion? ClassifyNaming(
        IReadOnlyList<(int? StartMs, string Text)> segments,
        string target,
        IReadOnlyDictionary<string, string[]> readings,
        IReadOnlyDictionary<string, string[][]> phrases
    )
    {
        var transcript = string.Join(" · ", segments
            .Where(s => SpeechSentence.GetScoringCharacters(s.Text).Length > 0)
            .Select(s => s.Text.Trim()));
        if (transcript.Length == 0)
            return null;

        var expected = SpeechSentence.GetScoringCharacters(target);
        var expectedText = string.Concat(expected);
        var hesitated = false;
        var firstAnswerFound = false;
        var firstAnswerUnclear = false;
        var earlierTargetUnclear = false;
        int? firstOnset = null;
        SpeechResponseReviewReason? reviewReason = null;

        foreach (var (segmentStart, part) in NamingParts(segments, expectedText))
        {
            if (part is null)
            {
                reviewReason = SpeechResponseReviewReason.SelfCorrection;
                firstAnswerUnclear |= !firstAnswerFound;
                continue;
            }
            if (part.Length == 0)
            {
                firstAnswerUnclear |= !firstAnswerFound;
                earlierTargetUnclear = true;
                continue;
            }

            var start = segmentStart;
            var answer = part;
            while (Array.Find(FillerWords, word => answer.StartsWith(word, StringComparison.Ordinal)) is { } filler)
            {
                hesitated = true;
                start = null;
                answer = answer[filler.Length..];
            }
            if (answer.Length == 0)
                continue;

            var characters = SpeechSentence.GetScoringCharacters(answer);
            var match = SpeechResponseAlignment.FindWithin(expected, characters, readings, phrases, out _, out var at);
            if (match == true)
            {
                var prefix = string.Concat(characters.Take(at));
                if (prefix == "问" || prefix.EndsWith("不是", StringComparison.Ordinal))
                    return new(transcript, "待确认", firstOnset, null, hesitated, reviewReason, "目标词前的内容有歧义，请人工核对");

                var startsWithTarget = at == 0;
                var introducesTarget = prefix is "是" or "就是";
                var earlierWrongWord = firstAnswerFound || (!startsWithTarget && !introducesTarget);
                var correctOnset = startsWithTarget && !earlierTargetUnclear ? start : null;
                if (!firstAnswerFound && !firstAnswerUnclear && !introducesTarget)
                    firstOnset = start;

                var outcome = firstAnswerUnclear ? "待确认" : earlierWrongWord ? "自我纠正" : "正确";
                return new(transcript, outcome, firstOnset, correctOnset, hesitated, reviewReason,
                    firstAnswerUnclear ? "首次回答需人工核对"
                    : firstOnset is null || correctOnset is null ? "无法定位完整的回答时间，请人工核对"
                    : earlierWrongWord ? "先说了其他词，后面才说对" : null);
            }
            if (answer == "问" && match == false)
            {
                firstAnswerUnclear |= !firstAnswerFound;
                continue;
            }

            if (!firstAnswerFound && !firstAnswerUnclear)
                firstOnset = start;
            firstAnswerUnclear |= !firstAnswerFound && match is null;
            firstAnswerFound = true;
            earlierTargetUnclear |= match is null;
        }

        return firstAnswerUnclear
            ? new(transcript, "待确认", firstOnset, null, hesitated, reviewReason, "首次回答需人工核对")
            : firstAnswerFound
                ? new(transcript, earlierTargetUnclear ? "待确认" : "错误", firstOnset, null, hesitated, reviewReason,
                    earlierTargetUnclear || firstOnset is null ? "词语或开口时间需人工核对" : null)
                : new(transcript, null, null, null, hesitated, reviewReason, "只听到语气词，请人工判断");
    }

    // Marks an explicit correction boundary with null. An empty string represents unrecognized audio.
    private static IEnumerable<(int? StartMs, string? Text)> NamingParts(
        IReadOnlyList<(int? StartMs, string Text)> segments,
        string expected
    )
    {
        foreach (var (segmentStart, transcript) in segments)
        {
            var start = segmentStart;
            var text = string.Concat(SpeechSentence.GetScoringCharacters(transcript));
            if (text.Length == 0)
            {
                yield return (start, text);
                continue;
            }
            while (SpeechResponseAlignment.FindCorrectionMarker(expected, text) is { } marker)
            {
                if (marker.Index > 0)
                    yield return (start, text[..marker.Index]);
                yield return (null, null);
                start = null;
                text = text[(marker.Index + marker.Length)..];
            }
            if (text.Length > 0)
                yield return (start, text);
        }
    }

    /// <summary>Splits PCM16 at pauses of at least <paramref name="pauseMs"/>; a part starts after 3 loud 20 ms windows.</summary>
    internal static IReadOnlyList<SpeechSegment> SplitSegments(AudioBuffer audio, int pauseMs = 250)
    {
        var segments = new List<SpeechSegment>();
        var window = audio.SampleRate / 50;
        var data = audio.Data;
        int run = 0, silent = 0, last = 0;
        int? start = null;
        int Ms(int frame) => (int)((long)frame * 1000 / audio.SampleRate);
        for (var i = 0; i + window <= audio.Frames; i += window)
        {
            double energy = 0;
            for (var j = i; j < i + window; j++)
            {
                double sample = BinaryPrimitives.ReadInt16LittleEndian(data[(j * audio.Channels * 2)..]);
                energy += sample * sample;
            }
            var active = energy / window >= 300d * 300;
            if (start is null)
            {
                run = active ? run + 1 : 0;
                if (run == 3) { start = i - 2 * window; last = i + window; silent = 0; }
            }
            else if (active) { last = i + window; silent = 0; }
            else if (++silent * 20 >= pauseMs)
            {
                segments.Add(new(Ms(start.Value), Ms(last), start.Value, last));
                start = null;
                run = 0;
            }
        }
        if (start is not null)
            segments.Add(new(Ms(start.Value), Ms(last), start.Value, last));
        return segments;
    }

    private static AudioBuffer PaddedSlice(AudioBuffer audio, SpeechSegment segment)
    {
        var from = Math.Max(0, segment.StartFrame - audio.SampleRate * 150 / 1000);
        var to = Math.Min(audio.Frames, segment.EndFrame + audio.SampleRate / 5);
        var bytesPerFrame = audio.Channels * 2;
        return new(audio.SampleRate, audio.Channels, audio.Data[(from * bytesPerFrame)..(to * bytesPerFrame)]);
    }

    private async Task<(string Transcript, string[] Characters)?> TranscribeRecordingAsync(
        AudioBuffer recording, CancellationToken cancellationToken
    )
    {
        if (!HasAudibleSignal(recording.Data))
            return null;

        var temporary = Directory.CreateTempSubdirectory("eggtcs-response-");
        try
        {
            var wave = Path.Combine(temporary.FullName, "response.wav");
            WriteWave(wave, recording);
            var transcript = await TranscribeAsync(wave, cancellationToken).ConfigureAwait(false);
            var characters = SpeechSentence.GetScoringCharacters(transcript.Normalize(NormalizationForm.FormKC));
            return characters.Length == 0 ? null : (transcript, characters);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private async Task<string> TranscribeAsync(string wave, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Engine)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-m", Model, "-a", wave, "--vad", VoiceDetector })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动本地识别模型。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("本地识别超时。");
        }
        await errors.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException("本地识别失败。");
        return (await output.ConfigureAwait(false)).Trim();
    }

    private static Dictionary<string, string[]> LoadReadings(string path)
    {
        var readings = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var colon = line.IndexOf(':');
            if (!line.StartsWith("U+", StringComparison.Ordinal) || colon < 3
                || !int.TryParse(line.AsSpan(2, colon - 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var codePoint)
                || !Rune.IsValid(codePoint))
                continue;
            var comment = line.IndexOf('#', colon);
            var values = line[(colon + 1)..(comment < 0 ? line.Length : comment)];
            readings[new Rune(codePoint).ToString()] =
                values.Normalize().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        return readings;
    }

    private static bool HasAudibleSignal(ReadOnlySpan<byte> pcm)
    {
        // Exclude short clicks and near-silent captures before allowing ASR to infer words.
        const int frameBytes = 16000 * 2 / 50;
        var activeFrames = 0;
        for (var offset = 0; offset + frameBytes <= pcm.Length; offset += frameBytes)
        {
            double energy = 0;
            for (var sample = offset; sample < offset + frameBytes; sample += 2)
            {
                var value = (double)BinaryPrimitives.ReadInt16LittleEndian(pcm[sample..]);
                energy += value * value;
            }
            if (energy >= 80d * 80 * (frameBytes / 2) && ++activeFrames >= 5)
                return true;
        }
        return false;
    }

    private static void WriteWave(string path, AudioBuffer recording)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + recording.Data.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)recording.Channels);
        writer.Write(recording.SampleRate);
        writer.Write(recording.SampleRate * recording.Channels * 2);
        writer.Write((short)(recording.Channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(recording.Data.Length);
        writer.Write(recording.Data);
    }
}
