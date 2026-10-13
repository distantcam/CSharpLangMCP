using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace CSharpLangMCP.Roslyn;

/// <summary>
/// Resolves the <c>symbol</c> tool argument, in three escalating forms: an exact documentation
/// comment id, a plain or partially qualified name, or a file position.
/// </summary>
/// <remarks>
/// Every entry point returns either a symbol or the error text to hand straight back to the caller,
/// so a miss and an ambiguity read the same way at the call site. Ambiguity is reported rather than
/// guessed at: the caller gets the candidates and their exact ids so the next call can be
/// unambiguous.
/// </remarks>
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

    /// <summary>
    /// Resolves the <c>symbol</c> and <c>filePath</c> argument pair every navigating tool takes: by
    /// id or name when one is given, by file position otherwise. Hands back the solution too, so
    /// the caller does not have to ask for it a second time.
    /// </summary>
    public static async Task<(ISymbol? Symbol, Solution? Solution, string? Error)> ResolveArgumentsAsync(
        RoslynWorkspaceService workspace,
        string? symbol,
        string? filePath,
        int line,
        int character,
        string? content,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(filePath))
            return (null, null, "Error: Provide either a symbol id or name, or a filePath with line and character.");

        (string Path, string Content)? overlay = filePath is not null && content is not null
            ? (Path.GetFullPath(filePath), content)
            : null;

        var solution = await workspace.GetSolutionAsync(filePath, overlay, ct);

        var (found, error) = string.IsNullOrWhiteSpace(symbol)
            ? await ResolveAtPositionAsync(solution, filePath!, line, character, ct)
            : await ResolveAsync(solution, symbol, ct);

        return (found, solution, error);
    }

    /// <summary>Resolves a symbol by id or name, searching source declarations across the solution.</summary>
    public static async Task<(ISymbol? Symbol, string? Error)> ResolveAsync(
        Solution solution, string symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return (null, "No symbol specified.");

        symbol = symbol.Trim();

        // Tier 1: an exact documentation comment id, e.g. "M:Ns.Type.Method(System.String)".
        if (LooksLikeDocumentationId(symbol))
            return await ResolveDocumentationIdAsync(solution, symbol, ct);

        // Tier 2: a plain or partially qualified name.
        var candidates = await FindByNameAsync(solution, symbol, ct);

        return candidates.Count switch
        {
            0 => (null, $"No source symbol named '{symbol}' found in the workspace."),
            1 => (candidates[0], null),

            // Overloads and partial declarations of one symbol should not read as ambiguity.
            _ when DistinctById(candidates) is { Count: 1 } single => (single[0], null),
            _ => (null, SymbolFormatter.FormatAmbiguity([.. candidates.Take(MaxCandidates)])),
        };
    }

    /// <summary>Resolves the symbol referenced or declared at a file position.</summary>
    public static async Task<(ISymbol? Symbol, string? Error)> ResolveAtPositionAsync(
        Solution solution, string filePath, int line, int character, CancellationToken ct)
    {
        var document = RoslynWorkspaceService.FindDocument(solution, filePath);
        if (document is null)
            return (null, $"File is not part of the loaded workspace: {filePath}");

        var text = await document.GetTextAsync(ct);
        if (!TryGetPosition(text, line, character, out var position, out var error))
            return (null, error);

        var semanticModel = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        if (semanticModel is null || root is null)
            return (null, "Could not build a semantic model for the document.");

        var token = root.FindToken(position);

        // Covers usages; falls back to the declaration when the position sits on a declared name.
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, solution.Workspace, ct)
            ?? semanticModel.GetDeclaredSymbol(token.Parent!, ct);

        return symbol is null
            ? (null, $"No symbol at {filePath}:{line + 1},{character + 1}.")
            : (symbol, null);
    }

    /// <summary>
    /// Every source declaration answering to a name, likeliest first. Exposed so a caller that wants
    /// one particular kind can pick it out rather than be told the name is ambiguous.
    /// </summary>
    /// <remarks>
    /// FindSourceDeclarations matches on the simple name only, so a dotted query is filtered against
    /// the full display string afterwards.
    /// </remarks>
    public static async Task<IReadOnlyList<ISymbol>> FindByNameAsync(
        Solution solution, string symbol, CancellationToken ct)
    {
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

        // Types first, then by name, so the likeliest intent is at the top.
        return
        [
            .. candidates
                .OrderBy(s => s.Kind == SymbolKind.NamedType ? 0 : 1)
                .ThenBy(s => s.ToDisplayString(), StringComparer.Ordinal)
        ];
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

    private static async Task<(ISymbol? Symbol, string? Error)> ResolveDocumentationIdAsync(
        Solution solution, string id, CancellationToken ct)
    {
        ISymbol? fallback = null;

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
                continue;

            if (DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) is not { } resolved)
                continue;

            // A project that references another sees its symbols through a compilation reference, so
            // the same id resolves in several projects to symbols that are equivalent but not equal.
            // Prefer the one from the assembly that declares it: downstream operations such as
            // FindReferences key off symbol identity.
            if (SymbolEqualityComparer.Default.Equals(resolved.ContainingAssembly, compilation.Assembly))
                return (resolved, null);

            fallback ??= resolved;
        }

        return fallback is null
            ? (null, $"No symbol matches the id '{id}'.")
            : (fallback, null);
    }

    /// <summary>True when the string carries an XML documentation id prefix such as "M:" or "T:".</summary>
    private static bool LooksLikeDocumentationId(string value) =>
        value.Length > 2 && value[1] == ':' && "NTMPFE".Contains(value[0], StringComparison.Ordinal);

    private static List<ISymbol> DistinctById(IReadOnlyList<ISymbol> symbols) =>
        symbols
            .Select(IdFor)
            .Where(id => id is not null)
            .Distinct(StringComparer.Ordinal)
            .Select(id => symbols.First(s => string.Equals(IdFor(s), id, StringComparison.Ordinal)))
            .ToList();
}
