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
/// FirstOnsetMs is the start of the first real word; CorrectOnsetMs is the start of the segment holding the target.
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
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "SpeechAsr");
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

        var heard = new List<(int StartMs, string Text)>();
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
                heard = [(segments[0].StartMs, text.Normalize(NormalizationForm.FormKC))];
            }
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
        return ClassifyNaming(heard, target, _readings.Value, _phraseReadings.Value);
    }

    private static readonly string[] FillerWords = ["那个", "这个", "就是"];
    private const string FillerCharacters = "嗯恩问啊呃额是哦噢唔";

    /// <summary>True when the characters are only hesitation sounds such as 嗯 or 那个 (the ASR often hears 嗯 as 问).</summary>
    internal static bool IsFiller(IEnumerable<string> characters)
    {
        var text = string.Concat(characters);
        foreach (var word in FillerWords)
            text = text.Replace(word, "", StringComparison.Ordinal);
        return text.All(c => FillerCharacters.Contains(c));
    }

    /// <summary>Decides the naming outcome from recognized segments in time order. Pure, so it can be tested without the model.</summary>
    internal static SpeechNamingSuggestion? ClassifyNaming(
        IReadOnlyList<(int StartMs, string Text)> segments,
        string target,
        IReadOnlyDictionary<string, string[]> readings,
        IReadOnlyDictionary<string, string[][]> phrases
    )
    {
        var spoken = segments
            .Select(s => (s.StartMs, s.Text, Characters: SpeechSentence.GetScoringCharacters(s.Text)))
            .Where(s => s.Characters.Length > 0)
            .ToArray();
        if (spoken.Length == 0)
            return null;
        var transcript = string.Join(" · ", spoken.Select(s => s.Text.Trim()));
        var expected = SpeechSentence.GetScoringCharacters(target);
        var hesitated = false;
        int? firstOnset = null;

        if (SpeechResponseAlignment.FindWithin(
                expected, spoken.SelectMany(s => s.Characters).ToArray(), readings, phrases, out var marker) is null
            && marker is not null)
        {
            var first = spoken.FirstOrDefault(s => !IsFiller(s.Characters));
            return new(transcript, "待确认", first.Characters is null ? null : first.StartMs, null,
                spoken[0].StartMs != first.StartMs, marker, "回答中有改口用语，请人工核对");
        }

        foreach (var (start, _, characters) in spoken)
        {
            var match = SpeechResponseAlignment.FindWithin(expected, characters, readings, phrases, out _, out var at);
            if (match == false)
            {
                if (IsFiller(characters)) hesitated = true;
                else firstOnset ??= start;
                continue;
            }
            if (firstOnset is not null)
                return match == true
                    ? new(transcript, "自我纠正", firstOnset, start, hesitated, null, "先说了其他词，后面才说对")
                    : new(transcript, "待确认", firstOnset, null, hesitated, null, "有不确定的字，请人工判断");

            firstOnset = start;
            if (match is null)
                return new(transcript, "待确认", start, null, hesitated, null, "有不确定的字，请人工判断");
            var before = characters.Take(at).ToArray();
            if (!IsFiller(before))
                return new(transcript, "待确认", start, null, hesitated, null, "同一段里先说了其他词，说对的时间需人工确认");
            return before.Length == 0
                ? new(transcript, "正确", start, start, hesitated, null, null)
                : new(transcript, "正确", start, start, true, null, "语气词和目标词连在一起，反应时可能偏早");
        }
        return firstOnset is null
            ? new(transcript, null, null, null, true, null, "只听到语气词，请人工判断")
            : new(transcript, "错误", firstOnset, null, hesitated, null, null);
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
