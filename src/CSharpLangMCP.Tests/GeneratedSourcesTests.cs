using CSharpLangMCP.Tools;

namespace CSharpLangMCP.Tests;

/// <summary>
/// Covers <c>csharp_generated_sources</c> by feeding the server project a <c>[GeneratedRegex]</c>
/// probe, which the SDK's regex generator - referenced by every net10 project - expands in memory.
/// </summary>
public class GeneratedSourcesTests
{
    private const string Probe = """
        namespace CSharpLangMCP.Probe;

        internal static partial class RegexProbe
        {
            [System.Text.RegularExpressions.GeneratedRegex("probe[0-9]+")]
            internal static partial System.Text.RegularExpressions.Regex Probe();
        }
        """;

    private static async Task<Microsoft.CodeAnalysis.Project> ProbeProjectAsync()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();
        var project = solution.Projects.First(p =>
            p.Name.StartsWith("CSharpLangMCP", StringComparison.Ordinal) &&
            !p.Name.Contains("Tests", StringComparison.Ordinal));

        return project.AddDocument("RegexProbe.cs", Probe).Project;
    }

    [Test]
    public async Task ListsGeneratorOutputWithItsGenerator()
    {
        var result = await StructureTools.DescribeGeneratedSourcesAsync(
            [await ProbeProjectAsync()], hintName: null, CancellationToken.None);

        await Assert.That(result).Contains("RegexGenerator.g.cs");
        await Assert.That(result).Contains("lines)");
    }

    [Test]
    public async Task ReadsTheMatchingFile()
    {
        var result = await StructureTools.DescribeGeneratedSourcesAsync(
            [await ProbeProjectAsync()], hintName: "RegexGenerator.g.cs", CancellationToken.None);

        // The generated body carries the pattern, which only the generator could have put there.
        await Assert.That(result).Contains("probe[0-9]+");
    }

    [Test]
    public async Task SaysSoWhenNothingMatches()
    {
        var result = await StructureTools.DescribeGeneratedSourcesAsync(
            [await ProbeProjectAsync()], hintName: "NoSuchFile.g.cs", CancellationToken.None);

        await Assert.That(result).StartsWith("No generated file matches");
    }
}
