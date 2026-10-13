using CSharpLangMCP.Tools;
using Microsoft.CodeAnalysis;

namespace CSharpLangMCP.Tests;

/// <summary>Covers <c>csharp_decompile</c> against assemblies this repository actually references.</summary>
public class DecompileTests
{
    private static async Task<(Solution Solution, Compilation Compilation)> ServerAsync()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();
        var project = solution.Projects.First(p =>
            p.Name.StartsWith("CSharpLangMCP", StringComparison.Ordinal) &&
            !p.Name.Contains("Tests", StringComparison.Ordinal));

        return (solution, (await project.GetCompilationAsync())!);
    }

    private static async Task<string> DecompileAsync(string id)
    {
        var (solution, compilation) = await ServerAsync();
        var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation)
            ?? throw new InvalidOperationException("Fixture symbol missing: " + id);

        return await DecompileTools.DecompileSymbolAsync(solution, symbol, CancellationToken.None);
    }

    [Test]
    public async Task BclMemberComesFromTheRuntimeNotTheReferenceAssembly()
    {
        var result = await DecompileAsync("M:System.Collections.Generic.List`1.Add(`0)");

        // The reference pack strips bodies to `throw null`; a private field proves a real body.
        await Assert.That(result).Contains("reference assembly; decompiled from");
        await Assert.That(result).Contains("_size");
        await Assert.That(result).DoesNotContain("throw null");
    }

    [Test]
    public async Task PackageMemberDecompilesOnlyThatMember()
    {
        var result = await DecompileAsync(
            "M:Microsoft.CodeAnalysis.DocumentationCommentId.CreateDeclarationId(Microsoft.CodeAnalysis.ISymbol)");

        await Assert.That(result).Contains("CreateDeclarationId");
        await Assert.That(result).DoesNotContain("reference assembly");
        // A sibling member showing up would mean the whole type was dumped.
        await Assert.That(result).DoesNotContain("GetFirstSymbolForDeclarationId");
    }

    [Test]
    public async Task SourceSymbolsAreRefused()
    {
        var result = await DecompileAsync("T:CSharpLangMCP.Tools.DecompileTools");

        await Assert.That(result).StartsWith("Error:");
        await Assert.That(result).Contains("declared in source");
    }
}
