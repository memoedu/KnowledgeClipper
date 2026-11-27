using System.Diagnostics;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Kernel.WorkerRuntime;

namespace KnowledgeClipper.Core.V3.Kernel.WorkerAdapter;

internal static class SshContentProcessInvoker
{
    internal static async Task<WorkerProcessInvocationResult> InvokeAsync(AdmittedAuthorityWorker worker,
        LegacyAttemptPackage package, string configuration, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(configuration))
            throw new InvalidDataException("Content SSH configuration must have an absolute path.");
        using var config = JsonDocument.Parse(await File.ReadAllBytesAsync(configuration, cancellationToken).ConfigureAwait(false));
        var python = config.RootElement.GetProperty("python").GetString();
        var helper = Path.Combine(AppContext.BaseDirectory, "remote-content.py");
        if (python is null || !Path.IsPathFullyQualified(python) || !File.Exists(python) || !File.Exists(helper))
            throw new InvalidDataException("The configured Content SSH runtime is unavailable.");

        var stderrPath = Path.Combine(Path.GetDirectoryName(package.RequestPath)!, "stderr.log");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            }
        };
        var remoteTimeout = Math.Clamp((int)timeout.TotalSeconds - 15, 5, 1800).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var arg in new[] { helper, "submit", configuration, package.JobRoot, package.RequestPath,
                     worker.WorkerVersion, remoteTimeout })
            process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start())
            throw new InvalidOperationException("Could not start the configured Content SSH runtime.");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw OutcomeUnknown();
        }
        var diagnostic = (await stderr.ConfigureAwait(false)).Trim();
        await stdout.ConfigureAwait(false);
        // The helper output is fixed status codes only. Do not include a worker's raw model or SSH logs.
        var safe = diagnostic is "CONTENT_PRECHECK_REJECTED" or "CONTENT_REMOTE_OUTCOME_UNKNOWN" ? diagnostic : "";
        await File.AppendAllTextAsync(stderrPath, safe + Environment.NewLine, CancellationToken.None).ConfigureAwait(false);
        if (process.ExitCode == 75) throw OutcomeUnknown();
        return new(process.ExitCode, false, false, stderrPath, "ssh");
    }

    internal static WorkerExternalStateUnknownException OutcomeUnknown() =>
        new("content_ssh_outcome_unknown", "Remote Content attempt is unresolved. Read back the same frozen request; do not submit a new attempt.");
}
