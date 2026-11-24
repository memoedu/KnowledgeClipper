using KnowledgeClipper.Workflow;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using System.Net;
using System.Text;
using System.Text.Json;

var host = WebAssemblyHostBuilder.CreateDefault(args).Build();
Browser.Js = host.Services.GetRequiredService<IJSRuntime>();
await host.RunAsync();

public sealed record HostResponse(int Status, string Content, string Mime);
public sealed record BrowserSource(string Text, string Title, string Route, string? Prefix = null, string? Suffix = null, bool IsImage = false);
public sealed record BrowserOptions(string Action, BrowserSource Source, JsonElement Values, string Model, string? ImageModel,
    Dictionary<string,string> Defaults, Dictionary<string,string> Overrides, string? SavedId = null);
public sealed record BrowserImage(string Data, string Role);

public static class Browser
{
    internal static IJSRuntime Js = null!;
    internal static readonly JsonSerializerOptions Json = new(WorkflowJson.Options) { WriteIndented = false };
    private static BrowserSession? session;
    private static BrowserSession Active => session ?? throw new InvalidOperationException("请先选择材料。");

    [JSInvokable]
    public static async Task<string> Open(string serialized, BrowserImage[] images)
    {
        var options = JsonSerializer.Deserialize<BrowserOptions>(serialized, Json)!;
        session = new(options);
        await session.Initialize(images);
        return session.View();
    }

    [JSInvokable] public static Task<string> Command(string command) => Active.Command(JsonSerializer.Deserialize<JsonElement>(command, Json));
    [JSInvokable] public static string View() => Active.View();
    internal static ValueTask<T> Port<T>(string op, object? data = null, CancellationToken ct = default) =>
        Js.InvokeAsync<T>("clipperHost.call", ct, [op, data]);

    public sealed class Transport : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var result = request.Method == HttpMethod.Get
                ? await Port<HostResponse>("reference", request.RequestUri!.AbsoluteUri, ct)
                : await Port<HostResponse>("model", new { path = request.RequestUri!.AbsolutePath, body }, ct);
            return new((HttpStatusCode)result.Status) {Content = new StringContent(result.Content, Encoding.UTF8, result.Mime)};
        }
    }

    public sealed class Ocr : IImageOcrExtractor
    {
        public async Task<OcrExtraction> ExtractAsync(ImageAsset image, LanguageSettings settings, string output, Action<string> progress,
            CancellationToken ct, string? resumeManifest = null)
        {
            var text = await Port<string>("ocr", new { data = Convert.ToBase64String(image.ReadVerified()), settings.MineruLanguage,
                settings.MineruFormula, settings.MineruTable, settings.MineruTimeoutSeconds }, ct);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("图片未识别出文字。");
            Directory.CreateDirectory(output);
            var path = Path.Combine(output, "ocr.txt");
            await File.WriteAllTextAsync(path, text, ct);
            return new(text, path);
        }
    }
}
