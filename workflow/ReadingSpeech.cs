using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public static class ReadingSpeech
{
    public sealed record Part(string Text, bool Math);

    public static bool HasFormula(string text) => SourceMath.Read(text).Length > 0;

    public static Part[] Parts(string text)
    {
        var parts = new List<Part>();
        var from = 0;
        foreach (var formula in SourceMath.Read(text)) {
            if (formula.Start < from) continue;
            if (from < formula.Start) parts.Add(new(text[from..formula.Start], false));
            parts.Add(new(formula.Tex, true));
            from = formula.Start + formula.Length;
        }
        if (from < text.Length) parts.Add(new(text[from..], false));
        return parts.ToArray();
    }
    public static string Text(CaptureMaterial material, bool contextEnabled) =>
        contextEnabled && material.ContextText is { } context && HasFormula(context) ? context : material.SelectedText;

    public static LanguageSettings VoiceSettings(string text, LanguageSettings settings)
    {
        if (!settings.AutoVoice) return settings;
        var parts = Parts(text);
        if (!parts.Any(p => p.Math)) return settings;
        var prose = string.Concat(parts.Where(p => !p.Math).Select(p => p.Text));
        return Regex.IsMatch(prose, @"\p{IsCJKUnifiedIdeographs}") ? settings with { AutoVoice = false } : settings;
    }

    public static async Task<string> ReadFormulaTextAsync(string raw, LanguageSettings settings, CancellationToken ct)
    {
        var info = new ProcessStartInfo(settings.NodePath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8 };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"helpers","read-math.mjs"));
        using var process = Process.Start(info) ?? throw new IOException("无法启动公式朗读。");
        using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) {} });
        var output = process.StandardOutput.ReadToEndAsync(ct); var errors = process.StandardError.ReadToEndAsync(ct);
        var input = System.Text.Json.JsonSerializer.Serialize(Parts(raw), WorkflowJson.Options);
        await process.StandardInput.WriteAsync(input.AsMemory(),ct); process.StandardInput.Close();
        await process.WaitForExitAsync(ct);
        var result = await output; await errors;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("无法生成这段公式的读法，原文已保留。");
        return result;
    }
}
