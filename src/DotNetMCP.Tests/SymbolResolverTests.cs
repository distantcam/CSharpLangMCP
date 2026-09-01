using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;

namespace DotNetMCP.Tests;

public class SymbolResolverTests
{
    [Test]
    public async Task ResolvesAPlainName()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var lookup = await SymbolResolver.ResolveAsync(solution, "RoslynWorkspaceService", CancellationToken.None);

        await Assert.That(lookup).IsTypeOf<SymbolLookup.Found>();
        await Assert.That(((SymbolLookup.Found)lookup).Symbol.Name).IsEqualTo("RoslynWorkspaceService");
    }

    [Test]
    public async Task ResolvesAQualifiedNameToTheRightOverloadSet()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var lookup = await SymbolResolver.ResolveAsync(
            solution, "SymbolResolver.ResolveAtPositionAsync", CancellationToken.None);

        await Assert.That(lookup).IsTypeOf<SymbolLookup.Found>();

        var symbol = ((SymbolLookup.Found)lookup).Symbol;
        await Assert.That(symbol.ContainingType.Name).IsEqualTo("SymbolResolver");
        await Assert.That(symbol.Name).IsEqualTo("ResolveAtPositionAsync");
    }

    [Test]
    public async Task DocumentationIdRoundTripsBackToTheSameSymbol()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var first = await SymbolResolver.ResolveAsync(solution, "SymbolFormatter.Truncate", CancellationToken.None);
        await Assert.That(first).IsTypeOf<SymbolLookup.Found>();

        var original = ((SymbolLookup.Found)first).Symbol;
        var id = SymbolResolver.IdFor(original);
        await Assert.That(id).IsNotNull();

        var second = await SymbolResolver.ResolveAsync(solution, id!, CancellationToken.None);
        await Assert.That(second).IsTypeOf<SymbolLookup.Found>();

        var resolved = ((SymbolLookup.Found)second).Symbol;
        await Assert.That(SymbolEqualityComparer.Default.Equals(original, resolved)).IsTrue();
    }

    [Test]
    public async Task ReportsAmbiguityInsteadOfGuessing()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        // Several tool classes declare a private ResolveAsync alongside SymbolResolver's public one.
        var lookup = await SymbolResolver.ResolveAsync(solution, "ResolveAsync", CancellationToken.None);

        await Assert.That(lookup).IsTypeOf<SymbolLookup.Ambiguous>();
        await Assert.That(((SymbolLookup.Ambiguous)lookup).Candidates.Count).IsGreaterThan(1);
    }

    [Test]
    public async Task ReportsAMissingSymbolClearly()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var lookup = await SymbolResolver.ResolveAsync(solution, "NoSuchSymbolAnywhere", CancellationToken.None);

        await Assert.That(lookup).IsTypeOf<SymbolLookup.NotFound>();
        await Assert.That(((SymbolLookup.NotFound)lookup).Message).Contains("NoSuchSymbolAnywhere");
    }

    [Test]
    public async Task ResolvesBySourcePosition()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var path = Path.Combine(WorkspaceFixture.RepositoryRoot, "src", "DotNetMCP", "Tools", "ToolBase.cs");
        var document = RoslynWorkspaceService.FindDocument(solution, path);
        await Assert.That(document).IsNotNull();

        var text = await document!.GetTextAsync();
        var line = Enumerable.Range(0, text.Lines.Count)
            .First(i => text.Lines[i].ToString().Contains("public static async Task<string> ExecuteAsync"));
        var character = text.Lines[line].ToString().IndexOf("ExecuteAsync", StringComparison.Ordinal);

        var lookup = await SymbolResolver.ResolveAtPositionAsync(
            solution, path, line, character, CancellationToken.None);

        await Assert.That(lookup).IsTypeOf<SymbolLookup.Found>();
        await Assert.That(((SymbolLookup.Found)lookup).Symbol.Name).IsEqualTo("ExecuteAsync");
    }

    [Test]
    public async Task RejectsAPositionPastTheEndOfTheFile()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var path = Path.Combine(WorkspaceFixture.RepositoryRoot, "src", "DotNetMCP", "Tools", "ToolBase.cs");

        var lookup = await SymbolResolver.ResolveAtPositionAsync(
            solution, path, line: 100_000, character: 0, CancellationToken.None);

        await Assert.That(lookup).IsTypeOf<SymbolLookup.NotFound>();
        await Assert.That(((SymbolLookup.NotFound)lookup).Message).Contains("past the end");
    }
}
