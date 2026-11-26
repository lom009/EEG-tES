using System.Security.Cryptography;
using System.Text;
using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechCorpusTests
{
    private static string BuiltInRoot => Path.Combine(AppContext.BaseDirectory, "SpeechCorpora");

    [Fact]
    public void BuiltInResourcesMatchBaselineAndIdentifiers()
    {
        Assert.False(File.Exists(Path.Combine(BuiltInRoot, "catalog.json")));
        Assert.False(Directory.Exists(Path.Combine(BuiltInRoot, "audio")));
        var provider = new FileSpeechCorpusProvider(BuiltInRoot + Path.DirectorySeparatorChar);
        Assert.Equal(new[] { "MSP", "MHINT", "MHINT-P" }, provider.Corpora.Select(c => c.Id));
        Assert.Equal(new[] { 100, 240, 40 }, provider.Corpora.Select(c => c.Sentences.Count));
        var rows = File.ReadAllLines(
                Path.Combine(AppContext.BaseDirectory, "SpeechResourceHashes.csv")
            )
            .Skip(1)
            .Select(row => row.Split(',').Select(x => x.Trim('"')).ToArray())
            .ToArray();
        Assert.Equal(380, rows.Count(r => r[0] == "audio"));
        Assert.Equal(3, rows.Count(r => r[0] == "generated-text"));
        var actual = Directory
            .EnumerateFiles(BuiltInRoot, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(BuiltInRoot, p).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var relative = row[3];
            var file = Path.Combine(BuiltInRoot, relative);
            Assert.True(actual.Contains(relative), relative);
            Assert.Equal(row[4], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            if (row[0] == "audio")
                Assert.Equal(row[2], row[4]);
        }
        foreach (var corpus in provider.Corpora)
            for (var i = 0; i < corpus.Sentences.Count; i++)
            {
                var sentence = corpus.Sentences[i];
                Assert.Equal($"{corpus.Id}/{i + 1:000}", sentence.Id);
                Assert.Equal($"{corpus.Id}/{i + 1}.wav", sentence.AudioRelativePath);
                Assert.Equal(i + 1, sentence.Number);
                var audio = provider.ReadAudio(sentence);
                Assert.Equal(16000, audio.SampleRate);
                Assert.Equal(1, audio.Channels);
                Assert.True(audio.Duration > TimeSpan.Zero);
                Assert.Contains(audio.GetWaveform(), p => p.Envelope > 0);
            }
    }

    [Fact]
    public async Task AppendedResourcesAndNewCorporaAreDiscoveredWithoutChangingOldIdentity()
    {
        using var resources = new ResourceDirectory(copyBuiltIn: true);
        var before = new FileSpeechCorpusProvider(resources.Root);
        resources.Add("MSP", "新句子", "下一句话");
        resources.Add("中文语料10", "十号语料");
        resources.Add("中文语料2", "二号语料");
        var after = new FileSpeechCorpusProvider(resources.Root);
        Assert.Equal(100, before.Corpora[0].Sentences.Count);
        Assert.Equal(102, after.Corpora[0].Sentences.Count);
        Assert.Equal(
            new[] { "MSP", "MHINT", "MHINT-P", "中文语料2", "中文语料10" },
            after.Corpora.Select(c => c.Id)
        );
        Assert.Equal(before.Corpora[2].Sentences, after.Corpora[2].Sentences);
        Assert.Equal(before.Corpora[0].Sentences, after.Corpora[0].Sentences.Take(100));
        using var session = new SpeechTrainingSession(after, new ControllableSpeechAudioPlayer());
        session.SelectSentenceList("06");
        await session.StartAsync();
        session.RateAll(true);
        await session.SaveNextAsync();
        session.RateAll(false);
        await session.SaveNextAsync();
        Assert.True(session.Ended);
        Assert.Equal(2, session.Result!.Sentences.Count);
        Assert.Equal(7, session.Result.RatedCount);
        Assert.Equal(3, session.Result.CorrectCount);
        Assert.Equal(3d / 7, session.Result.Accuracy);
        Assert.Equal(101, session.Result.Settings.ResourceStart);
        Assert.Equal(102, session.Result.Settings.ResourceEnd);
        Assert.Equal("MSP/101", session.Result.Sentences[0].SentenceId);
        Assert.Equal(1, session.Result.Sentences[0].Number);
    }

    [Fact]
    public void UnpaddedAudioNamesGrowBeyondThreeDigits()
    {
        using var resources = new ResourceDirectory();
        resources.Add("Corpus", Enumerable.Range(1, 1000).Select(n => $"句子{n}").ToArray());
        var provider = new FileSpeechCorpusProvider(resources.Root);
        foreach (var number in new[] { 1, 9, 10, 99, 100, 999, 1000 })
        {
            var sentence = provider.Corpora[0].Sentences[number - 1];
            Assert.Equal($"句子{number}", sentence.Text);
            Assert.Equal($"Corpus/{number}.wav", sentence.AudioRelativePath);
            Assert.Equal($"Corpus/{number:000}", sentence.Id);
            Assert.True(provider.ReadAudio(sentence).Duration > TimeSpan.Zero);
        }
    }

    [Fact]
    public void Utf8BomWhitespaceAndBlankLinesPreserveChineseAndDigits()
    {
        using var resources = new ResourceDirectory();
        var list = resources.Add("中文", "第一句", "第二句");
        File.WriteAllText(
            Path.Combine(list, "sentences.txt"),
            "\uFEFF\n  第一行正文  \r\n\r\n2026年第二句\t\n",
            new UTF8Encoding(false)
        );
        var sentences = new FileSpeechCorpusProvider(resources.Root).Corpora[0].Sentences;
        Assert.Equal(new[] { "第一行正文", "2026年第二句" }, sentences.Select(s => s.Text));
    }

    [Theory]
    [InlineData("empty-root")]
    [InlineData("empty-corpus")]
    [InlineData("nested-list")]
    [InlineData("empty-text")]
    [InlineData("encoding")]
    [InlineData("punctuation")]
    [InlineData("missing-audio")]
    [InlineData("extra-audio")]
    [InlineData("audio-case")]
    [InlineData("audio-number")]
    [InlineData("padded-duplicate")]
    [InlineData("text-case")]
    public void InvalidResourcesFailWithLocation(string kind)
    {
        using var resources = new ResourceDirectory();
        var list = kind == "empty-root" ? resources.Root : resources.Add("测试", "句子");
        var text = Path.Combine(list, "sentences.txt");
        var audio = Path.Combine(list, "1.wav");
        if (kind == "nested-list")
            Directory.CreateDirectory(Path.Combine(list, "01"));
        if (kind == "empty-corpus")
        {
            File.Delete(text);
            File.Delete(audio);
        }
        if (kind == "empty-text")
            File.WriteAllText(text, " \n\t");
        if (kind == "encoding")
            File.WriteAllBytes(text, [0xff]);
        if (kind == "punctuation")
            File.WriteAllText(text, "！？");
        if (kind == "missing-audio")
            File.Delete(audio);
        if (kind == "extra-audio")
            File.Copy(audio, Path.Combine(list, "2.wav"));
        if (kind == "audio-case")
            File.Move(audio, Path.Combine(list, "1.WAV"));
        if (kind == "audio-number")
            File.Move(audio, Path.Combine(list, "001.wav"));
        if (kind == "padded-duplicate")
            File.Copy(audio, Path.Combine(list, "001.wav"));
        if (kind == "text-case")
            File.Move(text, Path.Combine(list, "Sentences.txt"));
        var error = Record.Exception(() => new FileSpeechCorpusProvider(resources.Root));
        Assert.True(error is InvalidDataException or FileNotFoundException);
        Assert.Contains(resources.Root, error!.Message);
    }

    [Fact]
    public void CaseSensitiveHostsRejectDuplicateIdentifiers()
    {
        using var resources = new ResourceDirectory();
        resources.Add("Corpus", "句子");
        // Windows normally cannot create these duplicates; execute the actual collision checks on case-sensitive hosts.
        if (Directory.Exists(Path.Combine(resources.Root, "corpus")))
            return;
        resources.Add("corpus", "句子");
        Assert.Throws<InvalidDataException>(() => new FileSpeechCorpusProvider(resources.Root));
    }

    [Fact]
    public void AudioCannotEscapeRootAndCorruptAudioDoesNotFallback()
    {
        var provider = new FileSpeechCorpusProvider();
        Assert.Throws<InvalidDataException>(() =>
            provider.ReadAudio(new("bad", 1, "句子", "../outside.wav"))
        );
        using var resources = new ResourceDirectory();
        var list = resources.Add("测试", "句子");
        File.WriteAllText(Path.Combine(list, "1.wav"), "broken");
        var custom = new FileSpeechCorpusProvider(resources.Root);
        Assert.Throws<InvalidDataException>(() => custom.ReadAudio(custom.Corpora[0].Sentences[0]));
    }

    private sealed class ResourceDirectory : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "speech-directory-tests-" + Guid.NewGuid());

        public ResourceDirectory(bool copyBuiltIn = false)
        {
            Directory.CreateDirectory(Root);
            if (!copyBuiltIn)
                return;
            foreach (
                var file in Directory.EnumerateFiles(BuiltInRoot, "*", SearchOption.AllDirectories)
            )
            {
                var target = Path.Combine(Root, Path.GetRelativePath(BuiltInRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        public string Add(string corpus, params string[] texts)
        {
            var directory = Path.Combine(Root, corpus);
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "sentences.txt");
            var existing = File.Exists(file) ? File.ReadAllLines(file) : [];
            File.WriteAllLines(file, existing.Concat(texts), new UTF8Encoding(false));
            for (var i = 0; i < texts.Length; i++)
                File.Copy(
                    Path.Combine(BuiltInRoot, "MSP/1.wav"),
                    Path.Combine(directory, $"{existing.Length + i + 1}.wav")
                );
            return directory;
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
