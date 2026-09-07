using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMCP.Roslyn;

/// <summary>
/// Renders Roslyn results as the plain text the tools return.
/// </summary>
/// <remarks>
/// Positions are 0-based on tool input and 1-based in this output, matching the convention the
/// server has always used. Locations carry their source line text: a bare file/line/column pair
/// forces the caller to make another round trip just to see what is there.
/// </remarks>
internal static class SymbolFormatter
{
    /// <summary>Signature form used for headings: fully qualified, with parameter types.</summary>
    public static readonly SymbolDisplayFormat Signature = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Compact form used inside lists, without the containing type.</summary>
    public static readonly SymbolDisplayFormat Member = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Writes a symbol as a bullet with its resolvable id underneath.</summary>
    public static void AppendSymbolLine(StringBuilder sb, ISymbol symbol, string indent = "")
    {
        sb.AppendLine($"{indent}- {symbol.Kind} {symbol.ToDisplayString(Signature)}");
        if (SymbolResolver.IdFor(symbol) is { } id)
            sb.AppendLine($"{indent}  id: {id}");
    }

    /// <summary>Writes a source location as "path:line:col" followed by the source line itself.</summary>
    public static void AppendLocationLine(StringBuilder sb, Location location, SourceText? text, string indent = "  ")
    {
        if (!location.IsInSource)
        {
            sb.AppendLine($"{indent}{Describe(location)}");
            return;
        }

        var span = location.GetLineSpan();
        var path = span.Path;
        var line = span.StartLinePosition.Line;
        var column = span.StartLinePosition.Character;

        sb.AppendLine($"{indent}{path}:{line + 1}:{column + 1}");

        if (text is not null && line < text.Lines.Count)
        {
            var source = text.Lines[line].ToString().Trim();
            if (source.Length > 0)
                sb.AppendLine($"{indent}    {Truncate(source, 200)}");
        }
    }

    /// <summary>Describes a location that has no source, such as one inside a referenced assembly.</summary>
    public static string Describe(Location location) => location.Kind switch
    {
        LocationKind.MetadataFile => $"<metadata: {location.MetadataModule?.ContainingAssembly?.Name ?? "unknown"}>",
        LocationKind.None => "<no location>",
        _ => location.ToString(),
    };

    /// <summary>Strips the XML documentation wrapper down to readable summary and returns text.</summary>
    public static string? SummarizeDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        var text = System.Text.RegularExpressions.Regex.Replace(xml, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        return text.Length == 0 ? null : text;
    }

    /// <summary>Renders the candidate list returned when a name matches more than one symbol.</summary>
    public static string FormatAmbiguity(IReadOnlyList<ISymbol> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"'{candidates[0].Name}' is ambiguous - {candidates.Count} symbols match. Re-run with one of these ids:");
        sb.AppendLine();

        foreach (var candidate in candidates)
            AppendSymbolLine(sb, candidate);

        return sb.ToString();
    }

    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    /// <summary>Appends a trailing note when a result set was capped.</summary>
    public static void AppendTruncationNote(StringBuilder sb, int shown, int total, string noun)
    {
        if (total > shown)
            sb.AppendLine($"\n... and {total - shown} more {noun} (raise maxResults to see them).");
    }
}
