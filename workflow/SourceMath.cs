using System.Text;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KnowledgeClipper.Workflow;

internal static class SourceMath
{
    internal sealed record Formula(int Start, int Length, string Tex, bool Block);
    internal static readonly MarkdownPipeline CodeSpans = new MarkdownPipelineBuilder().UsePreciseSourceLocation().DisableHtml().Build();
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseMathematics().UsePreciseSourceLocation().DisableHtml().Build();

    internal static SourceSpan[] Code(string source) => Markdown.Parse(source, CodeSpans).Descendants()
        .Where(n => n is CodeInline or CodeBlock).Select(n => n.Span).ToArray();

    internal static Formula[] Read(string source)
    {
        if (!source.Contains('$') && !source.Contains(@"\(") && !source.Contains(@"\[")) return [];
        var parsed = Normalize(source);
        if (!source.Contains('$')) return parsed.Brackets.ToArray();
        // Explicit brackets also work immediately adjacent to CJK text.
        // Dollar-delimited math uses the card editor's Markdown grammar.
        var found = parsed.Brackets;
        foreach (var node in Markdown.Parse(parsed.Text, Pipeline).Descendants().Where(n => n is MathInline or MathBlock)) {
            var start = parsed.Map[node.Span.Start];
            if (source[start] != '$' || found.Any(f => start >= f.Start && start < f.Start + f.Length)) continue;
            var end = parsed.Map[Math.Min(node.Span.End, parsed.Map.Length - 1)] + 1;
            found.Add(new(start, end - start, node is MathInline inline ? inline.Content.ToString() : ((MathBlock)node).Lines.ToString(),
                node is MathBlock || node is MathInline { DelimiterCount: > 1 }));
        }
        return found.OrderBy(f => f.Start).ToArray();
    }

    internal static (string Text, int[] Map, List<Formula> Brackets) Normalize(string source)
    {
        var protectedSpans = Code(source);
        var brackets = new List<Formula>();
        bool Protected(int index) => protectedSpans.Any(s => index >= s.Start && index <= s.End);
        var result = new StringBuilder(); var map = new List<int>();
        void Append(string value, int index) { result.Append(value); map.AddRange(Enumerable.Repeat(index, value.Length)); }
        for (var i = 0; i < source.Length; i++)
        {
            if (Protected(i) || source[i] != '\\' || i + 1 >= source.Length || source[i + 1] is not ('(' or '[') ||
                i > 0 && source[i - 1] == '\\') { Append(source[i].ToString(), i); continue; }
            var block = source[i + 1] == '['; var close = block ? "\\]" : "\\)";
            var end = source.IndexOf(close, i + 2, StringComparison.Ordinal);
            if (end < 0 || Protected(end)) { Append(source[i].ToString(), i); continue; }
            brackets.Add(new(i, end + 2 - i, source[(i + 2)..end], block));
            Append(block ? "\n$$\n" : "$", i);
            for (var position = i + 2; position < end; position++) Append(source[position].ToString(), position);
            Append(block ? "\n$$\n" : "$", end + 1);
            i = end + 1;
        }
        return (result.ToString(), map.ToArray(), brackets);
    }
}
