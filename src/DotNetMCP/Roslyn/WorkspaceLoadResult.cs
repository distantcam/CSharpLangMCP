namespace DotNetMCP.Roslyn;

/// <summary>
/// Outcome of a workspace load: what opened, and what MSBuild complained about on the way.
/// </summary>
/// <remarks>
/// Failures are collected rather than thrown. MSBuild reports project types Roslyn cannot open
/// (<c>.wixproj</c>, <c>.vcxproj</c>, ...) as workspace diagnostics and simply skips them, which is
/// why this server no longer needs to pre-filter the solution file.
/// </remarks>
public sealed record WorkspaceLoadResult(
    string LoadedPath,
    IReadOnlyList<ProjectSummary> Projects,
    IReadOnlyList<string> Failures,
    TimeSpan Duration);

public sealed record ProjectSummary(string Name, int DocumentCount, string? TargetFramework);
