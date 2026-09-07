using System.Text;
using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMCP.Tools;

/// <summary>
/// Renders the edits between two solutions, then previews or writes them.
/// </summary>
/// <remarks>
/// Every tool that changes code - rename, format, apply code action - shows the same thing and
/// answers to the same <c>apply</c> flag, so the walk, the totals and the four possible endings
/// live here once. Only the per-edit line differs, which is what <c>describe</c> is for.
/// </remarks>
internal static class ChangePreview
{
    /// <summary>Edits listed per file before the rest are summarised as a count.</summary>
    private const int MaxPerFile = 20;

    public static async Task<string> RenderAsync(
        StringBuilder sb,
        RoslynWorkspaceService workspace,
        Solution original,
        Solution changed,
        bool apply,
        Func<SourceText, TextChange, string> describe,
        CancellationToken ct,
        string noEdits = "No edits were produced.")
    {
        var edits = 0;
        var files = 0;

        foreach (var projectChange in changed.GetChanges(original).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var before = original.GetDocument(documentId);
                var after = changed.GetDocument(documentId);
                if (before is null || after is null)
                    continue;

                var textChanges = (await after.GetTextChangesAsync(before, ct)).ToArray();
                if (textChanges.Length == 0)
                    continue;

                files++;
                edits += textChanges.Length;

                sb.AppendLine($"{before.FilePath ?? before.Name}: {textChanges.Length} edit(s)");

                var text = await before.GetTextAsync(ct);
                foreach (var change in textChanges.Take(MaxPerFile))
                    sb.AppendLine($"  {describe(text, change)}");

                if (textChanges.Length > MaxPerFile)
                    sb.AppendLine($"  ... and {textChanges.Length - MaxPerFile} more in this file");
            }
        }

        if (edits == 0)
            return sb.Append(noEdits).ToString();

        sb.AppendLine();
        sb.AppendLine($"Total: {edits} edit(s) across {files} file(s).");

        if (!apply)
        {
            sb.AppendLine("Nothing was written. Re-run with apply=true to make these changes.");
            return sb.ToString();
        }

        if (!workspace.TryApplyChanges(changed))
            return sb.Append("Error: The workspace refused the changes; nothing was written.").ToString();

        sb.AppendLine("Applied - the files above have been written to disk.");
        return sb.ToString();
    }

    /// <summary>The 1-based line an edit starts on.</summary>
    public static int LineOf(SourceText text, TextChange change) =>
        text.Lines.GetLinePosition(change.Span.Start).Line + 1;

    /// <summary>
    /// Renders one edit as an insertion, a removal, or a replacement, with both halves put through
    /// <paramref name="render"/> - which is where a caller decides how to make its text readable.
    /// </summary>
    public static string DescribeEdit(SourceText text, TextChange change, Func<string, string> render)
    {
        var described = (render(text.ToString(change.Span)), render(change.NewText ?? "")) switch
        {
            ("", var to) => $"insert {to}",
            (var from, "") => $"remove {from}",
            var (from, to) => $"{from} -> {to}",
        };

        return $"line {LineOf(text, change)}: {described}";
    }
}
