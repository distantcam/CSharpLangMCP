using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMCP.Roslyn;

/// <summary>Outcome of resolving a caller-supplied symbol reference.</summary>
/// <remarks>
/// Ambiguity is reported rather than guessed at: the caller gets the candidates and their exact ids
/// so the next call can be unambiguous.
/// </remarks>
internal abstract record SymbolLookup
{
    internal sealed record Found(ISymbol Symbol) : SymbolLookup;

    internal sealed record Ambiguous(IReadOnlyList<ISymbol> Candidates) : SymbolLookup;

    internal sealed record NotFound(string Message) : SymbolLookup;
}

/// <summary>
/// Resolves the <c>symbol</c> tool argument, in three escalating forms: an exact documentation
/// comment id, a plain or partially qualified name, or a file position.
/// </summary>
internal static class SymbolResolver
{
    /// <summary>Upper bound on candidates reported for an ambiguous name.</summary>
    private const int MaxCandidates = 25;

    /// <summary>
    /// Stable, round-trippable id for a symbol, in XML documentation comment form
    /// (for example <c>M:Ns.Type.Method(System.String)</c>). Returned by every tool so a follow-up
    /// call can name the same symbol exactly.
    /// </summary>
    public static string? IdFor(ISymbol symbol) => DocumentationCommentId.CreateDeclarationId(symbol);

    /// <summary>Resolves a symbol by id or name, searching source declarations across the solution.</summary>
    public static async Task<SymbolLookup> ResolveAsync(Solution solution, string symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return new SymbolLookup.NotFound("No symbol specified.");

        symbol = symbol.Trim();

        // Tier 1: an exact documentation comment id, e.g. "M:Ns.Type.Method(System.String)".
        if (LooksLikeDocumentationId(symbol))
        {
            ISymbol? fallback = null;

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null)
                    continue;

                if (DocumentationCommentId.GetFirstSymbolForDeclarationId(symbol, compilation) is not { } resolved)
                    continue;

                // A project that references another sees its symbols through a compilation
                // reference, so the same id resolves in several projects to symbols that are
                // equivalent but not equal. Prefer the one from the assembly that declares it:
                // downstream operations such as FindReferences key off symbol identity.
                if (SymbolEqualityComparer.Default.Equals(resolved.ContainingAssembly, compilation.Assembly))
                    return new SymbolLookup.Found(resolved);

                fallback ??= resolved;
            }

            return fallback is null
                ? new SymbolLookup.NotFound($"No symbol matches the id '{symbol}'.")
                : new SymbolLookup.Found(fallback);
        }

        // Tier 2: a plain or partially qualified name. FindSourceDeclarations matches on the simple
        // name only, so a dotted query is filtered against the full display string afterwards.
        var simpleName = symbol[(symbol.LastIndexOf('.') + 1)..];
        var matches = await SymbolFinder.FindSourceDeclarationsAsync(
            solution, simpleName, ignoreCase: false, SymbolFilter.All, ct);

        var candidates = matches.ToList();

        if (candidates.Count == 0)
        {
            // Retry case-insensitively before giving up.
            candidates = (await SymbolFinder.FindSourceDeclarationsAsync(
                solution, simpleName, ignoreCase: true, SymbolFilter.All, ct)).ToList();
        }

        if (symbol.Contains('.'))
        {
            var qualified = candidates
                .Where(c => c.ToDisplayString().Contains(symbol, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (qualified.Count > 0)
                candidates = qualified;
        }

        return candidates.Count switch
        {
            0 => new SymbolLookup.NotFound($"No source symbol named '{symbol}' found in the workspace."),
            1 => new SymbolLookup.Found(candidates[0]),

            // Overloads and partial declarations of one symbol should not read as ambiguity.
            _ when DistinctById(candidates) is { Count: 1 } single => new SymbolLookup.Found(single[0]),
            _ => new SymbolLookup.Ambiguous(OrderCandidates(candidates).Take(MaxCandidates).ToList()),
        };
    }

    /// <summary>Resolves the symbol referenced or declared at a file position.</summary>
    public static async Task<SymbolLookup> ResolveAtPositionAsync(
        Solution solution, string filePath, int line, int character, CancellationToken ct)
    {
        var document = RoslynWorkspaceService.FindDocument(solution, filePath);
        if (document is null)
            return new SymbolLookup.NotFound($"File is not part of the loaded workspace: {filePath}");

        var text = await document.GetTextAsync(ct);
        if (!TryGetPosition(text, line, character, out var position, out var error))
            return new SymbolLookup.NotFound(error);

        var semanticModel = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        if (semanticModel is null || root is null)
            return new SymbolLookup.NotFound("Could not build a semantic model for the document.");

        var token = root.FindToken(position);

        // Covers usages; falls back to the declaration when the position sits on a declared name.
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, solution.Workspace, ct)
            ?? semanticModel.GetDeclaredSymbol(token.Parent!, ct);

        return symbol is null
            ? new SymbolLookup.NotFound($"No symbol at {filePath}:{line + 1},{character + 1}.")
            : new SymbolLookup.Found(symbol);
    }

    /// <summary>Converts a 0-based line/character pair into an absolute offset, with a clear error.</summary>
    public static bool TryGetPosition(SourceText text, int line, int character, out int position, out string error)
    {
        position = 0;

        if (line < 0 || character < 0)
        {
            error = "Line and character must be zero or greater.";
            return false;
        }

        if (line >= text.Lines.Count)
        {
            error = $"Line {line + 1} is past the end of the file ({text.Lines.Count} line(s)).";
            return false;
        }

        var textLine = text.Lines[line];
        if (character > textLine.Span.Length)
        {
            error = $"Character {character + 1} is past the end of line {line + 1} ({textLine.Span.Length} character(s)).";
            return false;
        }

        position = textLine.Start + character;
        error = string.Empty;
        return true;
    }

    /// <summary>True when the string carries an XML documentation id prefix such as "M:" or "T:".</summary>
    private static bool LooksLikeDocumentationId(string value) =>
        value.Length > 2 && value[1] == ':' && "NTMPFE".Contains(value[0], StringComparison.Ordinal);

    private static List<ISymbol> DistinctById(List<ISymbol> symbols) =>
        symbols
            .Select(IdFor)
            .Where(id => id is not null)
            .Distinct(StringComparer.Ordinal)
            .Select(id => symbols.First(s => string.Equals(IdFor(s), id, StringComparison.Ordinal)))
            .ToList();

    /// <summary>Types first, then by name, so the likeliest intent is at the top of an ambiguity list.</summary>
    private static IEnumerable<ISymbol> OrderCandidates(IEnumerable<ISymbol> symbols) =>
        symbols
            .OrderBy(s => s.Kind == SymbolKind.NamedType ? 0 : 1)
            .ThenBy(s => s.ToDisplayString(), StringComparer.Ordinal);
}
