namespace KnowledgeClipper.Workflow;

/// <summary>The presentation contract consumed by the same editor in WebView2 and web browsers.</summary>
public static class ChatView
{
    public static object Build(string draftId, ChatSession root, ChatQuestionType[] types, IReadOnlyList<ChatModel> models,
        int visibleCount, Func<string, string?, string?> display, Func<string, string?, ChatCopyMath[]> copyMath,
        Func<ChatSession, bool> tabBusy, bool busy, bool activeBusy, bool auxiliaryBusy,
        string? error, IReadOnlyDictionary<string, string> exportStatus, object? cardPreview, object auxiliary, bool study = false)
    {
        var chat = ChatWorkspace.Active(root);
        var sourceIds = root.Compositions.SelectMany(c => c.FrontSources.Concat(c.BackSources)).ToHashSet();
        object Tab(string id) {
            var session = ChatWorkspace.Session(root, id);
            return new { id, title = id == "main" ? "原对话" : root.Branches.Single(b => b.Id == id).Title,
                busy = tabBusy(session), forking = session.Fork is { Stage: not "verified" },
                needsReconciliation = session.Binding.NeedsReconciliation || session.Fork is { Stage: not "verified" } };
        }
        return new {
            draftId, study, conversationId = root.ActiveConversationId, tabs = ChatWorkspace.OpenIds(root).Select(Tab),
            canUndoClose = root.ClosedTabs.Count > 0, busy, conversationBusy = activeBusy && chat.Fork is not { Stage: not "verified" },
            error = error ?? chat.Binding.Error,
            status = auxiliaryBusy ? "正在处理卡片…" : activeBusy && chat.Fork is { Stage: not "verified" } ? "正在建立或核对分叉…" :
                activeBusy ? "正在回复…" : busy ? "其他对话正在处理…" : chat.Binding.NeedsReconciliation ? "结果待核对" : "草稿已保存",
            provider = chat.Binding.Provider, chat.Model, chat.Effort, chat.TypeId, chat.Input, chat.IncludeExamples, chat.UseQuestionTypeDefaults,
            messages = chat.Messages.TakeLast(visibleCount).Select(m => new { m.Id, m.Role, m.Status, m.TypeId, m.Action, m.CreatedAt,
                canFork = ChatWorkspace.CanFork(chat, m), parts = m.Parts.Select((p, i) => new { p.Kind, p.Text,
                    displayText = display(m.Id + ":" + i, p.Text), copyMath = copyMath(m.Id + ":" + i, p.Text),
                    image = p.Image == null ? null : new { p.Image.Sha256 } }) }),
            total = chat.Messages.Count, hasSent = chat.Prompts.Count > 0, models, types, attachments = chat.Attachments.Select(a => new { a.Sha256 }),
            sourceMessages = ChatWorkspace.Messages(root).Where(m => sourceIds.Contains(m.Id)).Select(m => new { m.Id,
                title = m.Text.Length > 32 ? m.Text[..32] + "…" : m.Text,
                conversation = root.Messages.Any(x => x.Id == m.Id) ? "原对话" : root.Branches.First(b => b.Session.Messages.Any(x => x.Id == m.Id)).Title }),
            cards = root.Compositions.Select(c => new { c.Id, c.TypeId, c.FrontSources, c.BackSources, c.Front, c.Back,
                c.Annotation, c.AnnotationEnabled, c.FrontAnnotation, c.FrontAnnotationEnabled, c.Revision, c.ManuallyEdited, c.SourcesChanged,
                c.ExportDraftId, canUndo = c.ExportDraftId == null && c.UndoHistory.Count > 0, canRedo = c.ExportDraftId == null && c.RedoHistory.Count > 0 }),
            proposals = root.Proposals, exportStatus, cardPreview, auxiliary,
            needsReconciliation = chat.Binding.NeedsReconciliation || chat.Fork is { Stage: not "verified" }
        };
    }
}
