using System.Collections.Concurrent;
using System.IO.Enumeration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace DotNetMCP.Roslyn;

/// <summary>
/// Owns the single <see cref="MSBuildWorkspace"/> and hands tools a solution snapshot that reflects
/// on-disk state, plus whatever unsaved buffer the calling tool supplied for that one call.
/// </summary>
/// <remarks>
/// Replaces the former LSP bridge. MSBuild project evaluation happens in Roslyn's out-of-process
/// build host, so no MSBuildLocator registration is required and no process is kept alive between
/// queries.
/// </remarks>
public sealed class RoslynWorkspaceService(ILogger<RoslynWorkspaceService> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Last-known write time per document, used to detect edits made outside this server.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _diskStamps = new(PathComparer);

    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _loadedPath;

    /// <summary>What the solution's shape was built from, as of the last load; see <see cref="Shape"/>.</summary>
    private Shape? _shape;

    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
            _diskStamps.Clear();
            _shape = Shape.Capture(solution, resolved);

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
    /// <param name="overlay">
    /// An unsaved buffer for one file, applied to the snapshot this call returns and to nothing else.
    /// It is deliberately not remembered: a buffer supplied once must not go on shadowing the file on
    /// disk for the rest of the session, which is what makes the cached solution below safe to keep.
    /// </param>
    public async Task<Solution> GetSolutionAsync(
        string? nearPath, (string Path, string Content)? overlay, CancellationToken ct)
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
        else if (_shape is { } shape && _loadedPath is { } loaded && !shape.IsCurrent(_solution))
        {
            // A source file came or went, or a project or build file changed. Working out which
            // project a new file belongs to means evaluating MSBuild's globs, which is a reload.
            logger.LogInformation("Project structure changed on disk; reloading {Path}", loaded);
            await LoadAsync(loaded, ct);
        }

        var solution = await SyncChangedDocumentsAsync(ct);

        if (overlay is not { } buffer)
            return solution;

        // Forked off the cached solution rather than assigned back to it, so the buffer reaches this
        // caller and no one else.
        var document = FindDocument(solution, buffer.Path);
        return document is null
            ? solution
            : solution.WithDocumentText(document.Id, SourceText.From(buffer.Content));
    }

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

    /// <summary>Re-reads documents whose on-disk timestamp moved since they were last seen.</summary>
    private async Task<Solution> SyncChangedDocumentsAsync(CancellationToken ct)
    {
        var solution = _solution!;

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not { } filePath || !File.Exists(filePath))
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
        // as though an external editor had done it. A code action that adds a file has already put
        // it in the solution, so the shape is re-captured rather than triggering a reload.
        _diskStamps.Clear();
        _shape = Shape.Capture(_solution, _loadedPath!);
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
        _shape = null;
        _diskStamps.Clear();
    }

    /// <summary>
    /// The on-disk inputs that decide which documents and options a solution has: the source files
    /// under each project directory, and the write times of the solution, project and
    /// Directory.*.props/targets files.
    /// </summary>
    /// <remarks>
    /// Compared against disk rather than against the solution's documents, so a file a project
    /// deliberately excludes is part of the baseline and does not trigger a reload on every call.
    /// </remarks>
    private sealed class Shape
    {
        private static readonly string[] BuildFileNames =
            ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

        private readonly HashSet<string> _sources;
        private readonly Dictionary<string, DateTime> _buildFiles;

        private Shape(HashSet<string> sources, Dictionary<string, DateTime> buildFiles) =>
            (_sources, _buildFiles) = (sources, buildFiles);

        public static Shape Capture(Solution solution, string loadedPath)
        {
            var buildFiles = new Dictionary<string, DateTime>(PathComparer);

            // A missing file stamps as 1601-01-01, so creating one - a new Directory.Build.props
            // above a project, say - reads as a change too.
            foreach (var path in BuildFilesOf(solution, loadedPath))
                buildFiles[path] = File.GetLastWriteTimeUtc(path);

            return new Shape(SourcesUnder(ProjectDirectories(solution)), buildFiles);
        }

        public bool IsCurrent(Solution solution) =>
            _buildFiles.All(f => File.GetLastWriteTimeUtc(f.Key) == f.Value) &&
            _sources.SetEquals(SourcesUnder(ProjectDirectories(solution)));

        private static IEnumerable<string> BuildFilesOf(Solution solution, string loadedPath)
        {
            yield return loadedPath;

            foreach (var project in solution.Projects)
            {
                if (project.FilePath is not { } projectFile)
                    continue;

                yield return projectFile;

                // MSBuild imports the nearest of each of these walking up from the project.
                for (var directory = Path.GetDirectoryName(projectFile); directory is not null; directory = Path.GetDirectoryName(directory))
                {
                    foreach (var name in BuildFileNames)
                        yield return Path.Combine(directory, name);
                }
            }
        }

        /// <summary>Project directories, minus any nested inside another, since those are walked already.</summary>
        private static List<string> ProjectDirectories(Solution solution)
        {
            var directories = solution.Projects
                .Select(p => Path.GetDirectoryName(p.FilePath))
                .OfType<string>()
                .Distinct(PathComparer)
                .OrderBy(d => d.Length)
                .ToList();

            return directories
                .Where(d => !directories.Any(outer =>
                    outer.Length < d.Length &&
                    d.StartsWith(outer.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison)))
                .ToList();
        }

        private static HashSet<string> SourcesUnder(IEnumerable<string> directories)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var sources = new HashSet<string>(PathComparer);

            foreach (var directory in directories.Where(Directory.Exists))
            {
                var files = new FileSystemEnumerable<string>(directory, (ref entry) => entry.ToFullPath(), options)
                {
                    ShouldIncludePredicate = (ref entry) =>
                        !entry.IsDirectory && entry.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),

                    // Build output churns on every build and is never hand-written source; dot
                    // directories are .git, .vs and the like.
                    ShouldRecursePredicate = (ref entry) =>
                        entry.FileName is not ("bin" or "obj") && !entry.FileName.StartsWith('.'),
                };

                sources.UnionWith(files);
            }

            return sources;
        }
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
