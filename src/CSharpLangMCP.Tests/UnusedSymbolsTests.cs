using CSharpLangMCP.Tools;

namespace CSharpLangMCP.Tests;

/// <summary>Covers <c>csharp_unused_symbols</c> against a probe file added in memory.</summary>
public class UnusedSymbolsTests
{
    private const string Probe = """
        namespace CSharpLangMCP.Probe;

        internal sealed class Used
        {
            private int _unusedField;

            private Used() { }

            public static void Run(IThing thing)
            {
                Helper();
                thing.Do();
            }

            private static void Helper() { }

            private static void Orphan() { }

            private static void Recurse() => Recurse();

            [System.Obsolete]
            private static void Attributed() { }
        }

        internal interface IThing
        {
            void Do();
        }

        internal sealed class Thing : IThing
        {
            public void Do() { }
        }

        internal static class Caller
        {
            internal static void Go() => Used.Run(new Thing());
        }

        internal sealed class Dead
        {
            public void Inside() { }
        }

        internal sealed record Pair(int Left, int Right);
        """;

    private static async Task<string> DescribeAsync()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();
        var project = solution.Projects.First(p =>
            p.Name.StartsWith("CSharpLangMCP", StringComparison.Ordinal) &&
            !p.Name.Contains("Tests", StringComparison.Ordinal));

        var path = Path.Combine(Path.GetDirectoryName(project.FilePath)!, "UnusedProbe.cs");
        var document = project.AddDocument("UnusedProbe.cs", Probe, filePath: path);

        return await NavigationTools.DescribeUnusedAsync(
            document.Project.Solution, path, includePublic: false, maxResults: 100, CancellationToken.None);
    }

    [Test]
    public async Task ReportsUnreferencedSymbols()
    {
        var result = await DescribeAsync();

        await Assert.That(result).Contains("Used._unusedField");
        await Assert.That(result).Contains("Used.Orphan()");
        // Calling itself does not keep a method alive.
        await Assert.That(result).Contains("Used.Recurse()");

        // Unused types are reported once, without their members.
        await Assert.That(result).Contains("NamedType CSharpLangMCP.Probe.Dead");
        await Assert.That(result).DoesNotContain("Dead.Inside()");
        await Assert.That(result).Contains("NamedType CSharpLangMCP.Probe.Caller");
        await Assert.That(result).DoesNotContain("Caller.Go()");
        await Assert.That(result).Contains("CSharpLangMCP.Probe.Pair");
    }

    [Test]
    public async Task SkipsWhatIsReachedWithoutADirectReference()
    {
        var result = await DescribeAsync();

        await Assert.That(result).DoesNotContain("Used.Helper()");
        await Assert.That(result).DoesNotContain("Used.Run(");
        await Assert.That(result).DoesNotContain("Used.Used()");
        await Assert.That(result).DoesNotContain("Used.Attributed()");
        await Assert.That(result).DoesNotContain("Thing.Do()");
        await Assert.That(result).DoesNotContain("Pair.Left");
    }
}
