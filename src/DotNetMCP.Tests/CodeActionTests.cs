using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetMCP.Tests;

public class CodeActionTests
{
    private static readonly CodeActionService Service = new(NullLogger<CodeActionService>.Instance);

    /// <summary>A file with a missing using and a body-bodied method, in a throwaway project.</summary>
    private const string Sample = """
        using System;

        namespace Fx;

        public class Sample
        {
            public void UsesListWithoutUsing()
            {
                List<int> numbers = new List<int>();
                numbers.Add(1);
            }

            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private static async Task<(Document Document, IReadOnlyList<Diagnostic> Diagnostics)> SampleDocumentAsync()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: "Fx",
            assemblyName: "Fx",
            language: LanguageNames.CSharp,
            compilationOptions: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()));

        var document = workspace.AddDocument(project.Id, "Sample.cs", SourceText.From(Sample));

        var model = await document.GetSemanticModelAsync();
        return (document, model!.GetDiagnostics());
    }

    private static TextSpan SpanOfLineContaining(SourceText text, string needle)
    {
        var index = Enumerable.Range(0, text.Lines.Count)
            .First(i => text.Lines[i].ToString().Contains(needle, StringComparison.Ordinal));

        return text.Lines[index].Span;
    }

    [Test]
    public async Task OffersAddImportForAMissingUsing()
    {
        var (document, diagnostics) = await SampleDocumentAsync();
        var text = await document.GetTextAsync();
        var span = SpanOfLineContaining(text, "List<int> numbers");

        var actions = await Service.GetActionsAsync(
            document, span, diagnostics, includeFixes: true, includeRefactorings: false, CancellationToken.None);

        await Assert.That(actions.Select(a => a.Title)).Contains("using System.Collections.Generic;");
        await Assert.That(actions.All(a => a.Kind == CodeActionKind.Fix)).IsTrue();
        await Assert.That(actions.First(a => a.Title == "using System.Collections.Generic;").DiagnosticId).IsEqualTo("CS0246");
    }

    [Test]
    public async Task AddImportActuallyProducesTheEdit()
    {
        var (document, diagnostics) = await SampleDocumentAsync();
        var text = await document.GetTextAsync();
        var span = SpanOfLineContaining(text, "List<int> numbers");

        var actions = await Service.GetActionsAsync(
            document, span, diagnostics, includeFixes: true, includeRefactorings: false, CancellationToken.None);

        var addImport = actions.First(a => a.Title == "using System.Collections.Generic;");
        var changed = await CodeActionService.GetChangedSolutionAsync(addImport.Action, CancellationToken.None);

        await Assert.That(changed).IsNotNull();

        var updated = await changed!.GetDocument(document.Id)!.GetTextAsync();
        await Assert.That(updated.ToString()).Contains("using System.Collections.Generic;");
    }

    [Test]
    public async Task FlattensNestedActionGroupsIntoApplicableLeaves()
    {
        var (document, diagnostics) = await SampleDocumentAsync();
        var text = await document.GetTextAsync();
        var span = SpanOfLineContaining(text, "List<int> numbers");

        var actions = await Service.GetActionsAsync(
            document, span, diagnostics, includeFixes: true, includeRefactorings: false, CancellationToken.None);

        // "Generate type 'List'" is a group; only its children are individually applicable.
        var nested = actions.Where(a => a.Title.Contains(" > ", StringComparison.Ordinal)).ToArray();
        await Assert.That(nested).IsNotEmpty();

        // A grouping parent throws from GetOperationsAsync, so none may be listed on its own.
        await Assert.That(actions.Any(a => a.Action.NestedActions.Length > 0)).IsFalse();
    }

    [Test]
    public async Task OffersRefactoringsAndNotFixesWhenFixesAreExcluded()
    {
        var (document, diagnostics) = await SampleDocumentAsync();
        var text = await document.GetTextAsync();
        var span = SpanOfLineContaining(text, "public int Add");

        var actions = await Service.GetActionsAsync(
            document, span, diagnostics, includeFixes: false, includeRefactorings: true, CancellationToken.None);

        await Assert.That(actions).IsNotEmpty();
        await Assert.That(actions.All(a => a.Kind == CodeActionKind.Refactoring)).IsTrue();
        await Assert.That(actions.Select(a => a.Title)).Contains("Use expression body for method");
    }

    [Test]
    public async Task ReturnsNothingWhenBothCategoriesAreExcluded()
    {
        var (document, diagnostics) = await SampleDocumentAsync();
        var text = await document.GetTextAsync();
        var span = SpanOfLineContaining(text, "List<int> numbers");

        var actions = await Service.GetActionsAsync(
            document, span, diagnostics, includeFixes: false, includeRefactorings: false, CancellationToken.None);

        await Assert.That(actions).IsEmpty();
    }

    [Test]
    public async Task TitlesAreUniqueSoTheyCanIdentifyAnAction()
    {
        var (document, diagnostics) = await SampleDocumentAsync();
        var text = await document.GetTextAsync();
        var span = SpanOfLineContaining(text, "List<int> numbers");

        var actions = await Service.GetActionsAsync(
            document, span, diagnostics, includeFixes: true, includeRefactorings: true, CancellationToken.None);

        var titles = actions.Select(a => a.Title).ToArray();
        await Assert.That(titles.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(titles.Length);
    }
}
