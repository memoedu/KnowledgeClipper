using System.Text.Json;
using KnowledgeClipper.Workflow;
using static Browser;

public sealed partial class BrowserSession(BrowserOptions options)
{
    const string Defaults = "/modes", Overrides = "/user-modes", Data = "/clips";
    readonly LanguageSettings settings = FloatingSettings.Read(options.Values) with {
        ApiBaseUrl = "https://clipper.invalid", Model = options.Model, ImageModel = options.ImageModel ?? options.Model };
    readonly HttpClient http = new(new Transport()) { Timeout = Timeout.InfiniteTimeSpan };
    readonly DraftStore drafts = new(Data);
    readonly ExcerptStore excerpts = new(Data);
    readonly ImageMaterialStore images = new(Data);
    readonly ChatConfigurationLoader chatConfig = new(Defaults + "/chat", Overrides + "/chat");
    readonly Dictionary<string,string> hashes = new();
    readonly Dictionary<string,ChatCoordinator> coordinators = new();
    readonly Dictionary<string,string> receipts = new();
    readonly List<ImageAsset> collection = [];
    readonly List<ClipDraft> languageCards = [];
    readonly CancellationTokenSource lifetime = new();
    ContentModule content = null!;
    ContentConfiguration modes = null!;
    ClipDraft draft = null!;
    ExcerptSession? excerpt;
    ImageMaterialJob? imageJob;
    FloatingRoute route = null!;
    CaptureMaterial material = null!;
    ChatModel[] models = [];
    string screen = "", status = "", speechError = "", error = "";
    object? preview;
    int visible = 30;
    bool working;
    bool deferredAuto;
    string? imageRoute, readMode;
    public string View() => JsonSerializer.Serialize(Projection(), Json);

