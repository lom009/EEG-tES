using System.Text;
using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechCorpusConfigurationTests
{
    private const string Rule =
        """{"adaptiveScoring":{"expectedCharacterCount":7,"minimumCorrect":4}}""";

    [Fact]
    public void BuiltInConfigurationsAreCopiedAndLoaded()
    {
        var provider = new FileSpeechCorpusProvider();
        foreach (
            var (name, expected, minimum) in new[]
            {
                ("MSP", 7, 4),
                ("MHINT", 10, 8),
                ("MHINT-P", 10, 8),
            }
        )
        {
            Assert.True(
                File.Exists(
                    Path.Combine(AppContext.BaseDirectory, "SpeechCorpora", name, "corpus.json")
                )
            );
            var corpus = provider.Corpora.Single(c => c.Id == name);
            Assert.Equal(new(expected, minimum), corpus.AdaptiveScoring);
            Assert.Null(corpus.AdaptiveConfigurationError);
            Assert.All(
                corpus.Sentences,
                s => corpus.AdaptiveScoring!.ValidateSentence(s.ScoringCharacters.Count)
            );
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(7, 0)]
    [InlineData(7, -1)]
    [InlineData(7, 8)]
    public void RuleRejectsInvalidValues(int expected, int minimum) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AdaptiveScoringRule(expected, minimum)
        );

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"adaptiveScoring\":null}")]
    [InlineData("{\"adaptiveScoring\":{}}")]
    [InlineData("{\"adaptiveScoring\":{\"expectedCharacterCount\":7}}")]
    [InlineData("{\"adaptiveScoring\":{\"minimumCorrect\":4}}")]
    [InlineData("{\"adaptiveScoring\":{\"expectedCharacterCount\":0,\"minimumCorrect\":4}}")]
    [InlineData("{\"adaptiveScoring\":{\"expectedCharacterCount\":7,\"minimumCorrect\":8}}")]
    [InlineData("{\"adaptiveScoring\":{\"expectedCharacterCount\":7,\"minimumCorrect\":0}}")]
    [InlineData("{\"adaptiveScoring\":{\"expectedCharacterCount\":7,\"minimumCorrect\":-1}}")]
    [InlineData("{\"adaptiveScoring\":{\"expectedCharacterCount\":7,\"minimumCorrect\":4.5}}")]
    [InlineData("{\"adaptiveScoring\":{\"expectedCharacterCount\":7,\"minimumCorrect\":\"4\"}}")]
    [InlineData(
        "{\"adaptiveScoring\":{\"expectedCharacterCount\":2147483648,\"minimumCorrect\":4}}"
    )]
    public async Task InvalidConfigurationOnlyDisablesAdaptiveWithoutNameFallback(string? json)
    {
        using var directory = new CorpusDirectory();
        directory.Add("MSP", json); // Even a built-in name must not infer a rule.
        directory.Add("可用语料", Rule);
        var provider = new FileSpeechCorpusProvider(directory.Root);
        var bad = provider.Corpora.Single(c => c.Id == "MSP");
        Assert.Null(bad.AdaptiveScoring);
        Assert.Contains("MSP", bad.AdaptiveConfigurationError);
        Assert.Contains(
            Path.Combine(directory.Root, "MSP", "corpus.json"),
            bad.AdaptiveConfigurationError
        );
        Assert.NotNull(provider.Corpora.Single(c => c.Id == "可用语料").AdaptiveScoring);
        using var session = new SpeechTrainingSession(
            provider,
            new ControllableSpeechAudioPlayer()
        );
        Assert.True(session.CanStart);
        Assert.False(session.CanUseAdaptiveMode);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        Assert.False(session.CanStart);
        await session.StartAsync();
        Assert.False(session.Started);
        Assert.Equal(bad.AdaptiveConfigurationError, session.Error);
        session.SelectMode(SpeechTrainingMode.FixedRate);
        await session.StartAsync();
        Assert.True(session.IsPlaying);
        Assert.Null(session.Error);
    }

    [Fact]
    public void Utf8BomIsAcceptedAndInvalidUtf8ReportsConfigurationPath()
    {
        using var directory = new CorpusDirectory();
        var path = directory.Add("自定义", Rule);
        File.WriteAllText(path, Rule, new UTF8Encoding(true));
        Assert.Equal(
            new(7, 4),
            new FileSpeechCorpusProvider(directory.Root).Corpora[0].AdaptiveScoring
        );
        File.WriteAllBytes(path, [0xff, 0xff]);
        var corpus = new FileSpeechCorpusProvider(directory.Root).Corpora[0];
        Assert.Null(corpus.AdaptiveScoring);
        Assert.Contains(path, corpus.AdaptiveConfigurationError);
    }

    [Fact]
    public async Task NewNamesUseIdenticalConfiguredRulesAndKeepImmutableSessionSnapshot()
    {
        using var directory = new CorpusDirectory();
        var path = directory.Add("新语料", Rule);
        directory.Add("OtherCorpus", Rule);
        var original = new FileSpeechCorpusProvider(directory.Root);
        Assert.Equal(original.Corpora[0].AdaptiveScoring, original.Corpora[1].AdaptiveScoring);
        foreach (var corpus in original.Corpora)
        {
            var a = new AdaptiveSpeechState().Submit(
                corpus.Sentences[0].Id,
                1,
                corpus.AdaptiveScoring!,
                4,
                7
            );
            Assert.True(a.Passed);
            Assert.Equal(0.75, a.NextR);
        }
        using var session = new SpeechTrainingSession(
            original,
            new ControllableSpeechAudioPlayer()
        );
        session.SelectCorpus("新语料");
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        await session.StartAsync();
        File.WriteAllText(path, Rule.Replace("\"minimumCorrect\":4", "\"minimumCorrect\":5"));
        var updated = new FileSpeechCorpusProvider(directory.Root).Corpora.Single(c =>
            c.Id == "新语料"
        );
        Assert.Equal(5, updated.AdaptiveScoring!.MinimumCorrect);
        Assert.False(updated.AdaptiveScoring.IsPassed(4, 7));
        Assert.Equal(4, session.Corpus.AdaptiveScoring!.MinimumCorrect);
        session.RateAll(false);
        for (var i = 0; i < 4; i++)
            session.Rate(i, true);
        await session.SaveNextAsync();
        Assert.True(session.Result!.Sentences[0].Adaptive!.Submission.Passed);
        Assert.Equal(new(7, 4), session.Result.Settings.AdaptiveScoring);
    }

    [Fact]
    public void OnlyCurrentListIsCheckedAndModeIsPreservedWhenSelectionBecomesUnavailable()
    {
        using var directory = new CorpusDirectory();
        directory.Add("自定义", Rule, "一二三，四五六七。", "一二三四五六");
        directory.Add("无配置", null);
        using var session = new SpeechTrainingSession(
            new FileSpeechCorpusProvider(directory.Root),
            new ControllableSpeechAudioPlayer(),
            1
        );
        session.SelectCorpus("自定义");
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        Assert.True(session.CanStart);
        session.SelectSentenceList("02");
        Assert.False(session.CanStart);
        Assert.False(session.CanUseAdaptiveMode);
        Assert.Equal(SpeechTrainingMode.AdaptiveRate, session.Mode);
        Assert.Contains("句子 2", session.AdaptiveUnavailableReason);
        session.SelectSentenceList("01");
        Assert.True(session.CanStart);
        session.SelectCorpus("无配置");
        Assert.False(session.CanStart);
        Assert.Equal(SpeechTrainingMode.AdaptiveRate, session.Mode);
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.True(session.CanStart);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"ssn\": 7, \"babble\": \"Babble-MSP.wav\"}")]
    [InlineData("{\"ssn\": \"../escape.wav\", \"babble\": \"Babble-MSP.wav\"}")]
    public void InvalidNoiseConfigurationDoesNotDisableScoringOrOtherNoise(string mapping)
    {
        using var directory = new CorpusDirectory();
        directory.Add(
            "Custom",
            "{\"adaptiveScoring\":{\"expectedCharacterCount\":7,\"minimumCorrect\":4},\"snrNoise\":"
                + mapping
                + "}"
        );
        var provider = new FileSpeechCorpusProvider(directory.Root);
        using var session = new SpeechTrainingSession(
            provider,
            new ControllableSpeechAudioPlayer()
        );
        Assert.True(session.CanUseAdaptiveMode);
        Assert.NotNull(session.SnrUnavailableReason);
        Assert.Null(provider.Corpora[0].AdaptiveConfigurationError);
        if (mapping.Contains("Babble-MSP"))
            Assert.Null(session.GetSnrUnavailableReason(SpeechNoiseKind.Babble));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{\"expectedCharacterCount\":0,\"minimumCorrect\":4}")]
    [InlineData("{\"expectedCharacterCount\":10,\"minimumCorrect\":8}")]
    public async Task FixedSnrCanUseNoiseWithoutUsableAdaptiveScoring(string? scoring)
    {
        using var directory = new CorpusDirectory();
        var json =
            "{"
            + (scoring is null ? "" : "\"adaptiveScoring\":" + scoring + ",")
            + "\"snrNoise\":{\"ssn\":\"SSN-MSP.wav\",\"babble\":\"Babble-MSP.wav\"}}";
        directory.Add("Custom", json);
        var provider = new FileSpeechCorpusProvider(directory.Root);
        using var session = new SpeechTrainingSession(
            provider,
            new ControllableSpeechAudioPlayer()
        );
        Assert.False(session.CanUseAdaptiveMode);
        Assert.False(session.CanUseAdaptiveSnr);
        Assert.True(session.CanUseFixedSnr);
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        session.SetFixedSnr(-10);
        await session.StartAsync();
        Assert.Null(session.Error);
        session.RateAll(true);
        await session.SaveNextAsync();
        Assert.Null(session.Result!.Settings.AdaptiveScoring);
        Assert.Equal(-10, session.Result.Sentences[0].Noise!.Settings.SnrDb);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public void BrokenJsonDisablesBothNoiseOptionsWithConfigurationReason(string json)
    {
        using var directory = new CorpusDirectory();
        directory.Add("Custom", json);
        using var session = new SpeechTrainingSession(
            new FileSpeechCorpusProvider(directory.Root),
            new ControllableSpeechAudioPlayer()
        );
        Assert.Contains("corpus.json", session.GetNoiseUnavailableReason(SpeechNoiseKind.Ssn));
        Assert.Contains("corpus.json", session.GetNoiseUnavailableReason(SpeechNoiseKind.Babble));
        Assert.False(session.CanUseFixedSnr);
        Assert.True(session.CanStart);
    }

    [Fact]
    public void MissingSsnDoesNotDisableFixedBabble()
    {
        using var directory = new CorpusDirectory();
        directory.Add("Custom", "{\"snrNoise\":{\"babble\":\"Babble-MSP.wav\"}}");
        using var session = new SpeechTrainingSession(
            new FileSpeechCorpusProvider(directory.Root),
            new ControllableSpeechAudioPlayer()
        );
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        Assert.False(session.CanStart);
        session.SelectNoise(SpeechNoiseKind.Babble);
        Assert.True(session.CanStart);
        Assert.False(session.CanUseAdaptiveSnr);
    }

    private sealed class CorpusDirectory : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "speech-config-" + Guid.NewGuid().ToString("N"));

        public string Add(string name, string? json, params string[] sentences)
        {
            var directory = Path.Combine(Root, name);
            Directory.CreateDirectory(directory);
            if (sentences.Length == 0)
                sentences = ["一二三四五六七"];
            File.WriteAllLines(Path.Combine(directory, "sentences.txt"), sentences);
            for (var i = 0; i < sentences.Length; i++)
                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "SpeechCorpora", "MSP", "1.wav"),
                    Path.Combine(directory, $"{i + 1}.wav")
                );
            var config = Path.Combine(directory, "corpus.json");
            if (json is not null)
                File.WriteAllText(config, json);
            return config;
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
