namespace EGGtCSPlatform.SpeechTraining;

public enum SpeechResponseReviewReason
{
    LengthMismatch,
    SelfCorrection,
}

public static class SpeechResponseAlignment
{
    private static readonly string[] CorrectionMarkers = ["不对", "说错了", "我重说", "重新说", "再说一遍"];

    /// <summary>Locates the first explicit correction marker outside the expected text.</summary>
    public static (int Index, int Length)? FindCorrectionMarker(string expected, string heard)
    {
        (int Index, int Length)? first = null;
        foreach (var marker in CorrectionMarkers)
        {
            if (expected.Contains(marker, StringComparison.Ordinal)) continue;
            var index = heard.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (first is null || index < first.Value.Index))
                first = (index, marker.Length);
        }
        return first;
    }

    /// <summary>Scores the same positions when a response can be safely compared.</summary>
    public static bool?[] Compare(
        IReadOnlyList<string> reference,
        IReadOnlyList<string> response,
        IReadOnlyDictionary<string, string[]> characterReadings,
        IReadOnlyDictionary<string, string[][]> phraseReadings,
        out SpeechResponseReviewReason? reviewReason
    )
    {
        var ratings = new bool?[reference.Count];
        var expectedText = string.Concat(reference);
        var heardText = string.Concat(response);
        reviewReason = null;
        if (FindCorrectionMarker(expectedText, heardText) is not null)
            reviewReason = SpeechResponseReviewReason.SelfCorrection;
        else if (reference.Count != response.Count)
            reviewReason = SpeechResponseReviewReason.LengthMismatch;
        if (reviewReason is not null)
            return ratings;

        var expectedReadings = ResolveReadings(reference, characterReadings, phraseReadings);
        var heardReadings = ResolveReadings(response, characterReadings, phraseReadings);
        for (var i = 0; i < reference.Count; i++)
            ratings[i] = Rate(reference[i], response[i], expectedReadings[i], heardReadings[i]);
        return ratings;
    }

    /// <summary>
    /// Looks for the target word anywhere in a free answer such as 「苹果苹果」 or 「嗯，是苹果」.
    /// Returns true when some span matches (same character or same reading), false when no span can,
    /// and null when the best span depends on a character with several readings.
    /// </summary>
    public static bool? FindWithin(
        IReadOnlyList<string> reference,
        IReadOnlyList<string> response,
        IReadOnlyDictionary<string, string[]> characterReadings,
        IReadOnlyDictionary<string, string[][]> phraseReadings,
        out SpeechResponseReviewReason? reviewReason
    ) => FindWithin(reference, response, characterReadings, phraseReadings, out reviewReason, out _);

    /// <summary>Same as <see cref="FindWithin(IReadOnlyList{string}, IReadOnlyList{string}, IReadOnlyDictionary{string, string[]}, IReadOnlyDictionary{string, string[][]}, out SpeechResponseReviewReason?)"/>, also returning where the match starts (-1 when none).</summary>
    public static bool? FindWithin(
        IReadOnlyList<string> reference,
        IReadOnlyList<string> response,
        IReadOnlyDictionary<string, string[]> characterReadings,
        IReadOnlyDictionary<string, string[][]> phraseReadings,
        out SpeechResponseReviewReason? reviewReason,
        out int matchStart
    )
    {
        reviewReason = null;
        matchStart = -1;
        var expectedText = string.Concat(reference);
        var heardText = string.Concat(response);
        if (FindCorrectionMarker(expectedText, heardText) is not null)
        {
            reviewReason = SpeechResponseReviewReason.SelfCorrection;
            return null;
        }
        if (reference.Count == 0 || response.Count < reference.Count)
            return false;

        var expectedReadings = ResolveReadings(reference, characterReadings, phraseReadings);
        var heardReadings = ResolveReadings(response, characterReadings, phraseReadings);
        var uncertain = false;
        for (var start = 0; start + reference.Count <= response.Count; start++)
        {
            bool? span = true;
            for (var i = 0; i < reference.Count && span != false; i++)
            {
                var rating = Rate(reference[i], response[start + i], expectedReadings[i], heardReadings[start + i]);
                if (rating != true) span = rating;
            }
            if (span == true)
            {
                matchStart = start;
                return true;
            }
            uncertain |= span is null;
        }
        return uncertain ? null : false;
    }

    private static bool? Rate(string expected, string heard, string[] expectedReadings, string[] heardReadings)
    {
        if (expected == heard) return true;
        if (!expectedReadings.Intersect(heardReadings, StringComparer.Ordinal).Any()) return false;
        return expectedReadings.Length == 1 && heardReadings.Length == 1 ? true : null;
    }

    private static string[][] ResolveReadings(
        IReadOnlyList<string> characters,
        IReadOnlyDictionary<string, string[]> characterReadings,
        IReadOnlyDictionary<string, string[][]> phraseReadings
    )
    {
        var readings = new string[characters.Count][];
        for (var start = 0; start < characters.Count;)
        {
            string[][]? phrase = null;
            for (var length = characters.Count - start; length >= 2; length--)
            {
                var text = string.Concat(characters.Skip(start).Take(length));
                if (phraseReadings.TryGetValue(text, out phrase) && phrase.Length == length)
                    break;
                phrase = null;
            }
            if (phrase is null)
            {
                readings[start] = characterReadings.GetValueOrDefault(characters[start]) ?? [];
                start++;
            }
            else
            {
                phrase.CopyTo(readings, start);
                start += phrase.Length;
            }
        }
        return readings;
    }
}