    public async Task Initialize(BrowserImage[] sourceImages)
    {
        foreach (var (name, contents) in options.Defaults) WriteRule(Defaults, name, contents);
        foreach (var (name, contents) in options.Overrides)
        {
            if (!options.Defaults.ContainsKey(name)) throw new InvalidDataException("规则路径无效。");
            WriteRule(Overrides, name, contents);
        }
        Directory.CreateDirectory(Data);
        content = new(http, () => "host");
        modes = ModeConfigurationLoader.Load(Defaults, Overrides);
        models = new[] { settings.Model, settings.ImageModel }.Distinct().Select(id => new ChatModel(id, id, [], ["text","image"])).ToArray();
        if (options.SavedId != null)
        {
            if (!Guid.TryParseExact(options.SavedId, "N", out _)) throw new InvalidDataException("草稿标识无效。");
            var files = await Port<Dictionary<string,string>>("draftFiles", options.SavedId);
            foreach (var (name, encoded) in files)
            {
                var path = Path.GetFullPath(Path.Combine(Data, name));
                if (!path.StartsWith(Data + "/", StringComparison.Ordinal)) throw new InvalidDataException("草稿路径无效。");
                var bytes = Convert.FromBase64String(encoded);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes); hashes[name] = ImageAsset.Hash(bytes);
            }
            var saved = WorkflowJson.Read<SessionIndex>(Data + "/session.json");
            draft = drafts.Find(saved.Id)!; material = draft.Material; route = saved.Route; screen = saved.Screen;
            excerpt = saved.Excerpt == null ? null : excerpts.Find(saved.Excerpt);
            imageJob = saved.ImageJob == null ? null : images.Find(saved.ImageJob);
            collection.AddRange(saved.Collection);
            foreach (var (key, value) in saved.Receipts) receipts.Add(key, value);
            if (draft.ModeCards.Count != 0) languageCards.AddRange(LanguageModeCards.Children(draft, drafts));
            // A restored browser must never repeat an automatic model call or confirmation.
            return;
        }
        var action = FloatingWorkflow.Entries(options.Source.IsImage).Single(e => e.Id == options.Action).Action;
        route = FloatingWorkflow.Resolve(action, options.Source.IsImage, settings);
        AddImages(sourceImages);
        var source = options.Source;
        var text = source.IsImage ? "图片" : source.Text;
        var capture = source.Prefix != null && source.Suffix != null
            ? SelectionContext.Scope(source.Prefix,text,source.Suffix,settings.ContextGranularity, structuredParagraphs: true) : ((string?)null,(string?)null);
        material = SelectionContext.Build(text,capture.Item1,capture.Item2,settings.ContextEnabled,source.Title,0,DateTimeOffset.UtcNow,true);
        draft = new() { Original = material, Material = material, Settings = settings, Configuration = modes,
            Practice = settings.Practice, Input = new("selection-dot", route.Intent, ReadRequested: settings.LanguageAutoRead && route.Intent == "language") };
        screen = source.IsImage ? "images" : route.Intent;
        if (!source.IsImage) Enter();
        deferredAuto = !route.CollectImages;
        await Persist();
    }

    static void WriteRule(string root, string name, string value)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + "/", StringComparison.Ordinal)) throw new InvalidDataException("规则路径无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    sealed record SessionIndex(string Id, string Screen, FloatingRoute Route, string? Excerpt, string? ImageJob,
        List<ImageAsset> Collection, Dictionary<string,string> Receipts);

    async Task Persist()
    {
        drafts.Save(draft);
        WorkflowJson.Save(Data + "/session.json", new SessionIndex(draft.Id, screen, route, excerpt?.Id, imageJob?.Id, collection, receipts));
        var changed = new Dictionary<string,string>();
        var digests = new Dictionary<string,string>();
        foreach (var path in Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(Data, path);
            if (name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            if (hashes.ContainsKey(name) && name.EndsWith(".png", StringComparison.Ordinal)) continue;
            var bytes = File.ReadAllBytes(path);
            var hash = ImageAsset.Hash(bytes);
            if (hash == hashes.GetValueOrDefault(name)) continue;
            changed[name] = Convert.ToBase64String(bytes);
            digests[name] = hash;
        }
        await Port<bool>("checkpoint", new { id = draft.Id, screen, title = material.SelectedText, files = changed });
        foreach (var (name, hash) in digests) hashes[name] = hash;
    }

    void Enter()
    {
        screen = route.Intent;
        draft.Material = material;
        if (screen == "excerpt") {
            excerpt = ExcerptCards.Create(material, settings, excerpts);
            var original = ExcerptCards.Get(excerpt,excerpts,excerpt.Cards[0].Id);
            if (collection.Count > 0 && !options.Source.IsImage) {
                original.Excerpt = original.Excerpt! with { FrontImages = collection.ToArray() };
                excerpts.Cards.Save(original);
            }
        }
        if (screen != "question") return;
        draft.Chat ??= new() { Model = material.FrontImage == null ? settings.Model : settings.ImageModel,
            Input = material.FrontImage == null || !material.VisualQuestion ? material.SelectedText : "", TypeId = "fast-answer" };
        if (material.FrontImage is { } image) { draft.Chat.Attachments.Add(image); draft.Chat.Assets.Add(image); }
        if (!options.Source.IsImage) {
            draft.Chat.Attachments.AddRange(collection);
            draft.Chat.Assets.AddRange(collection.DistinctBy(a => a.Sha256));
        }
        draft.Chat.Compositions.Add(new() { TypeId = draft.Chat.TypeId });
        draft.Chat.Auxiliary = new(); draft.Chat.Auxiliary.Current.Model = draft.Chat.Model;
    }

    void AddImages(BrowserImage[] inputs)
    {
        if (collection.Count + inputs.Length > 50) throw new InvalidDataException("每次最多收集 50 张图片。");
        foreach (var input in inputs)
        {
            var bytes = Convert.FromBase64String(input.Data);
            var hash = ImageAsset.Hash(bytes);
            if (input.Role is not ("question" or "answer")) throw new InvalidDataException("图片角色无效。");
            var path = Data + "/assets/" + hash + ".png";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
            var asset = new ImageAsset(path, hash, input.Role, collection.Count + 1);
            asset.ReadVerified(); collection.Add(asset);
        }
    }

    async Task PrepareImages(bool original, string? chosen)
    {
        var kind = route.CollectImages ? original ? MaterialKind.OriginalImages : MaterialKind.OcrImages :
            FloatingWorkflow.SingleImageKind(route.Intent, route.Quick, settings);
        imageJob ??= images.Create(collection.Select(i => (i.Path, i.Role)), kind, settings, Defaults, Overrides);
        if (!route.CollectImages) ImageEntryRouting.SetSingle(imageJob, route.Intent, route.Quick);
        if (chosen != null) imageJob.Route = chosen;
        if (imageJob.Route == null) { imageRoute = null; return; }
        imageRoute = imageJob.Route;
        void Report(string value) { status = value; Publish(); }
        var processor = new ImagePreparation(images, new Ocr(), content);
        if (imageJob.Kind == MaterialKind.OriginalImages && (route.CollectImages || route.Intent == "excerpt")) {
            var (fields, mode) = await processor.OriginalAsync(imageJob, route.Intent == "excerpt" ? "image-excerpt" : imageRoute, Report, lifetime.Token);
            excerpt = new() { Original = material, Settings = settings, ImageMaterialId = imageJob.Id };
            ExcerptCards.Add(excerpt, excerpts, fields, mode?.Definition.Name ?? "原图", mode,
                originalExcerpt: !fields.BackImages.Any() && string.IsNullOrWhiteSpace(fields.Back));
            screen = "excerpt"; return;
        }
        var text = ImageEntryRouting.NeedsOcr(imageJob) ? await processor.TextAsync(imageJob, Report, lifetime.Token) : material.SelectedText;
        material = ImageEntryRouting.Material(imageJob, text);
        route = route with { Intent = imageRoute };
        Enter();
    }

    async Task SaveFields(string id, string kind, string type, string front, string back, IEnumerable<ImageAsset>? frontImages = null,
        IEnumerable<ImageAsset>? backImages = null, string? audio = null)
    {
        if (receipts.ContainsKey(id)) return;
        object[] Media(IEnumerable<ImageAsset>? assets, string side) => (assets ?? []).Select(a => (object)new { side,
            hash = a.Sha256, mime = "image/png", data = Convert.ToBase64String(a.ReadVerified()) }).ToArray();
        var attachments = Media(frontImages,"front").Concat(Media(backImages,"back")).ToList();
        if (audio != null) {
            var recording = await Port<JsonElement>("recording", audio);
            if (recording.ValueKind == JsonValueKind.Object) attachments.Add(recording);
        }
        await Port<bool>("save", new { id, kind, type_id = type, front, back, media = attachments });
        receipts[id] = "已保存到个人自由学习";
    }

    async Task SaveExcerpt(string id)
    {
        var item = ExcerptCards.Get(excerpt!, excerpts, id);
        var f = item.Excerpt!;
        if (item.IsOriginalExcerpt || (f.Back.Length == 0 && f.BackImages.Length == 0 && f.Kind == ExcerptCardKind.Basic)) {
            await SaveFields(id, "excerpt", item.ModeId ?? "excerpt-manual", f.Front, "", f.FrontImages);
        } else {
            var cloze = f.Kind == ExcerptCardKind.Cloze;
            await SaveFields(id, "card", cloze ? "excerpt-cloze" : item.ModeId!, cloze ? ExcerptCloze.Question(f.Front) : f.Front,
                cloze ? ExcerptCloze.Answer(f.Front) + (f.Back.Length > 0 ? "\n" + f.Back : "") : f.Back, f.FrontImages, f.BackImages);
        }
        item.ImportState = ImportState.Verified; excerpts.Cards.Save(item);
    }
    void Publish() => _ = Port<bool>("view", View());
}
