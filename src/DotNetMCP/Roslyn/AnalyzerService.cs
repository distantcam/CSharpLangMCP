using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DotNetMCP.Roslyn;

/// <summary>
/// Runs the analyzers a project actually configures, so diagnostics match what a build reports.
/// </summary>
/// <remarks>
/// <see cref="Compilation.GetDiagnostics(CancellationToken)"/> is the compiler alone - it never runs
/// a <see cref="DiagnosticAnalyzer"/>. A project with <c>AnalysisMode</c> or
/// <c>EnforceCodeStyleInBuild</c> set therefore fails its build on CAxxxx and IDExxxx rules that the
/// compiler-only path cannot see. MSBuildWorkspace already populates
/// <see cref="Project.AnalyzerReferences"/> and <see cref="Project.AnalyzerOptions"/> from the
/// project file, so closing that gap needs no build and no external process - only that those
/// analyzers be asked.
/// </remarks>
public sealed class AnalyzerService(ILogger<AnalyzerService> logger)
{
    /// <summary>
    /// Analyzer instances per project. Loading the assemblies behind a reference is the expensive
    /// part, and analyzer references only change when the workspace is reloaded.
    /// </summary>
    private readonly ConcurrentDictionary<ProjectId, ImmutableArray<DiagnosticAnalyzer>> _cache = new();

    /// <summary>Every analyzer the project configures for its own language.</summary>
    /// <remarks>
    /// A reference that cannot be loaded costs only its own analyzers: an unresolvable analyzer
    /// package is a normal state for a half-restored solution, and should not fail the whole call.
    /// </remarks>
    public ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(Project project)
    {
        return _cache.GetOrAdd(project.Id, _ =>
        {
            var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

            foreach (var reference in project.AnalyzerReferences)
            {
                try
                {
                    analyzers.AddRange(reference.GetAnalyzers(project.Language));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipping analyzer reference {Reference}", reference.Display);
                }
            }

            logger.LogDebug("{Count} analyzer(s) for project {Project}", analyzers.Count, project.Name);
            return analyzers.ToImmutable();
        });
    }

    /// <summary>Analyzer diagnostics for a whole project, or an empty set when it configures none.</summary>
    /// <remarks>
    /// The returned diagnostics are analyzer diagnostics only, so they concatenate with
    /// <see cref="Compilation.GetDiagnostics(CancellationToken)"/> without duplicating anything.
    /// </remarks>
    public async Task<ImmutableArray<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken ct)
    {
        if (await CreateAsync(project, ct) is not { } withAnalyzers)
            return [];

        var start = System.Diagnostics.Stopwatch.StartNew();
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(ct);

        logger.LogDebug(
            "Analyzed project {Project} in {Ms} ms, {Count} diagnostic(s)",
            project.Name, start.ElapsedMilliseconds, diagnostics.Length);

        return diagnostics;
    }

    /// <summary>Analyzer diagnostics for a single file.</summary>
    /// <remarks>
    /// Scoped to one syntax tree rather than the whole compilation, which is what keeps a file-scoped
    /// call affordable on a solution where a full analyzer run takes minutes.
    /// </remarks>
    public async Task<ImmutableArray<Diagnostic>> GetFileDiagnosticsAsync(Document document, CancellationToken ct)
    {
        if (await CreateAsync(document.Project, ct) is not { } withAnalyzers)
            return [];

        // The semantic model has to come from the same compilation the analyzers were bound to.
        if (await document.GetSyntaxTreeAsync(ct) is not { } tree)
            return [];

        var model = withAnalyzers.Compilation.GetSemanticModel(tree);

        var syntax = await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, ct);
        var semantic = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, ct);

        return [.. syntax, .. semantic];
    }

    /// <summary>
    /// Binds a project's analyzers to its compilation, or returns null when there is nothing to run.
    /// </summary>
    /// <remarks>
    /// A misbehaving analyzer is reported and dropped rather than thrown: analyzers come from NuGet
    /// packages this server does not control, and one that faults should cost its own rules only.
    /// </remarks>
    private async Task<CompilationWithAnalyzers?> CreateAsync(Project project, CancellationToken ct)
    {
        var analyzers = GetAnalyzers(project);
        if (analyzers.IsEmpty)
            return null;

        if (await project.GetCompilationAsync(ct) is not { } compilation)
            return null;

        var options = new CompilationWithAnalyzersOptions(
            project.AnalyzerOptions,
            onAnalyzerException: (ex, analyzer, _) =>
                logger.LogWarning(ex, "Analyzer {Analyzer} failed in {Project}", analyzer.GetType().Name, project.Name),
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false);

        return compilation.WithAnalyzers(analyzers, options);
    }
}
