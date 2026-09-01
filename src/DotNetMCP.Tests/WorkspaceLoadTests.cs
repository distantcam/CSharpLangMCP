using DotNetMCP.Roslyn;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetMCP.Tests;

public class WorkspaceLoadTests
{
    [Test]
    public async Task LoadsThisRepositoryFromItsSlnx()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var names = solution.Projects.Select(p => p.Name).ToArray();
        await Assert.That(names).Contains("DotNetMCP");
    }

    [Test]
    public async Task CompilesWithoutErrors()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var project = solution.Projects.Single(p => p.Name == "DotNetMCP");
        var compilation = await project.GetCompilationAsync();
        await Assert.That(compilation).IsNotNull();

        var errors = compilation!.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task FindsADocumentByPathRegardlessOfCase()
    {
        var (_, solution) = await WorkspaceFixture.GetAsync();

        var path = Path.Combine(WorkspaceFixture.RepositoryRoot, "src", "DotNetMCP", "Program.cs");

        await Assert.That(RoslynWorkspaceService.FindDocument(solution, path)).IsNotNull();
        await Assert.That(RoslynWorkspaceService.FindDocument(solution, path.ToUpperInvariant())).IsNotNull();
    }

    [Test]
    public async Task ResolvesADirectoryToItsSolutionFile()
    {
        var resolved = RoslynWorkspaceService.ResolveWorkspacePath(WorkspaceFixture.RepositoryRoot);

        await Assert.That(Path.GetExtension(resolved)).IsEqualTo(".slnx");
    }

    [Test]
    public async Task AcceptsASolutionFilePathDirectly()
    {
        var slnx = Directory.GetFiles(WorkspaceFixture.RepositoryRoot, "*.slnx").Single();

        var resolved = RoslynWorkspaceService.ResolveWorkspacePath(slnx);

        await Assert.That(resolved).IsEqualTo(slnx);
    }

    [Test]
    public async Task RejectsAPathThatIsNotASolutionOrProject()
    {
        var readme = Path.Combine(WorkspaceFixture.RepositoryRoot, "README.md");

        await Assert.That(() => RoslynWorkspaceService.ResolveWorkspacePath(readme))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task RejectsAPathThatDoesNotExist()
    {
        await Assert.That(() => RoslynWorkspaceService.ResolveWorkspacePath(@"Z:\definitely\not\here"))
            .Throws<DirectoryNotFoundException>();
    }

    [Test]
    public async Task ReportsUnsupportedProjectsAsWarningsRatherThanFailing()
    {
        // The case the old SolutionFilter existed to pre-empt: a solution carrying a project type
        // Roslyn cannot open should still load everything else.
        var root = Path.Combine(Path.GetTempPath(), "dotnetmcp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "Setup"));

        await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(root, "App", "Thing.cs"), "namespace App; public class Thing { }");
        await File.WriteAllTextAsync(Path.Combine(root, "Setup", "Setup.wixproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(root, "Fixture.sln"), SolutionWithWixProject);

        var service = new RoslynWorkspaceService(NullLogger<RoslynWorkspaceService>.Instance);
        try
        {
            var result = await service.LoadAsync(root, CancellationToken.None);

            await Assert.That(result.Projects.Select(p => p.Name)).Contains("App");
            await Assert.That(result.Failures).IsNotEmpty();
            await Assert.That(string.Join("\n", result.Failures)).Contains("wixproj");
        }
        finally
        {
            service.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private const string SolutionWithWixProject = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{930C7802-8A8C-48F9-8165-68863BCCD9DD}") = "Setup", "Setup\Setup.wixproj", "{22222222-2222-2222-2222-222222222222}"
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        	EndGlobalSection
        EndGlobal
        """;
}
