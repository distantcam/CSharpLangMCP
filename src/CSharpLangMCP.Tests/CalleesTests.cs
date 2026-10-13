using CSharpLangMCP.Tools;
using Microsoft.CodeAnalysis;

namespace CSharpLangMCP.Tests;

/// <summary>Covers <c>csharp_callees</c> against a probe class added in memory.</summary>
public class CalleesTests
{
    private const string Probe = """
        namespace CSharpLangMCP.Probe;

        internal sealed class Worker
        {
            private readonly System.Collections.Generic.List<int> _items = [];

            public int Count => _items.Count;

            public void Run()
            {
                Prepare();
                System.Func<int> f = () => Helper(1);
                _ = Count;
                _items.Add(f());
                Local();

                void Local() => Helper(2);
            }

            private static void Prepare() { }

            private static int Helper(int x) => x;
        }
        """;

    private static async Task<string> DescribeAsync(string id, bool sourceOnly = false)
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();
        var project = solution.Projects.First(p =>
            p.Name.StartsWith("CSharpLangMCP", StringComparison.Ordinal) &&
            !p.Name.Contains("Tests", StringComparison.Ordinal));

        project = project.AddDocument("CalleesProbe.cs", Probe).Project;
        var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(id, (await project.GetCompilationAsync())!)!;

        return await NavigationTools.DescribeCalleesAsync(project.Solution, symbol, sourceOnly, maxResults: 100, CancellationToken.None);
    }

    [Test]
    public async Task FindsCallsInTheBodyLambdasAndLocalFunctions()
    {
        var result = await DescribeAsync("M:CSharpLangMCP.Probe.Worker.Run");

        await Assert.That(result).Contains("Worker.Prepare()");
        await Assert.That(result).Contains("Worker.Count");
        await Assert.That(result).Contains("List<T>.Add(T item)   [System.Collections]");

        // Helper is called from the lambda and from the local function: one entry, two sites.
        await Assert.That(result.Split("Worker.Helper(int x)").Length - 1).IsEqualTo(1);
        await Assert.That(result).Contains("CalleesProbe.cs:12:36");
        await Assert.That(result).Contains("CalleesProbe.cs:17:25");

        // Entries follow the body's order.
        await Assert.That(result.IndexOf("Prepare", StringComparison.Ordinal))
            .IsLessThan(result.IndexOf("Helper", StringComparison.Ordinal));
    }

    [Test]
    public async Task SourceOnlyHidesExternalCalls()
    {
        var result = await DescribeAsync("M:CSharpLangMCP.Probe.Worker.Run", sourceOnly: true);

        await Assert.That(result).Contains("Worker.Prepare()");
        await Assert.That(result).DoesNotContain("List<T>");
    }

    [Test]
    public async Task PropertyBodiesAreWalked()
    {
        var result = await DescribeAsync("P:CSharpLangMCP.Probe.Worker.Count");

        await Assert.That(result).Contains("List<T>.Count");
    }

    [Test]
    public async Task MetadataSymbolsPointToDecompile()
    {
        var result = await DescribeAsync("M:System.String.IsNullOrEmpty(System.String)");

        await Assert.That(result).Contains("csharp_decompile");
    }
}
