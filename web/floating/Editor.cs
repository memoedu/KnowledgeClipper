using KnowledgeClipper.Workflow;
using System.Text.Json;
using static Browser;

public sealed partial class BrowserSession
{
    ChatCoordinator Coordinator(ChatSession session)
    {
        var root = draft.Chat!;
        var id = session == root.Auxiliary!.Current ? "aux" : root.ActiveConversationId;
        if (!coordinators.TryGetValue(id, out var value) || value.Session != session) {
            value = new(draft, drafts, chatConfig, null!, http, () => "host", Data, session);
            value.Changed += Publish; coordinators[id] = value;
        }
        return value;
    }

    object Projection()
    {
        var shared = new { screen, status, error, speechError, working, route, id = draft.Id, readMode, text = material.SelectedText,
            receipt = receipts.Count != 0, audio = draft.AudioPath, lookup = draft.Lookup, lookupError = draft.LookupError,
            replayOnly = draft.HistoryReplayOnly, showDictionary = settings.ShowDictionaryInPreview, context = material.ContextText,
            dictionary = draft.DictionaryDetails?.DisplayText, start = material.TargetStart, scope = settings.ContextGranularity };
        if (screen == "images") return new { shared, original = imageJob?.Kind == MaterialKind.OriginalImages ||
            imageJob == null && settings.OriginalImageMode == true, imageRoute = imageJob?.Route ?? imageRoute, collection = collection.Select(i => new {i.Sha256,i.Role}),
            routes = MaterialRouting.Choices(imageJob?.Kind ?? (settings.OriginalImageMode == true ? MaterialKind.OriginalImages : MaterialKind.OcrImages),
                imageJob?.Modes ?? ImageModeLoader.Load(Defaults, Overrides)) };
        if (screen == "excerpt" && excerpt != null) return new { shared, excerpt.ManualFront, excerpt.ManualBack,
            modes = ExcerptModeLoader.Load(Defaults, Overrides).Where(m => m.LoadError == null).Select(m => new { m.Definition.Id, m.Definition.Name }),
            cards = excerpt.Cards.Select(link => {
                var card = ExcerptCards.Get(excerpt, excerpts, link.Id);
                return new { link.Id, link.Label, link.SupersededBy, link.Replaces, card.Excerpt!.Front, card.Excerpt.Back,
                    card.Excerpt.Kind, frontImages = card.Excerpt.FrontImages.Select(i => i.Sha256), backImages = card.Excerpt.BackImages.Select(i => i.Sha256),
                    saved = receipts.ContainsKey(card.Id) };
            }) };
        if (screen == "language") return new { shared, modes = modes.Modes.Where(m => LanguageModes.IsConcrete(m.Definition))
            .Select(m => new {m.Definition.Id,m.Definition.Name}), cards = languageCards.Select(child => {
                string front = "", back = "";
                if (child.PreviewContent?.Fields is { IsUnresolved: false }) (front,back) = LanguageFaces(child);
                return new {child.Id, child.ModeId, front, back, reason = child.ContentError != null ? new[]{child.ContentError} : child.PreviewContent?.Fields?.UnresolvedReasons,
                    saved = receipts.ContainsKey(child.Id)};
            }) };
        var root = draft.Chat!;
        var aux = root.Auxiliary!.Current;
        var auxiliary = new { provider = "api", aux.Model, aux.Effort, models, busy = coordinators.GetValueOrDefault("aux")?.Busy ?? false };
        bool Busy(ChatSession session) => coordinators.Values.Any(c => c.Session == session && c.Busy);
        var view = ChatView.Build(draft.Id, root, chatConfig.Load().Types, models, visible,
            (_, text) => text == null ? null : ChatMarkdown.ForDisplay(text, root.Assets),
            (_, text) => text == null ? [] : ChatMarkdown.CopyMath(text), Busy, working, Busy(ChatWorkspace.Active(root)), Busy(aux),
            string.IsNullOrEmpty(error) ? null : error, receipts, preview, auxiliary, true);
        return new { shared, chat = view };
    }

