using System.Security.Cryptography;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Kernel.Common;
using KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

internal static class ContentSshCanary
{
    internal static async Task<int> RunAsync(string jobRoot, string requestFile, string version)
    {
        var root = Path.GetFullPath(jobRoot);
        var path = Path.GetFullPath(requestFile);
        var request = JsonSerializer.Deserialize<LegacyWorkerRequest>(await File.ReadAllBytesAsync(path), KernelJson.Options)
            ?? throw new InvalidDataException("Missing canary request.");
        if (Path.GetRelativePath(root, path).Replace('\\', '/') != request.AttemptPath + "/request.json"
            || request.WorkerId != "content.deepseek" || request.Payload.GetProperty("providerMode").GetString() != "replay")
            throw new InvalidDataException("Only the frozen replay fixture is allowed.");
        var worker = new AdmittedAuthorityWorker(request.WorkerId, version, "1.0.0",
            "must-not-start-local-content-worker", "", [request.StageId], "compute", "idempotency-key");
        var package = new LegacyAttemptPackage(root, path, Path.Combine(root, request.ResultPath),
            Path.Combine(root, request.CancelSignal.Path), request);
        var invoker = new SystemWorkerProcessInvoker();
        try
        {
            var invoked = await invoker.InvokeAsync(worker, package, TimeSpan.FromSeconds(45), CancellationToken.None);
            if (invoked.ExitCode != 0 || invoked.Transport != "ssh" || !File.Exists(package.ResultPath))
                throw new InvalidDataException("The original Core invoker did not consume a remote result.");
            using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(package.ResultPath));
            var body = result.RootElement;
            if (body.GetProperty("status").GetString() != "completed"
                || body.GetProperty("attemptId").GetString() != request.AttemptId
                || body.GetProperty("idempotencyKey").GetString() != request.IdempotencyKey)
                throw new InvalidDataException("Core canary did not receive the original completed attempt.");
            Console.WriteLine(JsonSerializer.Serialize(new { pass = true, transport = invoked.Transport,
                artifactCount = body.GetProperty("outputs").GetArrayLength(),
                resultSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package.ResultPath))) }));
            return 0;
        }
        catch (WorkerExternalStateUnknownException error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { pass = false, status = "unknown", code = error.Code }));
            return 75;
        }
    }
}
