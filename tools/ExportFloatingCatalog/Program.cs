using System.Text.Json;
using System.Diagnostics;
using KnowledgeClipper.Workflow;

if (args.Length != 2) throw new ArgumentException("Usage: ExportFloatingCatalog <tracked modes directory> <destination>");
var modes = Path.GetFullPath(args[0]);
var info = new ProcessStartInfo("git") { WorkingDirectory = modes, RedirectStandardOutput = true, UseShellExecute = false };
foreach (var option in new[] {"ls-files", "-z", "--", "."}) info.ArgumentList.Add(option);
using var git = Process.Start(info) ?? throw new IOException("Could not read the tracked mode definitions.");
var tracked = git.StandardOutput.ReadToEnd();
git.WaitForExit();
if (git.ExitCode != 0 || tracked.Length == 0) throw new InvalidDataException("Expected a Git checkout of the canonical modes.");
var files = tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(name => Path.Combine(modes, name))
    .Where(file => Path.GetExtension(file) is ".json" or ".md")
    .OrderBy(file => file, StringComparer.Ordinal)
    .ToDictionary(file => Path.GetRelativePath(modes, file).Replace('\\', '/'), File.ReadAllText);
var catalog = new { workflow = FloatingSettings.Catalog(), rules = files };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(catalog, WorkflowJson.Options) + "\n");
