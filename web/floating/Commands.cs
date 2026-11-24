using KnowledgeClipper.Workflow;
using System.Text.Json;
using static Browser;

public sealed partial class BrowserSession
{
    public async Task<string> Command(JsonElement e)
    {
        string Text(string key) => e.GetProperty(key).GetString() ?? "";
        var cmd = Text("command");
        if (cmd == "asset") {
            var sha = Text("sha256");
            var asset = collection.Concat(draft.Chat?.Assets ?? []).Concat(imageJob?.Images ?? []).FirstOrDefault(i => i.Sha256 == sha);
            return asset == null ? "" : Convert.ToBase64String(asset.ReadVerified());
        }
        if (cmd == "stop" && draft.Chat != null) { await ChatCommand(e); return View(); }
        if (cmd == "ready") return View();
        if (cmd == "manual" && excerpt != null) {
            excerpt.ManualFront = Text("front"); excerpt.ManualBack = Text("back");
            excerpts.Save(excerpt); await Persist(); return View();
        }
        if (working) return View();
        working = true; error = ""; Publish();
        try {
            switch (cmd) {
                case "start":
                    if (!deferredAuto) break;
                    deferredAuto = false;
                    if (screen == "images") await PrepareImages(settings.OriginalImageMode == true, null);
                    await StartAction(); break;
                case "addImages": AddImages(e.GetProperty("images").Deserialize<BrowserImage[]>(Json)!); imageJob = null; break;
                case "removeCollected": collection.RemoveAt(e.GetProperty("index").GetInt32()); imageJob = null; break;
                case "moveCollected":
                    var i = e.GetProperty("index").GetInt32();
                    var to = Math.Clamp(i + e.GetProperty("offset").GetInt32(), 0, collection.Count - 1);
                    var image = collection[i]; collection.RemoveAt(i); collection.Insert(to,image); imageJob = null; break;
                case "imageRole":
                    var at = e.GetProperty("index").GetInt32();
                    collection[at] = collection[at] with { Role = Text("role") == "answer" ? "answer" : "question" }; imageJob = null; break;
                case "imageRoutes":
                    var kind = e.GetProperty("original").GetBoolean() ? MaterialKind.OriginalImages : MaterialKind.OcrImages;
                    imageJob = images.Create(collection.Select(a => (a.Path,a.Role)),kind,settings,Defaults,Overrides); break;
                case "processImages":
                    await PrepareImages(e.GetProperty("original").GetBoolean(),e.TryGetProperty("route",out var chosen) ? chosen.GetString() : null);
                    await StartAction(); break;
                case "readAgain": await Read(true,false); break;
                case "readCards": readMode = e.TryGetProperty("mode",out var mode) ? mode.GetString() : null;
                    route = route with { Quick = false }; await Read(false,true); break;
                case "saveLanguage": await SaveLanguage(languageCards.Single(child => child.Id == Text("id"))); break;
                case "addExcerpt": case "cloze": case "generateExcerpt":
                    excerpt!.ManualFront = Text("front"); excerpt.ManualBack = Text("back"); excerpts.Save(excerpt);
                    if (cmd == "cloze") ExcerptCards.Cloze(excerpt,excerpts,e.GetProperty("start").GetInt32(),e.GetProperty("length").GetInt32());
                    if (cmd == "addExcerpt") ExcerptCards.Add(excerpt,excerpts,new(excerpt.ManualFront,excerpt.ManualBack),"手动");
                    if (cmd == "generateExcerpt") {
                        var selected = ExcerptModeLoader.Load(Defaults,Overrides).Single(m => m.Definition.Id == Text("mode"));
                        var fields = await content.ProcessExcerptAsync(excerpt.Original,selected,settings,lifetime.Token);
                        ExcerptCards.Add(excerpt,excerpts,fields,selected.Definition.Name,selected);
                    }
                    break;
                case "editExcerpt":
                    if (receipts.ContainsKey(Text("id"))) throw new InvalidDataException("此条已保存。");
                    ExcerptCards.Edit(excerpt!,excerpts,Text("id"),Text("front"),Text("back")); break;
                case "merge": ExcerptCards.Merge(excerpt!,excerpts,e.GetProperty("ids").Deserialize<string[]>(Json)!); break;
                case "undoMerge": ExcerptCards.UndoMerge(excerpt!,excerpts,Text("id")); break;
                case "saveExcerpt": await SaveExcerpt(Text("id")); break;
                default:
                    if (screen != "question") throw new InvalidDataException("请打开问答工作区。");
                    await ChatCommand(e); break;
            }
        } catch (Exception ex) { error = ex.Message; }
        finally {
            working = false;
            await Persist();
        }
        return View();
    }

    async Task StartAction()
    {
        if (screen == "excerpt" && route.Quick) await SaveExcerpt(excerpt!.Cards[0].Id);
        if (screen == "language") await Read(settings.LanguageAutoRead,true);
        if (screen != "question" || !route.SendOnce && !route.Quick) return;
        var chat = draft.Chat!;
        if (route.SendOnce) { await Coordinator(chat).SendAsync(models,ct:lifetime.Token); return; }
        var mode = EntryRouting.ModeId(modes, "question");
        var result = await content.ProcessAsync(material, mode, modes, settings, lifetime.Token, "解释：" + material.SelectedText);
        var basic = result.Basic ?? throw new InvalidDataException(result.Answer?.DisplayText ?? "暂未取得问答结果。");
        var card = chat.Compositions[0]; card.Front = basic.Front; card.Back = basic.Back;
        if (material.FrontImage is { } image) card.Front += "\n\n![图片](asset:" + image.Sha256 + ")";
        await SaveChat(card);
    }
}
