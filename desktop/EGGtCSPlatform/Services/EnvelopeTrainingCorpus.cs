using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace EGGtCSPlatform.Services;

public sealed record EnvelopeTrainingSentence(string Text, string? AudioFile = null);
public sealed record EnvelopeTrainingSentenceTable(string Name, IReadOnlyList<EnvelopeTrainingSentence> Sentences)
{
    public override string ToString() => Name;
}
public sealed record EnvelopeTrainingCorpus(string Name, string Voice,
    IReadOnlyList<EnvelopeTrainingSentenceTable> Tables, string? Directory = null)
{
    public static EnvelopeTrainingCorpus Demo { get; } = new("示例语料", "示例合成语音",
        [new("01", EnvelopeTrainingAudio.DemoSentences.Select(text => new EnvelopeTrainingSentence(text)).ToArray())]);
    public override string ToString() => Name;

    public static async Task<EnvelopeTrainingCorpus> LoadAsync(string manifest)
    {
        var corpus = JsonSerializer.Deserialize<EnvelopeTrainingCorpus>(await File.ReadAllTextAsync(manifest),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("语料清单为空。");
        if (string.IsNullOrWhiteSpace(corpus.Name) || string.IsNullOrWhiteSpace(corpus.Voice)
            || corpus.Tables is not { Count: > 0 } || corpus.Tables.Count > 100)
            throw new InvalidDataException("语料清单须含名称、载体及句表。");
        var root = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        foreach (var table in corpus.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Name) || table.Sentences is not { Count: > 0 } || table.Sentences.Count > 100)
                throw new InvalidDataException("每个句表须包含 1–100 句。");
            foreach (var sentence in table.Sentences)
            {
                if (string.IsNullOrWhiteSpace(sentence.Text) || sentence.Text.EnumerateRunes().Count() > 20
                    || string.IsNullOrWhiteSpace(sentence.AudioFile))
                    throw new InvalidDataException("每句须含 1–20 个字及对应 PCM WAV 路径。");
                var path = Path.GetFullPath(Path.Combine(root, sentence.AudioFile));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("音频须位于语料清单所在目录内。");
                using var audio = File.OpenRead(path);
                // Validate all files before mutating the current training session.
                EnvelopeTrainingAudio.Prepare(audio, sentence.Text, 2, 1);
            }
        }
        return corpus with { Directory = root };
    }
}
