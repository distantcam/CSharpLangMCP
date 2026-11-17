using CSharpLangMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSharpLangMCP.Tests;

/// <summary>
/// Covers reloading when a project's shape changes on disk. Runs against a throwaway project of
/// its own, since adding and deleting files in this repository would disturb the shared fixture.
/// </summary>
public class WorkspaceShapeTests
{
    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <DefineConstants>$(DefineConstants){0}</DefineConstants>
          </PropertyGroup>
          <ItemGroup>
            <Compile Remove="Excluded.cs" />
          </ItemGroup>
        </Project>
        """;

    [Test]
    public async Task PicksUpAddedAndDeletedFilesAndProjectEdits()
    {
        var directory = Directory.CreateTempSubdirectory("csharplangmcp-shape-").FullName;
        using var service = new RoslynWorkspaceService(NullLogger<RoslynWorkspaceService>.Instance);

        try
        {
            var projectFile = Path.Combine(directory, "Probe.csproj");
            await File.WriteAllTextAsync(projectFile, string.Format(ProjectXml, ""));
            await File.WriteAllTextAsync(Path.Combine(directory, "A.cs"), "class A;");
            await File.WriteAllTextAsync(Path.Combine(directory, "Excluded.cs"), "class Excluded;");

            await service.LoadAsync(projectFile, CancellationToken.None);
            await Assert.That(await DocumentNamesAsync(service)).IsEquivalentTo(["A.cs"]);

            // An excluded file is in the baseline, so it must not read as a change on every call.
            var unchanged = await service.GetSolutionAsync(null, overlay: null, CancellationToken.None);
            await Assert.That(await service.GetSolutionAsync(null, overlay: null, CancellationToken.None))
                .IsSameReferenceAs(unchanged);

            await File.WriteAllTextAsync(Path.Combine(directory, "B.cs"), "class B;");
            await Assert.That(await DocumentNamesAsync(service)).IsEquivalentTo(["A.cs", "B.cs"]);

            File.Delete(Path.Combine(directory, "A.cs"));
            await Assert.That(await DocumentNamesAsync(service)).IsEquivalentTo(["B.cs"]);

            await File.WriteAllTextAsync(projectFile, string.Format(ProjectXml, ";PROBE_SYMBOL"));
            File.SetLastWriteTimeUtc(projectFile, DateTime.UtcNow.AddSeconds(5));

            var solution = await service.GetSolutionAsync(null, overlay: null, CancellationToken.None);
            var symbols = solution.Projects.Single().ParseOptions!.PreprocessorSymbolNames;
            await Assert.That(symbols).Contains("PROBE_SYMBOL");
        }
        finally
        {
            service.Unload();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string[]> DocumentNamesAsync(RoslynWorkspaceService service)
    {
        var solution = await service.GetSolutionAsync(null, overlay: null, CancellationToken.None);

        // Evaluation adds generated files under obj (global usings, assembly info); only the
        // hand-written ones are under test.
        return
        [
            .. solution.Projects.Single().Documents
                .Where(d => d.Folders.Count == 0 && !d.Name.Contains(".g.", StringComparison.Ordinal)
                    && !d.Name.Contains("AssemblyInfo", StringComparison.Ordinal))
                .Select(d => d.Name)
                .Order(StringComparer.Ordinal)
        ];
    }
}
