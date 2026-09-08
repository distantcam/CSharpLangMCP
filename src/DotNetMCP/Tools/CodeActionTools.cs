using System.ComponentModel;
using System.Text;
using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotNetMCP.Tools;

/// <summary>Roslyn's quick fixes and refactorings, listed and applied.</summary>
[McpServerToolType]
public sealed class CodeActionTools(
    ILogger<CodeActionTools> logger,
    RoslynWorkspaceService workspace,
    CodeActionService codeActions,
    AnalyzerService analyzers)
{
    [McpServerTool(Name = "csharp_code_actions")]
    [Description("List the quick fixes and refactorings available at a position or over a line range - the same actions an IDE lightbulb offers. Apply one with csharp_apply_code_action.")]
    public Task<string> CodeActionsAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based start line")] int startLine,
        [Description("0-based end line. Omit to use just the start line.")] int? endLine = null,
        [Description("0-based start character. Defaults to the start of the line.")] int? startCharacter = null,
        [Description("0-based end character. Defaults to the end of the end line.")] int? endCharacter = null,
        [Description("Include quick fixes for diagnostics (default true)")] bool includeFixes = true,
        [Description("Include refactorings (default true). These are the slower half of the call.")] bool includeRefactorings = true,
        [Description("Also consider the project's analyzer diagnostics, which is what makes the IDExxxx fixes (add braces, use expression body, remove unnecessary usings) available. Off by default because running the analyzers costs a second or two. CAxxxx diagnostics are reported by csharp_diagnostics but their fixers are not bundled, so no fix appears for them here.")] bool includeAnalyzers = false,
        [Description("Unsaved file content (reads from disk if omitted)")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_code_actions", async ct =>
        {
            var located = await LocateAsync(filePath, startLine, endLine, startCharacter, endCharacter, includeAnalyzers, content, ct);
            if (located.Error is { } error)
                return error;

            var (document, span, diagnostics) = (located.Document!, located.Span, located.Diagnostics!);

            var actions = await codeActions.GetActionsAsync(
                document, span, diagnostics, includeFixes, includeRefactorings, ct);

            var text = await document.GetTextAsync(ct);
            var lines = text.Lines.GetLinePositionSpan(span);

            var sb = new StringBuilder();
            sb.AppendLine($"{document.FilePath}  lines {lines.Start.Line + 1}-{lines.End.Line + 1}");
            sb.AppendLine();

            if (actions.Count == 0)
            {
                sb.AppendLine("No code actions available here.");
                if (!includeRefactorings)
                    sb.AppendLine("Refactorings were excluded - re-run with includeRefactorings=true to see more.");
                if (!includeAnalyzers)
                    sb.AppendLine("Analyzer diagnostics were not considered - re-run with includeAnalyzers=true to see their fixes.");
                return sb.ToString();
            }

            sb.AppendLine($"{actions.Count} code action(s). Apply one by passing its exact title to csharp_apply_code_action:");

            foreach (var group in actions.GroupBy(a => a.Kind))
            {
                sb.AppendLine();
                sb.AppendLine(group.Key == CodeActionKind.Fix ? "Quick fixes:" : "Refactorings:");

                foreach (var action in group)
                {
                    var origin = action.DiagnosticId is null
                        ? $"[{action.Provider}]"
                        : $"[{action.DiagnosticId}, {action.Provider}]";

                    sb.AppendLine($"  - {action.Title}   {origin}");
                }
            }

            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_apply_code_action")]
    [Description("Apply a code action offered by csharp_code_actions, identified by its exact title. Previews the edits by default; pass apply=true to write them.")]
    public Task<string> ApplyCodeActionAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Exact title as listed by csharp_code_actions")] string title,
        [Description("0-based start line - must match the csharp_code_actions call")] int startLine = 0,
        [Description("0-based end line. Omit to use just the start line.")] int? endLine = null,
        [Description("0-based start character. Defaults to the start of the line.")] int? startCharacter = null,
        [Description("0-based end character. Defaults to the end of the end line.")] int? endCharacter = null,
        [Description("Write the changes to disk. Default false, which only previews them.")] bool apply = false,
        [Description("Must match the csharp_code_actions call. Required to apply a fix for an analyzer diagnostic (CAxxxx, IDExxxx).")] bool includeAnalyzers = false,
        [Description("Unsaved file content (reads from disk if omitted)")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_apply_code_action", async ct =>
        {
            if (string.IsNullOrWhiteSpace(title))
                return "Error: title must not be empty.";

            var located = await LocateAsync(filePath, startLine, endLine, startCharacter, endCharacter, includeAnalyzers, content, ct);
            if (located.Error is { } error)
                return error;

            var (document, span, diagnostics) = (located.Document!, located.Span, located.Diagnostics!);

            var actions = await codeActions.GetActionsAsync(
                document, span, diagnostics, includeFixes: true, includeRefactorings: true, ct);

            var chosen = actions.FirstOrDefault(a => string.Equals(a.Title, title, StringComparison.Ordinal))
                ?? actions.FirstOrDefault(a => string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase));

            if (chosen is null)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"No code action titled '{title}' is available at this location.");

                if (actions.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Available here:");
                    foreach (var action in actions)
                        sb.AppendLine($"  - {action.Title}");
                }

                return sb.ToString();
            }

            var changed = await CodeActionService.GetChangedSolutionAsync(chosen.Action, ct);
            if (changed is null)
                return $"'{chosen.Title}' produced no file edits - it is not something this server can apply.";

            var sb2 = new StringBuilder();
            sb2.AppendLine($"{chosen.Title}");
            sb2.AppendLine();

            return await ChangePreview.RenderAsync(
                sb2, workspace, document.Project.Solution, changed, apply, DescribeEdit, ct);
        }, cancellationToken);
    }

    /// <summary>
    /// Resolves the file and line range to a document, a span, and the diagnostics on that document.
    /// </summary>
    /// <remarks>
    /// Diagnostics are computed fresh rather than read from a cache: a fix offered against a stale
    /// diagnostic either fails to apply or silently does the wrong thing.
    /// </remarks>
    private async Task<(Document? Document, TextSpan Span, IReadOnlyList<Diagnostic>? Diagnostics, string? Error)> LocateAsync(
        string filePath, int startLine, int? endLine, int? startCharacter, int? endCharacter,
        bool includeAnalyzers, string? content, CancellationToken ct)
    {
        var full = Path.GetFullPath(filePath);
        var solution = await workspace.GetSolutionAsync(
            full, content is null ? null : (full, content), ct);

        var document = RoslynWorkspaceService.FindDocument(solution, full);
        if (document is null)
            return (null, default, null, $"Error: File is not part of the loaded workspace: {full}");

        var text = await document.GetTextAsync(ct);
        var lastLine = endLine ?? startLine;

        if (!SymbolResolver.TryGetPosition(text, startLine, startCharacter ?? 0, out var start, out var startError))
            return (null, default, null, $"Error: {startError}");

        int end;
        if (endCharacter is { } explicitEnd)
        {
            if (!SymbolResolver.TryGetPosition(text, lastLine, explicitEnd, out end, out var endError))
                return (null, default, null, $"Error: {endError}");
        }
        else
        {
            if (lastLine >= text.Lines.Count)
                return (null, default, null, $"Error: Line {lastLine + 1} is past the end of the file ({text.Lines.Count} line(s)).");

            end = text.Lines[lastLine].End;
        }

        if (end < start)
            return (null, default, null, "Error: The end of the range is before its start.");

        var model = await document.GetSemanticModelAsync(ct);
        if (model is null)
            return (null, default, null, $"Error: Could not build a semantic model for {full}");

        var diagnostics = new List<Diagnostic>(model.GetDiagnostics(cancellationToken: ct));

        // Without these, no CA or IDE diagnostic is ever fixable here, so the whole analyzer half of
        // the lightbulb - "remove unnecessary usings", "make field readonly" - stays out of reach.
        if (includeAnalyzers)
            diagnostics.AddRange(await analyzers.GetFileDiagnosticsAsync(document, ct));

        return (document, TextSpan.FromBounds(start, end), diagnostics, null);
    }

    /// <summary>Code edits are ordinary source, so they read best flattened to one quoted line.</summary>
    private static string DescribeEdit(SourceText text, TextChange change) =>
        ChangePreview.DescribeEdit(text, change, static value =>
        {
            var flattened = value.ReplaceLineEndings(" ").Trim();
            return flattened.Length == 0 ? "" : $"\"{SymbolFormatter.Truncate(flattened, 100)}\"";
        });
}
