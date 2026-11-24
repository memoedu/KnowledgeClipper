using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnowledgeClipper.Workflow;

public sealed record SettingChoice(string Value, string Label, string English);
public sealed record FloatingSetting(string Key, string Group, string Label, string English, string Kind,
    SettingChoice[]? Choices = null, int? Minimum = null, int? Maximum = null);

public static class FloatingSettings
{
    public static readonly FloatingSetting[] Fields = [
        new("practice", "capture", "语言卡练习方式", "Language card practice", "choice",
            [new("Both", "理解与朗读、书写", "Reading and writing"), new("Reading", "理解与朗读", "Reading"), new("Writing", "书写练习", "Writing")]),
        new("contextEnabled", "capture", "带入来源上下文", "Include source context", "boolean"),
        new("contextGranularity", "capture", "上下文范围", "Context length", "choice",
            [new("sentence", "一句", "Sentence"), new("paragraph", "一段", "Paragraph")]),
        new("showDictionaryInPreview", "capture", "制卡时显示词典详情", "Show dictionary details in the card editor", "boolean"),
        new("questionCardIncludeContext", "capture", "问答卡正面附带前序问答", "Include earlier turns on the card front", "boolean"),
        new("clipboardImageDotEnabled", "floating", "选中图片时显示浮点", "Show the image menu", "boolean"),
        new("quickLanguage", "floating", "读 · 快捷生成语言卡", "Read · make language cards immediately", "boolean"),
        new("quickQuestion", "floating", "卡 · 快捷保存一轮问答", "Card · save one question and answer immediately", "boolean"),
        new("quickExcerpt", "floating", "摘 · 直接收下原文或原图", "Excerpt · save the original immediately", "boolean"),
        new("originalImageMode", "images", "保留原图制卡", "Make cards from original images", "boolean"),
        new("ocrDefaultRoute", "images", "多图识别后的默认操作", "Default action after multi-image OCR", "choice",
            [new("ask", "每次选择", "Ask each time"), new("excerpt", "摘录", "Excerpt"), new("language", "语言学习", "Language practice"), new("question", "问答", "Questions")]),
        new("originalDefaultRoute", "images", "多张原图的默认操作", "Default action for original images", "choice",
            [new("ask", "每次选择", "Ask each time"), new("image-answer", "多图模式", "Multi-image cards"), new("image-excerpt", "原图摘录", "Original image excerpt")]),
        new("mineruLanguage", "images", "OCR 语言", "OCR language", "text"),
        new("mineruTable", "images", "识别表格", "Recognize tables", "boolean"),
        new("mineruFormula", "images", "识别公式", "Recognize formulas", "boolean"),
        new("mineruTimeoutSeconds", "images", "OCR 超时（秒）", "OCR timeout (seconds)", "number", Minimum: 30, Maximum: 3600),
        new("languageAutoRead", "speech", "点「读」后自动朗读", "Read aloud when opening Read", "boolean"),
        new("speechAutoPlay", "speech", "合成完成后自动播放", "Play after synthesis", "boolean"),
        new("autoVoice", "speech", "自动匹配中英文音色", "Choose voice by language", "boolean"),
        new("voice", "speech", "中文或指定音色", "Chinese or selected voice", "text"),
        new("englishVoice", "speech", "英文音色", "English voice", "text"),
        new("ratePercent", "speech", "速度变化（%）", "Speed adjustment (%)", "number", Minimum: -50, Maximum: 100),
        new("volumePercent", "speech", "音量变化（%）", "Volume adjustment (%)", "number", Minimum: -100, Maximum: 100),
        new("pitchHz", "speech", "音调变化（Hz）", "Pitch adjustment (Hz)", "number", Minimum: -50, Maximum: 50),
        new("originalExcerptHotkey", "shortcuts", "直接收下原文", "Save original excerpt", "text"),
        new("replayLastHotkey", "shortcuts", "重读上一条", "Replay last recording", "text"),
        new("contentTimeoutSeconds", "model", "请求超时（秒）", "Request timeout (seconds)", "number", Minimum: 1, Maximum: 600),
        new("contentMaxRetries", "model", "失败重试次数", "Maximum retries", "number", Minimum: 0, Maximum: 5)
    ];

    public static object Catalog()
    {
        var defaults = JsonSerializer.SerializeToElement(new LanguageSettings { ContextEnabled = true }, WorkflowJson.Options);
        return new {
            text = FloatingWorkflow.Entries(false), image = FloatingWorkflow.Entries(true),
            leaveMilliseconds = FloatingWorkflow.LeaveMilliseconds, fields = Fields,
            defaults = Fields.ToDictionary(field => field.Key, field => defaults.GetProperty(field.Key))
        };
    }

    public static LanguageSettings Read(JsonElement values)
    {
        var result = new JsonObject();
        foreach (var field in Fields)
        {
            if (!values.TryGetProperty(field.Key, out var value)) continue;
            var valid = field.Kind switch {
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False || field.Key == "originalImageMode" && value.ValueKind == JsonValueKind.Null,
                "number" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= field.Minimum && number <= field.Maximum,
                "choice" => value.ValueKind == JsonValueKind.String && field.Choices!.Any(choice => choice.Value == value.GetString()),
                "text" => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 160 } text &&
                    (text.Length > 0 || field.Group == "shortcuts") && !text.Any(char.IsControl),
                _ => false
            };
            if (!valid) throw new InvalidDataException("设置无效：" + field.Label);
            result[field.Key] = JsonNode.Parse(value.GetRawText());
        }
        return result.Deserialize<LanguageSettings>(WorkflowJson.Options)!;
    }
}