    async Task ChatCommand(JsonElement e)
    {
        var root = draft.Chat!;
        var chat = ChatWorkspace.Active(root);
        var cmd = e.GetProperty("command").GetString()!;
        string Text(string key) => e.GetProperty(key).GetString() ?? "";
        CardComposition Card() => root.Compositions.Single(c => c.Id == Text("cardId"));
        if (e.TryGetProperty("conversationId", out var tab) && tab.GetString() is {Length: >0} from && from != root.ActiveConversationId) return;
        if (ChatEditorCommands.Apply(root, e, out var clear)) { if (clear) preview = null; return; }
        switch (cmd) {
            case "ready": case "models": case "auxiliaryModels": case "reloadView": break;
            case "older": visible += 30; break;
            case "input": chat.Input = Text("text"); break;
            case "new": ChatWorkspace.New(root); break;
            case "fork": ChatWorkspace.Fork(root,Text("messageId")); break;
            case "closeConversation": ChatWorkspace.Close(root,Text("id")); break;
            case "undoCloseConversation": ChatWorkspace.UndoClose(root); break;
            case "switchConversation": ChatWorkspace.Switch(root,Text("id")); break;
            case "moveConversation": ChatWorkspace.Move(root,Text("id"),Text("targetId"),e.GetProperty("after").GetBoolean()); break;
            case "settings": case "auxiliarySettings":
                var target = cmd == "settings" ? chat : root.Auxiliary!.Current;
                if (e.TryGetProperty("model",out var model) && models.Any(m => m.Id == model.GetString())) target.Model = model.GetString()!;
                if (e.TryGetProperty("typeId",out var type)) { chatConfig.Target(type.GetString()!); target.TypeId = type.GetString()!; }
                if (e.TryGetProperty("includeExamples",out var examples)) target.IncludeExamples = examples.GetBoolean();
                break;
            case "send": if (e.TryGetProperty("input",out var input)) chat.Input = input.GetString() ?? "";
                await Coordinator(chat).SendAsync(models, ct: lifetime.Token); break;
            case "stop": foreach (var c in coordinators.Values.Where(c => c.Busy)) await c.StopAsync(); break;
            case "recover": await Coordinator(chat).RecoverAsync(lifetime.Token); break;
            case "removeImage": chat.Attachments.RemoveAll(a => a.Sha256 == Text("sha256")); break;
            case "pasteImageData":
                var previous = collection.Count;
                var payload = e.GetProperty("images").EnumerateArray().Select(i => i.GetString()!).Select(encoded =>
                    new BrowserImage(encoded[(encoded.IndexOf(',') + 1)..],"question")).ToArray();
                AddImages(payload);
                var added = collection.Skip(previous).ToArray(); collection.RemoveRange(previous, added.Length);
                foreach (var asset in added) if (!root.Assets.Any(a => a.Sha256 == asset.Sha256)) root.Assets.Add(asset);
                if (!e.TryGetProperty("cardId",out _)) { chat.Attachments.AddRange(added); break; }
                var edit = Card();
                if (edit.ExportDraftId != null) throw new InvalidDataException("已确认的卡片不可修改。");
                var side = Text("side");
                var old = side switch { "front" => edit.Front, "back" => edit.Back, "frontAnnotation" => edit.FrontAnnotation,
                    "annotation" => edit.Annotation, _ => throw new InvalidDataException("图片位置无效。") };
                var selection = e.TryGetProperty("start",out var at) ? Math.Clamp(at.GetInt32(),0,old.Length) : old.Length;
                var end = e.TryGetProperty("end",out var to) ? Math.Clamp(to.GetInt32(),selection,old.Length) : selection;
                var updated = old[..selection] + string.Join("\n",added.Select(a => "![图片](asset:" + a.Sha256 + ")")) + old[end..];
                ChatComposition.Edit(edit, side == "front" ? updated : edit.Front, side == "back" ? updated : edit.Back, edit.TypeId,
                    side == "annotation" ? updated : null, side == "annotation" ? true : null,
                    side == "frontAnnotation" ? updated : null, side == "frontAnnotation" ? true : null);
                preview = null; break;
            case "copyMessage": await Port<bool>("copy", ChatComposition.Material(ChatWorkspace.Messages(root).Single(m => m.Id == Text("messageId")))); break;
            case "preview":
                var card = Card();
                ChatMarkdown.Render(card.Front,card.Back,root.Assets,card.Annotation,card.FrontAnnotation);
                preview = new { cardId = card.Id, card.Revision,
                    front = ChatMarkdown.ForDisplay(ChatMarkdown.BackWithAnnotation(card.Front,card.FrontAnnotation),root.Assets),
                    back = ChatMarkdown.ForDisplay(ChatMarkdown.BackWithAnnotation(card.Back,card.Annotation),root.Assets) }; break;
            case "compose":
                var types = chatConfig.Load().Types.Select(t => t.Id).ToArray();
                var proposal = ChatComposition.Begin(root,Card(),Text("action"),Text("requirements"),types,
                    e.TryGetProperty("side",out var forSide) ? forSide.GetString() : null,chat);
                var aux = root.Auxiliary!.Current; aux.TypeId = Card().TypeId; aux.IncludeExamples = chat.IncludeExamples;
                await Coordinator(aux).SendAsync(models,proposal.Action,proposal.InputSnapshot,ChatComposition.ProposalImages(proposal,root),lifetime.Token);
                ChatComposition.Parse(proposal,aux.Messages.Last(m => m.Role == "assistant").Text,root,types); break;
            case "editProposal":
                var edited = root.Proposals.Single(p => p.Id == Text("proposalId") && p.Status == "preview");
                var candidate = edited.Cards[e.GetProperty("index").GetInt32()];
                if (edited.Action != "transcribe" || edited.TargetSide == "front") candidate.Front = Text("front");
                if (edited.Action != "formalize" && (edited.Action != "transcribe" || edited.TargetSide == "back")) candidate.Back = Text("back");
                break;
            case "adopt": ChatComposition.Adopt(root,root.Proposals.Single(p => p.Id == Text("proposalId"))); break;
            case "discard": root.Proposals.Single(p => p.Id == Text("proposalId")).Status = "discarded"; break;
            case "export":
                if (Text("destination") != "study") throw new InvalidDataException("站内卡片存入个人自由学习。");
                await SaveChat(Card()); break;
            case "openRules": await Port<bool>("settings", Text("kind")); break;
            case "snapshot":
                var snapshot = chat.Prompts.LastOrDefault() ?? chatConfig.Freeze(chat,chat.Input,[]);
                await Port<bool>("notice",snapshot.Instructions); break;
            case "uiError": case "copySelectionError": throw new InvalidDataException(Text("message"));
            default: throw new InvalidDataException("未知编辑操作：" + cmd);
        }
    }

    async Task SaveChat(CardComposition card)
    {
        var assets = draft.Chat!.Assets;
        var front = ChatMarkdown.BackWithAnnotation(card.Front,card.FrontAnnotation);
        var back = ChatMarkdown.BackWithAnnotation(card.Back,card.Annotation);
        ChatMarkdown.Render(card.Front,card.Back,assets,card.Annotation,card.FrontAnnotation);
        await SaveFields(card.Id, "card", card.TypeId, front, back, ChatMarkdown.ReferencedImages(front,assets), ChatMarkdown.ReferencedImages(back,assets));
        card.ExportDraftId = card.Id;
    }
}
