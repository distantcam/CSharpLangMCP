using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;

namespace DotNetMCP.Tests;

public class SymbolResolverTests
{
    [Test]
    public async Task ResolvesAPlainName()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var (symbol, error) = await SymbolResolver.ResolveAsync(
            solution, "RoslynWorkspaceService", CancellationToken.None);

        await Assert.That(error).IsNull();
        await Assert.That(symbol!.Name).IsEqualTo("RoslynWorkspaceService");
    }

    [Test]
    public async Task ResolvesAQualifiedNameToTheRightOverloadSet()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var (symbol, error) = await SymbolResolver.ResolveAsync(
            solution, "SymbolResolver.ResolveAtPositionAsync", CancellationToken.None);

        await Assert.That(error).IsNull();
        await Assert.That(symbol!.ContainingType.Name).IsEqualTo("SymbolResolver");
        await Assert.That(symbol.Name).IsEqualTo("ResolveAtPositionAsync");
    }

    [Test]
    public async Task DocumentationIdRoundTripsBackToTheSameSymbol()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var (original, error) = await SymbolResolver.ResolveAsync(
            solution, "SymbolFormatter.Truncate", CancellationToken.None);
        await Assert.That(error).IsNull();

        var id = SymbolResolver.IdFor(original!);
        await Assert.That(id).IsNotNull();

        var (resolved, roundTripError) = await SymbolResolver.ResolveAsync(solution, id!, CancellationToken.None);
        await Assert.That(roundTripError).IsNull();
        await Assert.That(SymbolEqualityComparer.Default.Equals(original, resolved)).IsTrue();
    }

    [Test]
    public async Task ReportsAmbiguityInsteadOfGuessing()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        // NavigationTools and WorkspaceTools each declare their own TextForAsync.
        var (symbol, error) = await SymbolResolver.ResolveAsync(solution, "TextForAsync", CancellationToken.None);

        await Assert.That(symbol).IsNull();
        await Assert.That(error).Contains("ambiguous");
        await Assert.That(error).Contains("id: ");
    }

    [Test]
    public async Task ReportsAMissingSymbolClearly()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var (symbol, error) = await SymbolResolver.ResolveAsync(
            solution, "NoSuchSymbolAnywhere", CancellationToken.None);

        await Assert.That(symbol).IsNull();
        await Assert.That(error).Contains("NoSuchSymbolAnywhere");
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

        var (symbol, error) = await SymbolResolver.ResolveAtPositionAsync(
            solution, path, line, character, CancellationToken.None);

        await Assert.That(error).IsNull();
        await Assert.That(symbol!.Name).IsEqualTo("ExecuteAsync");
    }

    [Test]
    public async Task RejectsAPositionPastTheEndOfTheFile()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var path = Path.Combine(WorkspaceFixture.RepositoryRoot, "src", "DotNetMCP", "Tools", "ToolBase.cs");

        var (symbol, error) = await SymbolResolver.ResolveAtPositionAsync(
            solution, path, line: 100_000, character: 0, CancellationToken.None);

        await Assert.That(symbol).IsNull();
        await Assert.That(error).Contains("past the end");
    }
}
