using System.Text.Json;

namespace KnowledgeClipper.Workflow;

public static class ChatEditorCommands
{
    public static bool Apply(ChatSession root, JsonElement command, out bool clearPreview)
    {
        clearPreview = false;
        string Text(string name) => command.GetProperty(name).GetString() ?? "";
        CardComposition Card() => root.Compositions.Single(c => c.Id == Text("cardId"));
        var chat = ChatWorkspace.Active(root);
        switch (Text("command"))
        {
            case "addCard":
                root.Compositions.Add(new() { TypeId = chat.TypeId });
                break;
            case "copyCard":
                ChatComposition.Editable(root, Card());
                break;
            case "select":
                ChatComposition.Select(root, Card(), Text("messageId"), Text("side"), command.GetProperty("selected").GetBoolean());
                break;
            case "editCard":
                var card = Card();
                string? Edited(string key) => command.TryGetProperty(key, out var value)
                    ? ChatMarkdown.NormalizeMediaReferences(value.GetString() ?? "", root.Assets) : null;
                bool? Enabled(string key) => command.TryGetProperty(key, out var value) ? value.GetBoolean() : null;
                ChatComposition.Edit(card, Edited("front") ?? card.Front, Edited("back") ?? card.Back,
                    command.TryGetProperty("typeId", out var type) ? type.GetString() ?? card.TypeId : card.TypeId,
                    Edited("annotation"), Enabled("annotationEnabled"), Edited("frontAnnotation"), Enabled("frontAnnotationEnabled"));
                clearPreview = true;
                break;
            case "move":
                ChatComposition.Move(root, Card(), Text("side"), command.GetProperty("index").GetInt32(), command.GetProperty("offset").GetInt32());
                break;
            case "rebuild":
                ChatComposition.Rebuild(root, Card());
                break;
            case "undo":
                ChatComposition.Undo(Card(), root);
                clearPreview = true;
                break;
            case "redo":
                ChatComposition.Redo(Card(), root);
                clearPreview = true;
                break;
            case "roundCard":
                var message = chat.Messages.Single(m => m.Id == Text("messageId"));
                var question = message.Role == "user" ? message : chat.Messages.Take(chat.Messages.IndexOf(message))
                    .LastOrDefault(m => m.Role == "user" && m.Action == null);
                if (question == null) throw new InvalidDataException("没有对应问题。");
                var answers = chat.Messages.Skip(chat.Messages.IndexOf(question) + 1).TakeWhile(m => m.Role != "user")
                    .Where(m => m.Role == "assistant" && m.Action == null);
                var round = new CardComposition { TypeId = question.TypeId, FrontSources = [question.Id], BackSources = answers.Select(m => m.Id).ToList() };
                ChatComposition.Rebuild(root, round);
                root.Compositions.Add(round);
                break;
            default:
                return false;
        }
        return true;
    }
}
