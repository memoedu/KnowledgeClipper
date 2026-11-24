namespace KnowledgeClipper.Workflow;

// Input segments must be adjacent, verified source text. Never infer missing text.
public static class SelectionContext
{
    public sealed record Result(string Text, string? Prefix, string? Suffix);

    public static Result Complete(string selected, string? prefix, string? suffix)
    {
        if (string.IsNullOrEmpty(selected) || prefix == null || suffix == null)
            return new Result(selected, prefix, suffix);
        var source = prefix + selected + suffix;
        var start = prefix.Length;
        var end = start + selected.Length;
        var left = start;
        var right = end;
        if (IsLetter(selected[0])) while (left > 0 && IsWordCharacter(source, left - 1)) left--;
        if (IsLetter(selected[^1])) while (right < source.Length && IsWordCharacter(source, right)) right++;
        // UIA caps each adjacent segment at 4000 characters. Reaching that cap
        // without a visible word boundary cannot prove that a word is complete.
        if (left == 0 && prefix.Length >= 4000) left = start;
        if (right == source.Length && suffix.Length >= 4000) right = end;
        return new Result(source[left..right], source[..left], source[right..]);
    }

    private static bool IsLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsWordCharacter(string source, int index) => IsLetter(source[index]) ||
        (source[index] is '\'' or '’' or '-') && index > 0 && index + 1 < source.Length &&
        IsLetter(source[index - 1]) && IsLetter(source[index + 1]);

    public static (string Prefix, string Suffix) Scope(string prefix, string selected, string suffix, string granularity,
        bool structuredParagraphs = false)
    {
        prefix = prefix.Length > 4000 ? prefix[^4000..] : prefix;
        suffix = suffix.Length > 4000 ? suffix[..4000] : suffix;
        var text = prefix + selected + suffix;
        int left = 0, end = prefix.Length + selected.Length, right = text.Length;
        var protectedAt = new int[text.Length];
        foreach (var formula in SourceMath.Read(text)) protectedAt[formula.Start] = formula.Start + formula.Length;
        if (text.IndexOfAny(['`', '~', '\n', '\r']) >= 0)
            foreach (var span in SourceMath.Code(text)) protectedAt[span.Start] = Math.Max(protectedAt[span.Start], span.End + 1);
        for (int i = 0; i < text.Length; i++) {
            if (protectedAt[i] > i) { i = protectedAt[i] - 1; continue; }
            bool line = text[i] is '\r' or '\n';
            if (line && structuredParagraphs) {
                var next = i + (text.AsSpan(i).StartsWith("\r\n") ? 2 : 1);
                if (next >= text.Length || text[next] is not ('\r' or '\n')) { i = next - 1; continue; }
            }
            if (!line && (granularity == "paragraph" || text[i] is not ('。' or '！' or '？' or '.' or '!' or '?'))) continue;
            if (text[i] == '.' && i>0 && i+1<text.Length && char.IsLetterOrDigit(text[i-1]) && char.IsLetterOrDigit(text[i+1])) continue;
            var stop = i + 1;
            if (line) {
                while (stop < text.Length && text[stop] is '\r' or '\n') stop++;
            } else {
                while (stop < text.Length && "。！？.!?…’”」』）".Contains(text[stop])) stop++;
            }
            if (stop <= prefix.Length) left = stop;
            else if (stop >= end) { right = Math.Max(end, line ? i : stop); break; }
            i = stop - 1;
        }
        return (text[left..prefix.Length].TrimStart(),text[end..right].TrimEnd());
    }
    public static CaptureMaterial Build(string text, string? prefix, string? suffix, bool enabled, string source,
        long window, DateTimeOffset capturedAt, bool lookup)
    {
        var span = Complete(text,prefix,suffix);
        var context = span.Prefix == null || span.Suffix == null ? null : span.Prefix + span.Text + span.Suffix;
        var material = new CaptureMaterial(span.Text,enabled ? context : null,
            enabled ? context == null ? "unavailable" : "source" : "generated_allowed",
            enabled && context != null ? span.Prefix!.Length : null,source,window,capturedAt)
            { LookupContextText = lookup ? context : null };
        material.Validate();
        return material;
    }
}
