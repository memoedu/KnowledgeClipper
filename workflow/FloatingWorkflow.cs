namespace KnowledgeClipper.Workflow;

public enum FloatingAction { Read, Card, Excerpt, Ask, Image }

public sealed record FloatingEntry(FloatingAction Action, string Id, string Label, string EnglishLabel);
public sealed record FloatingRoute(string Intent, bool Quick, bool CollectImages = false, bool SendOnce = false);

/// <summary>The action selected on the text or image dot, before any host captures or stores material.</summary>
public static class FloatingWorkflow
{
    public const int LeaveMilliseconds = 2000;
    public static readonly FloatingEntry Read = new(FloatingAction.Read, "read", "读", "Read");
    public static readonly FloatingEntry Card = new(FloatingAction.Card, "card", "卡", "Card");
    public static readonly FloatingEntry Excerpt = new(FloatingAction.Excerpt, "excerpt", "摘", "Excerpt");
    public static readonly FloatingEntry Ask = new(FloatingAction.Ask, "ask", "问", "Ask");
    public static readonly FloatingEntry Image = new(FloatingAction.Image, "image", "图", "Images");

    public static IReadOnlyList<FloatingEntry> Entries(bool image) =>
        image ? [Image, Read, Card, Excerpt, Ask] : [Read, Card, Excerpt, Ask];

    public static FloatingRoute Resolve(FloatingAction action, bool image, LanguageSettings settings) => action switch
    {
        FloatingAction.Read => new("language", settings.QuickLanguage),
        FloatingAction.Card => new("question", settings.QuickQuestion),
        FloatingAction.Excerpt => new("excerpt", settings.QuickExcerpt),
        FloatingAction.Ask => new("question", false, SendOnce: true),
        FloatingAction.Image when image => new("image", false, CollectImages: true),
        _ => throw new InvalidDataException("所选材料不支持此操作。")
    };

    public static MaterialKind SingleImageKind(string intent, bool quick, LanguageSettings settings) =>
        intent == "excerpt" && quick || settings.OriginalImageMode == true ? MaterialKind.OriginalImages : MaterialKind.OcrImages;
}
