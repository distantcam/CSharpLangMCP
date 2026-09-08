using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetMCP.Tests;

/// <summary>
/// Loads this repository as the workspace under test, once for the whole run.
/// </summary>
/// <remarks>
/// Dogfooding the repo keeps the fixture honest: the symbols the tests assert on are the ones being
/// shipped, so a rename that breaks an assertion is telling you something real.
/// </remarks>
public static class WorkspaceFixture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RoslynWorkspaceService? _service;
    private static Solution? _solution;

    /// <summary>Absolute path to the repository root, found by walking up to the solution file.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static async Task<(RoslynWorkspaceService Service, Solution Solution)> GetAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_service is null)
            {
                _service = new RoslynWorkspaceService(NullLogger<RoslynWorkspaceService>.Instance);
                await _service.LoadAsync(RepositoryRoot, CancellationToken.None);
                _solution = await _service.GetSolutionAsync(null, overlay: null, CancellationToken.None);
            }

            return (_service, _solution!);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (Directory.GetFiles(directory, "*.slnx").Length > 0)
                return directory;

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
