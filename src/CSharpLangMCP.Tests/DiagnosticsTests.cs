using CSharpLangMCP.Roslyn;
using CSharpLangMCP.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSharpLangMCP.Tests;

/// <summary>
/// Covers the analyzer half of <c>csharp_diagnostics</c> against this repository, which sets
/// <c>AnalysisMode</c>, <c>EnforceCodeStyleInBuild</c> and <c>TreatWarningsAsErrors</c> and so has a
/// real analyzer set and a real severity configuration to exercise.
/// </summary>
public class DiagnosticsTests
{
    private static readonly AnalyzerService s_service = new(NullLogger<AnalyzerService>.Instance);

    /// <summary>
    /// Rethrowing by name trips CA2200 and the braceless <c>if</c> trips IDE0011 - two analyzer
    /// diagnostics the compiler never reports.
    /// </summary>
    private const string Probe = """
        namespace CSharpLangMCP.Probe;

        internal static class AnalyzerProbe
        {
            public static void Rethrow(int n)
            {
                try
                {
                    if (n > 0)
                        throw new System.InvalidOperationException("probe");
                }
                catch (System.InvalidOperationException ex)
                {
                    throw ex;
                }
            }
        }
        """;

    private static async Task<Project> ServerProjectAsync()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();
        return solution.Projects.First(p =>
            p.Name.StartsWith("CSharpLangMCP", StringComparison.Ordinal) &&
            !p.Name.Contains("Tests", StringComparison.Ordinal));
    }

    /// <summary>Adds the probe to the in-memory solution only; nothing is written to disk.</summary>
    private static async Task<Document> ProbeDocumentAsync()
        => (await ServerProjectAsync()).AddDocument("AnalyzerProbe.cs", Probe);

    [Test]
    public async Task LoadsTheAnalyzersTheProjectConfigures()
    {
        var analyzers = s_service.GetAnalyzers(await ServerProjectAsync());

        // The SDK contributes NetAnalyzers and the C# code-style analyzers to every project here.
        // An empty set means analyzer references failed to load, which is silent otherwise.
        await Assert.That(analyzers).IsNotEmpty();
    }

    [Test]
    public async Task ReportsAnalyzerDiagnosticsTheCompilerCannotSee()
    {
        var document = await ProbeDocumentAsync();

        var compilerOnly = (await document.GetSemanticModelAsync())!.GetDiagnostics();
        await Assert.That(compilerOnly.Any(d => d.Id == "CA2200")).IsFalse();

        var analyzer = await s_service.GetFileDiagnosticsAsync(document, CancellationToken.None);
        var ca2200 = analyzer.Where(d => d.Id == "CA2200").ToArray();

        await Assert.That(ca2200).IsNotEmpty();

        // TreatWarningsAsErrors reaches the workspace as a compilation option, so this analyzer
        // warning arrives as an error - the same severity `dotnet build` reports for it.
        await Assert.That(ca2200[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    }

    [Test]
    public async Task AnalyzerResultsAreAdditiveToTheCompilers()
    {
        var analyzer = await s_service.GetFileDiagnosticsAsync(await ProbeDocumentAsync(), CancellationToken.None);

        // Callers concatenate these with Compilation.GetDiagnostics without deduplicating, which is
        // only safe because the analyzer pass never re-reports a compiler diagnostic.
        await Assert.That(analyzer.Any(d => d.Id.StartsWith("CS", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task FileScopedAnalysisStaysWithinTheFile()
    {
        var document = await ProbeDocumentAsync();
        var analyzer = await s_service.GetFileDiagnosticsAsync(document, CancellationToken.None);

        // The point of the per-file entry points is not paying for the whole compilation; a result
        // located in another file would mean the scoping had been lost.
        var strays = analyzer
            .Where(d => d.Location.IsInSource)
            .Where(d => !string.Equals(d.Location.GetLineSpan().Path, "AnalyzerProbe.cs", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(strays).IsEmpty();
    }

    [Test]
    public async Task ProjectWithoutAnalyzersReportsNothingRatherThanFailing()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Bare", LanguageNames.CSharp);

        await Assert.That(s_service.GetAnalyzers(project)).IsEmpty();
        await Assert.That(await s_service.GetProjectDiagnosticsAsync(project, CancellationToken.None)).IsEmpty();
    }

    /// <summary>
    /// The IDExxxx fixers live in the Features assemblies that <see cref="CodeActionService"/>
    /// composes, so feeding it analyzer diagnostics is what makes them reachable. CAxxxx fixers ship
    /// in the NetAnalyzers package instead and stay out of reach - their diagnostics are reported by
    /// csharp_diagnostics but have no fix here.
    /// </summary>
    [Test]
    public async Task AnalyzerDiagnosticsMakeTheirFixesReachable()
    {
        var document = await ProbeDocumentAsync();
        var text = await document.GetTextAsync();
        var span = text.Lines.First(l => l.ToString().Contains("if (n > 0)", StringComparison.Ordinal)).Span;

        var codeActions = new CodeActionService(NullLogger<CodeActionService>.Instance);

        var compilerOnly = await codeActions.GetActionsAsync(
            document, span, (await document.GetSemanticModelAsync())!.GetDiagnostics(),
            includeFixes: true, includeRefactorings: false, CancellationToken.None);

        await Assert.That(compilerOnly.Any(a => a.DiagnosticId == "IDE0011")).IsFalse();

        var analyzerDiagnostics = await s_service.GetFileDiagnosticsAsync(document, CancellationToken.None);
        var withAnalyzers = await codeActions.GetActionsAsync(
            document, span, analyzerDiagnostics, includeFixes: true, includeRefactorings: false, CancellationToken
                .None);

        await Assert.That(withAnalyzers.Any(a => a.DiagnosticId == "IDE0011")).IsTrue();
        await Assert.That(withAnalyzers.Select(a => a.Title)).Contains("Add braces");
    }

    [Test]
    public async Task ToolSaysSoWhenAnalyzersWereNotRun()
    {
        var (service, _) = await WorkspaceFixture.GetAsync();
        var tool = new WorkspaceTools(NullLogger<WorkspaceTools>.Instance, service, s_service);

        var file = Path.Combine(WorkspaceFixture.RepositoryRoot, "src", "CSharpLangMCP", "Program.cs");
        var result = await tool.GetDiagnosticsAsync(file, includeAnalyzers: false);

        // A clean compiler-only result is not a clean build, and the tool has to say which it gave.
        await Assert.That(result).Contains("includeAnalyzers=true");

        var withAnalyzers = await tool.GetDiagnosticsAsync(file, includeAnalyzers: true);
        await Assert.That(withAnalyzers).DoesNotContain("includeAnalyzers=true");
    }
}
