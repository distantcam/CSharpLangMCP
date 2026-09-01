using System.ComponentModel;
using System.Text;
using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotNetMCP.Tools;

/// <summary>Refactorings that can be previewed or written to disk.</summary>
[McpServerToolType]
public sealed class EditTools(ILogger<EditTools> logger, RoslynWorkspaceService workspace)
{
    [McpServerTool(Name = "csharp_rename")]
    [Description("Rename a symbol everywhere it is used. Previews the edits by default; pass apply=true to write them to disk.")]
    public Task<string> RenameAsync(
        [Description("Symbol id from csharp_find_symbol, or a name like 'LspClient.StartAsync'. Omit to use filePath/line/character instead.")] string? symbol = null,
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

            if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(filePath))
                return "Error: Provide either a symbol id or name, or a filePath with line and character.";

            var solution = await workspace.GetSolutionAsync(filePath, ct);

            var lookup = string.IsNullOrWhiteSpace(symbol)
                ? await SymbolResolver.ResolveAtPositionAsync(solution, filePath!, line, character, ct)
                : await SymbolResolver.ResolveAsync(solution, symbol, ct);

            if (SymbolFormatter.FormatFailure(lookup) is { } failure)
                return failure;

            var target = ((SymbolLookup.Found)lookup).Symbol;

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

            var totalEdits = 0;
            var changedFiles = 0;

            foreach (var projectChange in renamed.GetChanges(solution).GetProjectChanges())
            {
                foreach (var documentId in projectChange.GetChangedDocuments())
                {
                    var original = solution.GetDocument(documentId);
                    var updated = renamed.GetDocument(documentId);
                    if (original is null || updated is null)
                        continue;

                    var changes = (await updated.GetTextChangesAsync(original, ct)).ToArray();
                    if (changes.Length == 0)
                        continue;

                    changedFiles++;
                    totalEdits += changes.Length;

                    sb.AppendLine($"{original.FilePath ?? original.Name}: {changes.Length} edit(s)");

                    var text = await original.GetTextAsync(ct);
                    foreach (var change in changes.Take(20))
                    {
                        var position = text.Lines.GetLinePosition(change.Span.Start);
                        var before = text.Lines[position.Line].ToString().Trim();
                        sb.AppendLine($"  {position.Line + 1}: {SymbolFormatter.Truncate(before, 160)}");
                    }

                    if (changes.Length > 20)
                        sb.AppendLine($"  ... and {changes.Length - 20} more in this file");
                }
            }

            if (totalEdits == 0)
                return sb.Append("No edits required - nothing references this symbol under that name.").ToString();

            sb.AppendLine();
            sb.AppendLine($"Total: {totalEdits} edit(s) across {changedFiles} file(s).");

            if (!apply)
            {
                sb.AppendLine("Nothing was written. Re-run with apply=true to make these changes.");
                return sb.ToString();
            }

            if (!workspace.TryApplyChanges(renamed))
                return sb.Append("\nError: The workspace refused the changes; nothing was written.").ToString();

            sb.AppendLine("Applied - the files above have been written to disk.");
            return sb.ToString();
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
            var solution = await workspace.GetSolutionAsync(full, ct);

            var document = RoslynWorkspaceService.FindDocument(solution, full);
            if (document is null)
                return $"Error: File is not part of the loaded workspace: {full}";

            var options = await document.GetOptionsAsync(ct);
            var formatted = await Formatter.FormatAsync(document, options, ct);

            var changes = (await formatted.GetTextChangesAsync(document, ct)).ToArray();
            if (changes.Length == 0)
                return $"{full} is already correctly formatted.";

            var sb = new StringBuilder();
            sb.AppendLine($"{full}: {changes.Length} formatting change(s)");
            sb.AppendLine();

            var text = await document.GetTextAsync(ct);
            foreach (var change in changes.Take(30))
            {
                var position = text.Lines.GetLinePosition(change.Span.Start);
                sb.AppendLine($"  line {position.Line + 1}: {DescribeChange(text, change)}");
            }

            if (changes.Length > 30)
                sb.AppendLine($"  ... and {changes.Length - 30} more");

            sb.AppendLine();

            if (!apply)
            {
                sb.AppendLine("Nothing was written. Re-run with apply=true to format the file.");
                return sb.ToString();
            }

            if (!workspace.TryApplyChanges(formatted.Project.Solution))
                return sb.Append("Error: The workspace refused the changes; nothing was written.").ToString();

            sb.AppendLine("Applied - the file has been written to disk.");
            return sb.ToString();
        }, cancellationToken);
    }

    /// <summary>Renders a whitespace-only edit readably, since the raw text is invisible.</summary>
    private static string DescribeChange(SourceText text, TextChange change)
    {
        var removed = text.ToString(change.Span);
        var added = change.NewText ?? "";

        return (Visible(removed), Visible(added)) switch
        {
            ("", var to) => $"insert {to}",
            (var from, "") => $"remove {from}",
            var (from, to) => $"{from} -> {to}",
        };

        static string Visible(string value) =>
            value.Replace("\r\n", "\\n", StringComparison.Ordinal)
                 .Replace("\n", "\\n", StringComparison.Ordinal)
                 .Replace("\t", "\\t", StringComparison.Ordinal)
                 .Replace(" ", "·", StringComparison.Ordinal);
    }
}
