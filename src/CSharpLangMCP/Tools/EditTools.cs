using System.ComponentModel;
using System.Text;
using CSharpLangMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLangMCP.Tools;

/// <summary>Refactorings that can be previewed or written to disk.</summary>
[McpServerToolType]
public sealed class EditTools(ILogger<EditTools> logger, RoslynWorkspaceService workspace)
{
    [McpServerTool(Name = "csharp_rename")]
    [Description("Rename a symbol everywhere it is used. Previews the edits by default; pass apply=true to write them to disk.")]
    public Task<string> RenameAsync(
        [Description("Symbol id from csharp_find_symbol, or a name like 'SymbolResolver.ResolveAsync'. Omit to use filePath/line/character instead.")] string? symbol = null,
        [Description("The new name")] string newName = "",
        [Description("Absolute path to a .cs file, when locating by position")] string? filePath = null,
        [Description("0-based line number, when locating by position")] int line = 0,
        [Description("0-based character position, when locating by position")] int character = 0,
        [Description("Write the changes to disk. Default false, which only previews them.")] bool apply = false,
        [Description("Also rename matching text in comments (default false)")] bool renameInComments = false,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_rename", async ct =>
        {
            if (string.IsNullOrWhiteSpace(newName))
                return "Error: newName must not be empty.";

            var resolved = await SymbolResolver.ResolveArgumentsAsync(
                workspace, symbol, filePath, line, character, content: null, ct);

            if (resolved.Error is { } failure)
                return failure;

            var (target, solution) = (resolved.Symbol!, resolved.Solution!);

            if (!target.Locations.Any(l => l.IsInSource))
                return $"Error: {target.ToDisplayString(SymbolFormatter.Signature)} is defined in metadata and cannot be renamed.";

            var options = new SymbolRenameOptions(
                RenameOverloads: true,
                RenameInStrings: false,
                RenameInComments: renameInComments,
                RenameFile: false);

            var renamed = await Renamer.RenameSymbolAsync(solution, target, options, newName, ct);

            var sb = new StringBuilder();
            sb.AppendLine($"Rename {target.ToDisplayString(SymbolFormatter.Signature)} to '{newName}'");
            sb.AppendLine();

            return await ChangePreview.RenderAsync(
                sb, workspace, solution, renamed, apply, DescribeRenamedLine, ct,
                noEdits: "No edits required - nothing references this symbol under that name.");
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_format")]
    [Description("Format a C# file using the solution's .editorconfig settings. Previews the change by default; pass apply=true to write it.")]
    public Task<string> FormatAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Write the formatted file to disk. Default false.")] bool apply = false,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_format", async ct =>
        {
            var full = Path.GetFullPath(filePath);
            var solution = await workspace.GetSolutionAsync(full, overlay: null, ct);

            var document = RoslynWorkspaceService.FindDocument(solution, full);
            if (document is null)
                return $"Error: File is not part of the loaded workspace: {full}";

            var options = await document.GetOptionsAsync(ct);
            var formatted = await Formatter.FormatAsync(document, options, ct);

            return await ChangePreview.RenderAsync(
                new StringBuilder(), workspace, solution, formatted.Project.Solution, apply,
                DescribeWhitespaceChange, ct,
                noEdits: $"{full} is already correctly formatted.");
        }, cancellationToken);
    }

    /// <summary>A rename edit reads best as the line it lands on, before the change.</summary>
    private static string DescribeRenamedLine(SourceText text, TextChange change)
    {
        var line = ChangePreview.LineOf(text, change);
        return $"{line}: {SymbolFormatter.Truncate(text.Lines[line - 1].ToString().Trim(), 160)}";
    }

    /// <summary>Renders a whitespace-only edit readably, since the raw text is invisible.</summary>
    private static string DescribeWhitespaceChange(SourceText text, TextChange change) =>
        ChangePreview.DescribeEdit(text, change, static value =>
            value.Replace("\r\n", "\\n", StringComparison.Ordinal)
                 .Replace("\n", "\\n", StringComparison.Ordinal)
                 .Replace("\t", "\\t", StringComparison.Ordinal)
                 .Replace(" ", "·", StringComparison.Ordinal));
}
