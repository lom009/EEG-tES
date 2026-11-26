namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Training groups are views over a corpus; resource identity never depends on group size.</summary>
public sealed class SpeechSentenceListService
{
    public IReadOnlyList<SpeechSentenceList> GetLists(SpeechCorpus corpus, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var lists = new List<SpeechSentenceList>();
        for (long start = 0; start < corpus.Sentences.Count; start += size)
            lists.Add(
                new(lists.Count + 1, size, corpus.Sentences.Skip((int)start).Take(size).ToArray())
            );
        return lists.ToArray();
    }

    public SpeechSentence GetSentence(
        SpeechCorpus corpus,
        int size,
        int listNumber,
        int sentenceNumber
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(listNumber);
        var start = ((long)listNumber - 1) * size;
        if (start >= corpus.Sentences.Count)
            throw new ArgumentOutOfRangeException(nameof(listNumber));
        if (
            sentenceNumber <= 0
            || sentenceNumber > Math.Min((long)size, corpus.Sentences.Count - start)
        )
            throw new ArgumentOutOfRangeException(nameof(sentenceNumber));
        return corpus.Sentences[(int)(start + sentenceNumber - 1)];
    }
}
