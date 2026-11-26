using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechSentenceListServiceTests
{
    [Theory]
    [InlineData(10, 10, 10)]
    [InlineData(20, 5, 20)]
    [InlineData(30, 4, 10)]
    [InlineData(50, 2, 50)]
    [InlineData(150, 1, 100)]
    [InlineData(int.MaxValue, 1, 100)]
    public void GroupsCoverEveryResourceExactlyOnce(int size, int count, int lastCount)
    {
        var corpus = new FileSpeechCorpusProvider().Corpora[0];
        var service = new SpeechSentenceListService();
        var lists = service.GetLists(corpus, size);
        Assert.Equal(count, lists.Count);
        Assert.Equal(lastCount, lists[^1].Sentences.Count);
        Assert.Equal(corpus.Sentences, lists.SelectMany(l => l.Sentences));
        foreach (var list in lists)
            for (var n = 1; n <= list.Sentences.Count; n++)
                Assert.Same(
                    list.Sentences[n - 1],
                    service.GetSentence(corpus, size, list.Number, n)
                );
        Assert.Equal(100, lists[^1].ResourceEnd);
    }

    [Fact]
    public void ResourceIdentityIsIndependentOfGroupingAndPracticeBoundary()
    {
        var corpora = new FileSpeechCorpusProvider().Corpora;
        var service = new SpeechSentenceListService();
        Assert.Same(
            service.GetSentence(corpora[0], 30, 4, 1),
            service.GetSentence(corpora[0], 10, 10, 1)
        );
        Assert.Equal("MSP/091", service.GetSentence(corpora[0], 30, 4, 1).Id);
        Assert.Equal("MHINT-P/020", service.GetSentence(corpora[2], 30, 1, 20).Id);
        Assert.Equal("MHINT-P/021", service.GetSentence(corpora[2], 30, 1, 21).Id);
        Assert.Equal("MHINT-P/040", service.GetSentence(corpora[2], 30, 2, 10).Id);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-1, 1, 1)]
    [InlineData(30, 0, 1)]
    [InlineData(30, 5, 1)]
    [InlineData(30, 4, 11)]
    [InlineData(30, 1, 31)]
    [InlineData(30, 1, 0)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void InvalidRequestsAreRejected(int size, int list, int sentence)
    {
        var corpus = new FileSpeechCorpusProvider().Corpora[0];
        var service = new SpeechSentenceListService();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.GetSentence(corpus, size, list, sentence)
        );
        if (size <= 0)
            Assert.Throws<ArgumentOutOfRangeException>(() => service.GetLists(corpus, size));
    }

    [Fact]
    public async Task SizeSelectionResetsListAndLocksDuringTrainingWithPartialResults()
    {
        using var session = new SpeechTrainingSession(
            new FileSpeechCorpusProvider(),
            new ControllableSpeechAudioPlayer()
        );
        session.SelectSentenceList("05");
        session.SelectListSize(30);
        Assert.Equal("01", session.SentenceList.Id);
        session.SelectCorpus("MHINT-P");
        Assert.Equal(30, session.ListSize);
        session.SelectSentenceList("02");
        Assert.Equal(10, session.SentenceList.Sentences.Count);
        Assert.Equal("MHINT-P/031", session.CurrentSentence.Id);
        await session.StartAsync();
        session.SelectListSize(10);
        Assert.Equal(30, session.ListSize);
        for (var i = 0; i < 10; i++)
        {
            session.RateAll(true);
            await session.SaveNextAsync();
        }
        Assert.True(session.Ended);
        Assert.Equal(10, session.Result!.SentenceCount);
        Assert.Equal(30, session.Result.Settings.ListSize);
        Assert.Equal(31, session.Result.Settings.ResourceStart);
        Assert.Equal(40, session.Result.Settings.ResourceEnd);
        Assert.Equal(Enumerable.Range(1, 10), session.Result.Sentences.Select(s => s.Number));
    }
}
