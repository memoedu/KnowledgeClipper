namespace KnowledgeClipper.Workflow;

public static class ReadingCards
{
    public static async Task Prepare(ClipDraft draft, ClipDraft[] children, DraftStore store, ContentModule content,
        HttpClient http, CancellationToken cancellation, Action changed, Action<ClipDraft>? refresh = null)
    {
        var dictionaryTasks = children.Where(child => child.ConfirmedNote == null &&
            (child.DictionaryDetails == null || child.DictionaryDetails.Target != child.Material.SelectedText.Trim() ||
             child.DictionaryDetails.Language != child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language))
            .Select(child => child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language!)
            .Distinct(StringComparer.Ordinal).ToDictionary(language => language,
                language => new DirectDictionaryLookup(http).LookupAsync(draft.Material.SelectedText, language, cancellation));
        var exampleTasks = children.Where(child => child.ConfirmedNote == null && child.NoteId == null &&
            child.Material.ContextText == null).Select(child =>
            child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language!)
            .Distinct(StringComparer.Ordinal).ToDictionary(language => language, async language =>
            {
                var details = dictionaryTasks.TryGetValue(language, out var pending) ? await pending :
                    children.First(child => child.DictionaryDetails?.Language == language).DictionaryDetails;
                return await new AuthenticExampleLookup(http).FindWithFallbackAsync(draft.Material.SelectedText,
                    language, details, cancellation);
            });
        changed();
        await Task.WhenAll(children.Select(async child =>
        {
            if (child.Content != null && child.Content.Fields?.IsUnresolved != true || child.ConfirmedNote != null || child.NoteId != null) return;
            try
            {
                refresh?.Invoke(child);
                child.ContentError = null;
                var dictionaryLanguage = child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language!;
                dictionaryTasks.TryGetValue(dictionaryLanguage, out var dictionaryTask);
                if (child.Material.ContextText == null)
                {
                    var example = await exampleTasks[dictionaryLanguage];
                    if (dictionaryTask != null) child.DictionaryDetails = await dictionaryTask;
                    if (example == null) throw new InvalidDataException("开放语料没有找到包含原样词块的真实例句；请补充上下文后重试。");
                    child.Material = AuthenticExampleLookup.Apply(child.Material, example.Value);
                    child.DictionaryDetails = (child.DictionaryDetails ?? throw new InvalidDataException("缺少例句来源记录。")) with
                    { ExampleSentence = example.Value.Sentence, ExampleSource = example.Value.Source };
                    store.Save(child);
                }
                child.Content = await content.ProcessAsync(child.Material, child.ModeId!, child.Configuration, child.Settings, cancellation);
                if (dictionaryTask != null) child.DictionaryDetails = await dictionaryTask;
                child.EditedFields = null;
                child.Times["previewReady"] = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) { child.ContentError = "已停止本模式处理，原材料已保留。"; }
            catch (Exception ex) { child.ContentError = ex.Message; }
            finally { store.Save(child); changed(); }
        }));
        foreach (var child in children.Where(child => child.ConfirmedNote == null &&
            (child.DictionaryDetails == null || child.DictionaryDetails.Target != child.Material.SelectedText.Trim() ||
             child.DictionaryDetails.Language != child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language)))
        {
            var dictionaryLanguage = child.Configuration.Modes.Single(mode => mode.Definition.Id == child.ModeId).Definition.Language!;
            if (dictionaryTasks.TryGetValue(dictionaryLanguage, out var dictionaryTask))
                child.DictionaryDetails = await dictionaryTask;
            store.Save(child);
        }
        if (children.Length > 0) draft.DictionaryDetails = children[0].DictionaryDetails;
    }
}
