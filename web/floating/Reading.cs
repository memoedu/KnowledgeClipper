using KnowledgeClipper.Workflow;
using System.Text.Json;
using static Browser;

public sealed partial class BrowserSession
{
    async Task Read(bool play, bool generate)
    {
        var key = LanguageHistoryLookup.Key(material.SelectedText);
        var records = await Port<LanguageHistoryCandidate[]>("history", key);
        var history = new LanguageHistoryMatch(records.Where(x => x.SessionId != draft.Id).ToArray());
        var sentence = material.LookupContextText ?? material.ContextText;
        Task<LanguageLookupResult?> Meaning() => Lookup();
        async Task<LanguageLookupResult?> Lookup()
        {
            if (draft.Lookup != null && draft.Lookup.ContextText == sentence) return draft.Lookup;
            try {
                var cached = ReadingHistoryPolicy.Meaning(history, sentence);
                var path = File.Exists(Overrides + "/lookup-meaning.md") ? Overrides : Defaults;
                draft.Lookup = cached ?? await content.LookupMeaningAsync(material, settings, File.ReadAllText(path + "/lookup-meaning.md"), lifetime.Token);
                draft.LookupError = null;
            } catch (Exception ex) { draft.LookupError = ex.Message; }
            Publish();
            return draft.Lookup;
        }
        var meaning = Meaning();
        async Task Speak()
        {
            if (!play) return;
            try {
                if (draft.AudioPath == null) {
                    var reuse = await ReadingHistoryPolicy.Recording(history, material, settings.ContextEnabled, meaning);
                    if (reuse != null) {
                        draft.ReusedSpeech = new(reuse.AudioSourceDraftId, reuse.AudioText, reuse.AudioSettings, reuse.AudioPath!);
                        draft.AudioPath = reuse.AudioPath;
                    } else {
                        var text = ReadingSpeech.Text(material,settings.ContextEnabled);
                        var parts = ReadingSpeech.Parts(text);
                        var math = parts.Any(part => part.Math);
                        var voice = ReadingSpeech.VoiceSettings(text,settings);
                        draft.AudioPath = await Port<string>("synthesize", new {
                            text = math ? text : StreamingSpeech.SpokenText(text, voice), math = math ? parts : null, voice = StreamingSpeech.SelectVoice(text, voice),
                            style = StreamingSpeech.SpeechStyle(text, voice), settings.RatePercent, settings.VolumePercent, settings.PitchHz,
                            cacheKey = StreamingSpeech.AudioCacheKey(text, SpeechPurpose.Selection, settings), sentence, id = draft.Id
                        }, lifetime.Token);
                    }
                }
                speechError = "";
                await Port<bool>("play", new { key = draft.AudioPath, play = settings.SpeechAutoPlay || !generate });
            } catch (Exception ex) { speechError = ex.Message; }
            Publish();
        }
        var speaking = Speak();
        try {
            if (generate) {
                draft.HistoryReplayOnly = ReadingHistoryPolicy.ReplayOnly(route.Quick, history);
                if (!draft.HistoryReplayOnly) {
                    var selected = LanguageModes.Select(modes, material, settings.Practice, readMode);
                    languageCards.Clear();
                    languageCards.AddRange(LanguageModeCards.Ensure(draft, selected, drafts));
                    await ReadingCards.Prepare(draft, languageCards.ToArray(), drafts, content, http, lifetime.Token, Publish);
                    if (route.Quick) {
                        await speaking;
                        foreach (var child in languageCards.Where(c => c.PreviewContent?.Fields is { IsUnresolved: false })) await SaveLanguage(child);
                    }
                } else status = "已复用历史记录";
            }
        } finally {
            await Task.WhenAll(meaning, speaking);
            var pronunciation = draft.ReusedSpeech == null ? draft.Lookup?.Pronunciation :
                history.Candidates.FirstOrDefault(c => c.AudioSourceDraftId == draft.ReusedSpeech.SourceDraftId)?.Pronunciation;
            var candidate = new LanguageHistoryCandidate(draft.Id, draft.Id, draft.Lookup, pronunciation,
                material.ContextText, draft.ReusedSpeech == null ? material.ContextText :
                    history.Candidates.FirstOrDefault(c => c.AudioSourceDraftId == draft.ReusedSpeech.SourceDraftId)?.AudioContextText,
                draft.ReusedSpeech?.Text ?? ReadingSpeech.Text(material,settings.ContextEnabled), draft.ReusedSpeech?.Settings ?? settings, draft.AudioPath,
                draft.ReusedSpeech?.SourceDraftId ?? draft.Id, draft.AudioPath != null, languageCards.Any(c => receipts.ContainsKey(c.Id)));
            await Port<bool>("remember", new { key, candidate });
        }
    }

    (string Front, string Back) LanguageFaces(ClipDraft child)
    {
        var mode = modes.Modes.Single(m => m.Definition.Id == child.ModeId).Definition;
        var fields = child.PreviewContent?.Fields ?? throw new InvalidDataException("此模式尚未生成结果。");
        var faces = PublishedLanguage.Render(mode, child.Material, fields);
        return mode.Practice == PracticeChoice.Writing ? (faces.WritingFront, faces.WritingBack) : (faces.ReadingFront, faces.ReadingBack);
    }

    async Task SaveLanguage(ClipDraft child)
    {
        var (front, back) = LanguageFaces(child);
        var frontImages = ChatMarkdown.ReferencedImages(front, collection).ToList();
        if (material.FrontImage is { } primary && frontImages.All(i => i.Sha256 != primary.Sha256)) frontImages.Insert(0, primary);
        await SaveFields(child.Id, "card", child.ModeId!, front, back, frontImages, ChatMarkdown.ReferencedImages(back, collection),
            child.Practice == PracticeChoice.Reading ? draft.AudioPath : null);
    }
}
