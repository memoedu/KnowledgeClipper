using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.Workflow;

public static class DesktopContentSsh
{
    private sealed record Binding(string Host, string RemotePython, string RemoteHelper, string RemoteConfig, string Authority);
    private sealed class OutcomeUnknown() : Exception("Mac 内容结果尚未确认。请保留原草稿并重试同一请求，Mac 会防止重复提交。");
    private const int Limit = 4 * 1024 * 1024;
    private const string RequestSuffix = ".request.json";
    private const string ResponseSuffix = ".response.json";
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static FrozenContentSender? ForDraft(string root, ClipDraft draft)
    {
        if (!Guid.TryParseExact(draft.Id, "N", out _)) throw new InvalidDataException("草稿身份无效。");
        var directory = Path.Combine(root, "desktop-content-ssh", draft.Id);
        var frozen = Path.Combine(directory, "binding.json");
        var configuration = Path.Combine(root, "desktop-content-ssh.json");
        var path = File.Exists(frozen) ? frozen : configuration;
        if (!File.Exists(path)) return null;
        var endpoint = draft.Settings.ApiBaseUrl.TrimEnd('/') switch
        {
            "https://api.openai.com/v1" => "openai-responses",
            "https://api.deepseek.com" => "deepseek-chat",
            _ => null
        };
        if (endpoint == null && !File.Exists(frozen)) return null;
        if (endpoint == null) throw new OutcomeUnknown();
        var binding = WorkflowJson.Read<Binding>(path);
        if (binding.Host == null || !Regex.IsMatch(binding.Host, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z") ||
            !Guid.TryParseExact(binding.Authority, "N", out _) ||
            new[] { binding.RemotePython, binding.RemoteHelper, binding.RemoteConfig }.Any(value =>
                value == null || !Regex.IsMatch(value, @"\A/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\z")))
            throw new InvalidDataException("Mac 内容连接配置无效。");
        return (request, attempt, ct) => SendAsync(directory, draft.Id, binding, endpoint, request, attempt, ct);
    }

    private static async Task<HttpResponseMessage> SendAsync(string root, string draft, Binding binding, string endpoint,
        HttpRequestMessage request, int attempt, CancellationToken ct)
    {
        var expected = endpoint == "openai-responses" ? "https://api.openai.com/v1/responses" : "https://api.deepseek.com/chat/completions";
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != expected || request.Content == null)
            throw new OutcomeUnknown();
        var body = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (body.Length > Limit) throw new OutcomeUnknown();
        ct.ThrowIfCancellationRequested();
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new { draft, endpoint, attempt, authority = binding.Authority, hash = Hash(body), body });
        var identity = Hash(envelope);
        var reservation = Path.Combine(root, identity + RequestSuffix);
        var result = Path.Combine(root, identity + ResponseSuffix);
        var wire = JsonSerializer.SerializeToUtf8Bytes(new { identity, envelope });
        var start = new ProcessStartInfo("ssh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ClearAllForwardings=yes",
            "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", "SendEnv=-*", "-o", "ConnectTimeout=5", "-o", "ServerAliveInterval=5",
            "-o", "ServerAliveCountMax=2", binding.Host, $"exec {binding.RemotePython} -I -B {binding.RemoteHelper} {binding.RemoteConfig}" })
            start.ArgumentList.Add(value);
        using var process = new Process { StartInfo = start };
        try
        {
            Directory.CreateDirectory(root);
            PublishOnce(Path.Combine(root, "binding.json"), JsonSerializer.SerializeToUtf8Bytes(binding, WorkflowJson.Options));
            if (File.Exists(result)) return Response(await File.ReadAllBytesAsync(result, ct).ConfigureAwait(false), identity);
            if (Directory.EnumerateFiles(root, "*" + RequestSuffix).Any(path => path != reservation &&
                !File.Exists(path[..^RequestSuffix.Length] + ResponseSuffix))) throw new OutcomeUnknown();
            PublishOnce(reservation, envelope);
            if (!process.Start()) throw new OutcomeUnknown();
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            var output = BoundedRead(process.StandardOutput.BaseStream, ct);
            await process.StandardInput.BaseStream.WriteAsync(wire, ct).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var received = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new OutcomeUnknown();
            var response = Response(received, identity);
            PublishOnce(result, received);
            return response;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or JsonException or
                                      InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new OutcomeUnknown();
        }
    }

    private static async Task<byte[]> BoundedRead(Stream stream, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) return result.ToArray();
            if (result.Length + count > 2 * Limit) throw new OutcomeUnknown();
            result.Write(buffer, 0, count);
        }
    }

    private static HttpResponseMessage Response(byte[] receipt, string identity)
    {
        using var data = JsonDocument.Parse(receipt);
        var value = data.RootElement;
        var body = value.GetProperty("body").GetBytesFromBase64();
        var status = value.GetProperty("status").GetInt32();
        if (status is < 200 or > 599 || body.Length > Limit || value.GetProperty("identity").GetString() != identity ||
            value.GetProperty("sha256").GetString() != Hash(body)) throw new OutcomeUnknown();
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
    }

    private static bool PublishOnce(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new OutcomeUnknown();
            return false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
