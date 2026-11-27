using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class PronunciationChecks
{
    // Explicit live canary with public inputs. Saves audio without using an audio
    // device, changing application settings, or touching existing user recordings.
    public static async Task RunAsync(string output)
    {
        var directory = Path.GetFullPath(output);
        Directory.CreateDirectory(directory);
        var settings = new LanguageSettings { Voice = "Mia", EnglishVoice = "marin", SpeechAutoPlay = false };
        var helper = Path.Combine(Environment.CurrentDirectory, "helpers", "stream_speech.py");
        using var speech = new StreamingSpeech(helper, directory);
        var results = new List<object>();
        foreach (var (name, text, model) in new[] {
            ("english-word", "resilient", "gpt-realtime-2.1-mini"),
            ("english-sentence", "A patient gardener noticed a resilient plant beside the old stone wall.", "gpt-realtime-2.1-mini"),
            ("chinese-word", "琢磨", "mimo-v2.5-tts"),
            ("chinese-sentence", "清晨的雨刚停，石板路上泛着微光。她撑开伞，沿着河边慢慢走回家。", "mimo-v2.5-tts") })
        {
            var draft = new ClipDraft { Id = name + "-" + Guid.NewGuid().ToString("N"), Original = new(text), Settings = settings };
            await speech.SpeakAsync(draft, false, () => { });
            if (draft.SpeechError != null || !draft.Times.ContainsKey("audioComplete") || draft.AudioPath == null)
                throw new InvalidOperationException(name + " failed: " + draft.SpeechError);
            using var providerReceipt = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(directory, draft.Id + "-*.mp3.json").Single()));
            var provider = providerReceipt.RootElement;
            if (provider.GetProperty("model").GetString() != model)
                throw new InvalidOperationException(name + " used an unexpected model.");
            var info = new ProcessStartInfo(@"D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffmpeg.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-nostdin", "-v", "error", "-i", draft.AudioPath, "-f", "null", "-" }) info.ArgumentList.Add(argument);
            using var decoder = Process.Start(info)!;
            var error = decoder.StandardError.ReadToEndAsync();
            await decoder.WaitForExitAsync();
            if (decoder.ExitCode != 0) throw new InvalidOperationException(name + " decode failed: " + await error);
            var published = Path.Combine(directory, name + ".mp3");
            File.Copy(draft.AudioPath, published, overwrite: false);
            results.Add(new { name, text, model, provider = provider.GetProperty("provider").GetString(),
                voice = provider.GetProperty("voice").GetString(), path = published,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(published))), decoded = true });
            Console.WriteLine($"PASS live synthesis and decode: {name} ({model})");
        }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
            passed = true, scope = "Clipper provider receipts and MP3 decode for both configured models.", results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
