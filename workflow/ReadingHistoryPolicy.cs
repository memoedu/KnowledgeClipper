namespace KnowledgeClipper.Workflow;

public static class ReadingHistoryPolicy
{
    public static LanguageLookupResult? Meaning(LanguageHistoryMatch history, string? context) =>
        string.IsNullOrWhiteSpace(context) ? null : history.Candidates.Select(c => c.Lookup)
            .FirstOrDefault(lookup => lookup != null && lookup.ContextText == context);

    public static bool ReplayOnly(bool quick, LanguageHistoryMatch history) => quick && history.Candidates.Count != 0;

    public static async Task<LanguageHistoryCandidate?> Recording(LanguageHistoryMatch history, CaptureMaterial material,
        bool contextEnabled, Task<LanguageLookupResult?> meaning)
    {
        var expected = LanguageHistoryLookup.Key(ReadingSpeech.Text(material, contextEnabled));
        var candidates = history.Candidates.Where(c => c.CanPlay && LanguageHistoryLookup.Key(c.AudioText) == expected).ToArray();
        if (!contextEnabled || candidates.Length == 0) return candidates.FirstOrDefault();
        var sameSentence = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(material.ContextText) &&
            c.AudioContextText == material.ContextText);
        if (sameSentence != null) return sameSentence;
        var pronunciation = (await meaning)?.Pronunciation;
        if (string.IsNullOrWhiteSpace(pronunciation)) return null;
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Pronunciation) &&
            PronunciationKey(c.Pronunciation) == PronunciationKey(pronunciation));
    }

    private static string PronunciationKey(string value) =>
        new(value.Where(c => !char.IsWhiteSpace(c) && c is not ('/' or '[' or ']')).ToArray());
}
