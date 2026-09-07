using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace DotNetMCP.Roslyn;

/// <summary>
/// Owns the single <see cref="MSBuildWorkspace"/> and hands tools a solution snapshot that reflects
/// both on-disk state and any caller-supplied unsaved buffers.
/// </summary>
/// <remarks>
/// Replaces the former LSP bridge. MSBuild project evaluation happens in Roslyn's out-of-process
/// build host, so no MSBuildLocator registration is required and no process is kept alive between
/// queries.
/// </remarks>
public sealed class RoslynWorkspaceService(ILogger<RoslynWorkspaceService> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Caller-supplied buffer contents, keyed by full path, overriding what is on disk.</summary>
    private readonly ConcurrentDictionary<string, string> _overlays = new(PathComparer);

    /// <summary>Last-known write time per document, used to detect edits made outside this server.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _diskStamps = new(PathComparer);

    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _loadedPath;

    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public bool IsLoaded => _solution is not null;

    public string? LoadedPath => _loadedPath;

    /// <summary>
    /// Opens a solution or project. The path may be a directory, or a .sln, .slnx, .slnf or .csproj
    /// file; a directory is probed for one of those.
    /// </summary>
    public async Task<WorkspaceLoadResult> LoadAsync(string path, CancellationToken ct)
    {
        var resolved = ResolveWorkspacePath(path);
        var start = System.Diagnostics.Stopwatch.StartNew();

        await _gate.WaitAsync(ct);
        try
        {
            DisposeWorkspaceCore();

            var failures = new ConcurrentQueue<string>();
            var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                logger.LogWarning("Workspace {Kind}: {Message}", e.Diagnostic.Kind, e.Diagnostic.Message);
                failures.Enqueue($"[{e.Diagnostic.Kind}] {e.Diagnostic.Message}");
            });

            logger.LogInformation("Loading workspace {Path}", resolved);

            var solution = IsProjectFile(resolved)
                ? (await workspace.OpenProjectAsync(resolved, cancellationToken: ct)).Solution
                : await workspace.OpenSolutionAsync(resolved, cancellationToken: ct);

            _workspace = workspace;
            _solution = solution;
            _loadedPath = resolved;
            _overlays.Clear();
            _diskStamps.Clear();

            var projects = solution.Projects
                .Select(p => new ProjectSummary(p.Name, p.Documents.Count(), TargetFrameworkOf(p)))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();

            logger.LogInformation("Loaded {Count} project(s) in {Ms} ms", projects.Length, start.ElapsedMilliseconds);

            return new WorkspaceLoadResult(resolved, projects, failures.ToArray(), start.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the current solution, loading one if necessary, and refreshes any document whose file
    /// changed on disk since it was last read.
    /// </summary>
    /// <param name="nearPath">
    /// A file used to discover a workspace when none has been loaded yet; walked upwards looking for
    /// a solution or project file.
    /// </param>
    public async Task<Solution> GetSolutionAsync(string? nearPath, CancellationToken ct)
    {
        if (_solution is null)
        {
            // Walk up from the file in hand, and failing that from the working directory the server
            // was launched in, which is usually the project the client is working on.
            var discovered = (nearPath is null ? null : DiscoverWorkspaceFrom(nearPath))
                ?? DiscoverWorkspaceFrom(Directory.GetCurrentDirectory());

            if (discovered is null)
            {
                throw new InvalidOperationException(
                    "No workspace loaded. Call csharp_load_workspace with a solution, project or directory path first.");
            }

            logger.LogInformation("No workspace loaded; discovered {Path}", discovered);
            await LoadAsync(discovered, ct);
        }

        return await SyncChangedDocumentsAsync(ct);
    }

    /// <summary>Records an unsaved buffer for a file, replacing what is on disk until reload.</summary>
    public void SetOverlay(string filePath, string content) => _overlays[filePath] = content;

    /// <summary>Finds the document for a path, tolerating case differences on Windows.</summary>
    public static Document? FindDocument(Solution solution, string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var ids = solution.GetDocumentIdsWithFilePath(full);
        if (ids.Length > 0)
            return solution.GetDocument(ids[0]);

        // GetDocumentIdsWithFilePath matches ordinally; fall back to a scan for case differences.
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath is not null && PathComparer.Equals(d.FilePath, full));
    }

    /// <summary>Re-reads documents whose on-disk timestamp moved, then applies caller overlays on top.</summary>
    private async Task<Solution> SyncChangedDocumentsAsync(CancellationToken ct)
    {
        var solution = _solution!;

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not { } filePath)
                    continue;

                if (_overlays.TryGetValue(filePath, out var overlay))
                {
                    var current = await document.GetTextAsync(ct);
                    if (!string.Equals(current.ToString(), overlay, StringComparison.Ordinal))
                        solution = solution.WithDocumentText(document.Id, SourceText.From(overlay));
                    continue;
                }

                if (!File.Exists(filePath))
                    continue;

                var stamp = File.GetLastWriteTimeUtc(filePath);
                if (_diskStamps.TryGetValue(filePath, out var known) && known == stamp)
                    continue;

                _diskStamps[filePath] = stamp;

                // A first sighting only records the stamp; the loaded text is already current.
                if (known == default)
                    continue;

                var text = SourceText.From(await File.ReadAllTextAsync(filePath, ct));
                solution = solution.WithDocumentText(document.Id, text);
                logger.LogDebug("Refreshed {Path} from disk", filePath);
            }
        }

        _solution = solution;
        return solution;
    }

    /// <summary>Applies a changed solution to the workspace, writing the affected files to disk.</summary>
    public bool TryApplyChanges(Solution changed)
    {
        if (_workspace is null)
            return false;

        if (!_workspace.TryApplyChanges(changed))
            return false;

        _solution = _workspace.CurrentSolution;

        // Applied edits move the files under us; drop stamps so the next sync does not re-read them
        // as though an external editor had done it.
        _diskStamps.Clear();
        return true;
    }

    public void Unload()
    {
        _gate.Wait();
        try
        {
            DisposeWorkspaceCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DisposeWorkspaceCore()
    {
        if (_workspace is not null)
        {
            _workspace.CloseSolution();
            _workspace.Dispose();
            logger.LogInformation("Workspace unloaded");
        }

        _workspace = null;
        _solution = null;
        _loadedPath = null;
        _overlays.Clear();
        _diskStamps.Clear();
    }

    private static string? TargetFrameworkOf(Project project)
    {
        // Multi-targeted projects surface one Roslyn project per TFM, named like "Foo(net10.0)".
        var name = project.Name;
        var open = name.LastIndexOf('(');
        return open >= 0 && name.EndsWith(')') ? name[(open + 1)..^1] : null;
    }

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] WorkspaceExtensions = [".slnx", ".sln", ".slnf", ".csproj"];

    /// <summary>Turns a user-supplied path into a concrete solution or project file.</summary>
    internal static string ResolveWorkspacePath(string path)
    {
        var full = Path.GetFullPath(path);

        if (File.Exists(full))
        {
            if (!WorkspaceExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Not a solution or project file: {full}. Expected one of {string.Join(", ", WorkspaceExtensions)}.",
                    nameof(path));
            }

            return full;
        }

        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Path does not exist: {full}");

        return FindWorkspaceFileIn(full)
            ?? throw new FileNotFoundException($"No .slnx, .sln, .slnf or .csproj found in {full}.");
    }

    private static string? FindWorkspaceFileIn(string directory)
    {
        foreach (var extension in WorkspaceExtensions)
        {
            var matches = Directory.GetFiles(directory, "*" + extension);
            if (matches.Length > 0)
            {
                Array.Sort(matches, StringComparer.Ordinal);
                return matches[0];
            }
        }

        return null;
    }

    /// <summary>Walks up from a file or directory looking for a solution or project to open.</summary>
    private static string? DiscoverWorkspaceFrom(string startPath)
    {
        var full = Path.GetFullPath(startPath);
        var directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);

        while (directory is not null)
        {
            if (FindWorkspaceFileIn(directory) is { } found)
                return found;

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    public void Dispose()
    {
        DisposeWorkspaceCore();
        _gate.Dispose();
    }
}
