using System.ComponentModel;
using System.Text;
using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotNetMCP.Tools;

/// <summary>Workspace lifecycle and diagnostics.</summary>
[McpServerToolType]
public sealed class WorkspaceTools(ILogger<WorkspaceTools> logger, RoslynWorkspaceService workspace)
{
    [McpServerTool(Name = "csharp_load_workspace")]
    [Description("Load a C# solution or project for analysis. Accepts a directory, or a path to a .slnx, .sln, .slnf or .csproj file. Other tools load this automatically if they can find a workspace, but calling this first is faster and reports load problems.")]
    public Task<string> LoadWorkspaceAsync(
        [Description("Directory, or path to a .slnx / .sln / .slnf / .csproj file")] string path,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_load_workspace", async ct =>
        {
            var result = await workspace.LoadAsync(path, ct);

            var sb = new StringBuilder();
            sb.AppendLine($"Loaded {result.LoadedPath} in {result.Duration.TotalSeconds:F1}s");
            sb.AppendLine();
            sb.AppendLine($"{result.Projects.Count} project(s):");

            foreach (var project in result.Projects)
            {
                var tfm = project.TargetFramework is null ? "" : $" [{project.TargetFramework}]";
                sb.AppendLine($"  - {project.Name}{tfm} - {project.DocumentCount} document(s)");
            }

            if (result.Failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{result.Failures.Count} load warning(s) - these projects were skipped, the rest loaded fine:");
                foreach (var failure in result.Failures.Take(20))
                    sb.AppendLine($"  {SymbolFormatter.Truncate(failure, 300)}");

                SymbolFormatter.AppendTruncationNote(sb, Math.Min(20, result.Failures.Count), result.Failures.Count, "warning(s)");
            }

            if (result.Projects.Count == 0)
                sb.AppendLine("\nNo projects loaded. Check the load warnings above.");

            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_unload_workspace")]
    [Description("Unload the workspace and release memory and file handles. Roslyn memory-maps referenced assemblies, so call this before rebuilding the solution if a build reports locked files.")]
    public Task<string> UnloadWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_unload_workspace", ct =>
        {
            if (!workspace.IsLoaded)
                return Task.FromResult("No workspace is loaded.");

            var path = workspace.LoadedPath;
            workspace.Unload();

            return Task.FromResult($"Unloaded {path}. Call csharp_load_workspace to load it again.");
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_diagnostics")]
    [Description("Get compiler errors and warnings. Scope can be 'solution', a project name, or an absolute path to a .cs file. Each diagnostic includes the offending source line.")]
    public Task<string> GetDiagnosticsAsync(
        [Description("'solution', a project name, or an absolute path to a .cs file")] string scope = "solution",
        [Description("Lowest severity to report: Error, Warning, Info or Hidden. Default Warning.")] string minimumSeverity = "Warning",
        [Description("Unsaved file content, used only when scope is a file path")] string? content = null,
        [Description("Maximum diagnostics to return (default 100)")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_diagnostics", async ct =>
        {
            if (!Enum.TryParse<DiagnosticSeverity>(minimumSeverity, ignoreCase: true, out var floor))
                return $"Error: Unknown severity '{minimumSeverity}'. Use Error, Warning, Info or Hidden.";

            var isFile = scope.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (isFile && content is not null)
                workspace.SetOverlay(Path.GetFullPath(scope), content);

            var solution = await workspace.GetSolutionAsync(isFile ? scope : null, ct);

            var (diagnostics, description) = await CollectAsync(solution, scope, isFile, ct);
            if (diagnostics is null)
                return description;

            var filtered = diagnostics
                .Where(d => d.Severity >= floor)
                .OrderByDescending(d => d.Severity)
                .ThenBy(d => d.Location.GetLineSpan().Path, StringComparer.Ordinal)
                .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
                .ToArray();

            if (filtered.Length == 0)
                return $"No diagnostics at or above {floor} in {description}.";

            var errors = filtered.Count(d => d.Severity == DiagnosticSeverity.Error);
            var sb = new StringBuilder();
            sb.AppendLine($"{filtered.Length} diagnostic(s) in {description} ({errors} error(s)):");
            sb.AppendLine();

            var textCache = new Dictionary<string, SourceText?>(RoslynWorkspaceService.PathComparer);

            foreach (var diagnostic in filtered.Take(maxResults))
            {
                sb.AppendLine($"[{diagnostic.Severity.ToString().ToUpperInvariant()}] {diagnostic.Id}: {diagnostic.GetMessage()}");
                SymbolFormatter.AppendLocationLine(sb, diagnostic.Location, await TextForAsync(solution, diagnostic.Location, textCache, ct));
                sb.AppendLine();
            }

            SymbolFormatter.AppendTruncationNote(sb, Math.Min(maxResults, filtered.Length), filtered.Length, "diagnostic(s)");
            return sb.ToString();
        }, cancellationToken);
    }

    /// <summary>Gathers diagnostics for a solution, a named project, or a single file.</summary>
    private static async Task<(IReadOnlyList<Diagnostic>? Diagnostics, string Description)> CollectAsync(
        Solution solution, string scope, bool isFile, CancellationToken ct)
    {
        if (isFile)
        {
            var document = RoslynWorkspaceService.FindDocument(solution, scope);
            if (document is null)
                return (null, $"Error: File is not part of the loaded workspace: {scope}");

            var model = await document.GetSemanticModelAsync(ct);
            if (model is null)
                return (null, $"Error: Could not build a semantic model for {scope}");

            // Syntax plus declaration plus method-body diagnostics for this tree only.
            return (model.GetDiagnostics(cancellationToken: ct), Path.GetFileName(document.FilePath ?? scope));
        }

        var isSolution = scope.Equals("solution", StringComparison.OrdinalIgnoreCase);
        var projects = isSolution
            ? solution.Projects.ToArray()
            : solution.Projects.Where(p =>
                p.Name.Equals(scope, StringComparison.OrdinalIgnoreCase) ||
                // Multi-targeted projects are named "Foo(net10.0)"; match the bare name too.
                p.Name.StartsWith(scope + "(", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (projects.Length == 0)
        {
            var names = string.Join(", ", solution.Projects.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            return (null, $"Error: No project named '{scope}'. Loaded projects: {names}");
        }

        var all = new List<Diagnostic>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is not null)
                all.AddRange(compilation.GetDiagnostics(ct));
        }

        return (all, isSolution ? "the solution" : $"project {scope}");
    }

    private static async Task<SourceText?> TextForAsync(
        Solution solution, Location location, Dictionary<string, SourceText?> cache, CancellationToken ct)
    {
        if (!location.IsInSource)
            return null;

        var path = location.GetLineSpan().Path;
        if (cache.TryGetValue(path, out var cached))
            return cached;

        var document = RoslynWorkspaceService.FindDocument(solution, path);
        var text = document is null ? null : await document.GetTextAsync(ct);
        cache[path] = text;
        return text;
    }
}
