using System.ComponentModel;
using System.Text;
using CSharpLangMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLangMCP.Tools;

/// <summary>Symbol search and semantic navigation across the workspace.</summary>
[McpServerToolType]
public sealed class NavigationTools(ILogger<NavigationTools> logger, RoslynWorkspaceService workspace)
{
    [McpServerTool(Name = "csharp_find_symbol")]
    [Description("Search the whole solution for declared symbols by name or wildcard pattern. Start here: the ids it returns address a symbol exactly for the other tools, with no file position needed.")]
    public Task<string> FindSymbolAsync(
        [Description("Name or substring to search for, e.g. 'LoadAsync' or 'SymbolResolver'")] string query,
        [Description("Restrict to a kind: Class, Struct, Interface, Enum, Method, Property, Field, Event, Namespace. Omit for all.")] string? kind = null,
        [Description("Maximum results (default 50)")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_find_symbol", async ct =>
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Error: query must not be empty.";

            var solution = await workspace.GetSolutionAsync(null, overlay: null, ct);

            var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                solution, query, SymbolFilter.All, ct);

            var symbols = found.ToList();

            if (kind is not null)
            {
                if (!TryParseKind(kind, out var predicate))
                    return $"Error: Unknown kind '{kind}'. Use Class, Struct, Interface, Enum, Method, Property, Field, Event or Namespace.";

                symbols = symbols.Where(predicate).ToList();
            }

            if (symbols.Count == 0)
            {
                var suffix = kind is null ? "" : $" of kind {kind}";
                return $"No symbol matching '{query}'{suffix} found in the workspace.";
            }

            var ordered = symbols
                .OrderBy(s => s.Name.Length)
                .ThenBy(s => s.ToDisplayString(), StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"{ordered.Count} symbol(s) matching '{query}':");
            sb.AppendLine();

            foreach (var symbol in ordered.Take(maxResults))
            {
                SymbolFormatter.AppendSymbolLine(sb, symbol);
                foreach (var location in symbol.Locations.Where(l => l.IsInSource).Take(1))
                    SymbolFormatter.AppendLocationLine(sb, location, null, "    ");
            }

            SymbolFormatter.AppendTruncationNote(sb, Math.Min(maxResults, ordered.Count), ordered.Count, "symbol(s)");
            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_symbol_info")]
    [Description("Get the signature, documentation, base types and declaration sites of a symbol. Combines what hover and go-to-definition used to return separately.")]
    public Task<string> SymbolInfoAsync(
        [Description("Symbol id from csharp_find_symbol, or a name like 'SymbolResolver.ResolveAsync'. Omit to use filePath/line/character instead.")] string? symbol = null,
        [Description("Absolute path to a .cs file, when locating by position")] string? filePath = null,
        [Description("0-based line number, when locating by position")] int line = 0,
        [Description("0-based character position, when locating by position")] int character = 0,
        [Description("Unsaved file content, used only when locating by position")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_symbol_info", async ct =>
        {
            var resolved = await SymbolResolver.ResolveArgumentsAsync(
                workspace, symbol, filePath, line, character, content, ct);

            if (resolved.Error is { } failure)
                return failure;

            var (target, solution) = (resolved.Symbol!, resolved.Solution!);
            var texts = new SourceTextCache(solution);

            var sb = new StringBuilder();
            sb.AppendLine(target.ToDisplayString(SymbolFormatter.Signature));
            sb.AppendLine();
            sb.AppendLine($"Kind:       {target.Kind}");
            sb.AppendLine($"Accessibility: {target.DeclaredAccessibility}");

            if (target.ContainingType is { } containingType)
                sb.AppendLine($"Containing type: {containingType.ToDisplayString(SymbolFormatter.Signature)}");
            if (target.ContainingNamespace is { IsGlobalNamespace: false } ns)
                sb.AppendLine($"Namespace:  {ns.ToDisplayString()}");
            if (target.ContainingAssembly is { } assembly)
                sb.AppendLine($"Assembly:   {assembly.Name}");

            var modifiers = DescribeModifiers(target);
            if (modifiers.Length > 0)
                sb.AppendLine($"Modifiers:  {string.Join(", ", modifiers)}");

            if (SymbolResolver.IdFor(target) is { } id)
                sb.AppendLine($"Id:         {id}");

            if (target is INamedTypeSymbol namedType)
                AppendTypeRelationships(sb, namedType);

            if (target is IMethodSymbol { ReturnType: { } returnType })
                sb.AppendLine($"Returns:    {returnType.ToDisplayString(SymbolFormatter.Member)}");

            if (SymbolFormatter.SummarizeDocumentation(target) is { } documentation)
            {
                sb.AppendLine();
                sb.AppendLine("Documentation:");
                sb.AppendLine($"  {SymbolFormatter.Truncate(documentation, 1500)}");
            }

            sb.AppendLine();
            var sourceLocations = target.Locations.Where(l => l.IsInSource).ToArray();
            if (sourceLocations.Length > 0)
            {
                sb.AppendLine($"Declared at ({sourceLocations.Length} location(s)):");
                foreach (var location in sourceLocations)
                {
                    var text = await texts.ForAsync(location, ct);
                    SymbolFormatter.AppendLocationLine(sb, location, text);
                }
            }
            else
            {
                sb.AppendLine($"Declared in metadata: {SymbolFormatter.Describe(target.Locations.FirstOrDefault() ?? Location.None)}");
            }

            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_references")]
    [Description("Find every reference to a symbol across the solution, grouped by file, with the source line at each use site.")]
    public Task<string> ReferencesAsync(
        [Description("Symbol id from csharp_find_symbol, or a name like 'SymbolResolver.ResolveAsync'. Omit to use filePath/line/character instead.")] string? symbol = null,
        [Description("Absolute path to a .cs file, when locating by position")] string? filePath = null,
        [Description("0-based line number, when locating by position")] int line = 0,
        [Description("0-based character position, when locating by position")] int character = 0,
        [Description("Include the declaration itself in the results (default true)")] bool includeDeclaration = true,
        [Description("Unsaved file content, used only when locating by position")] string? content = null,
        [Description("Maximum reference sites to return (default 200)")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_references", async ct =>
        {
            var resolved = await SymbolResolver.ResolveArgumentsAsync(
                workspace, symbol, filePath, line, character, content, ct);

            if (resolved.Error is { } failure)
                return failure;

            var (target, solution) = (resolved.Symbol!, resolved.Solution!);
            var texts = new SourceTextCache(solution);

            var referenced = await SymbolFinder.FindReferencesAsync(target, solution, ct);

            var sites = referenced
                .SelectMany(r => r.Locations)
                .Where(l => !l.IsImplicit)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"References to {target.ToDisplayString(SymbolFormatter.Signature)}");
            sb.AppendLine();

            if (includeDeclaration)
            {
                var declarations = target.Locations.Where(l => l.IsInSource).ToArray();
                if (declarations.Length > 0)
                {
                    sb.AppendLine("Declaration:");
                    foreach (var location in declarations)
                        SymbolFormatter.AppendLocationLine(sb, location, await texts.ForAsync(location, ct));
                    sb.AppendLine();
                }
            }

            if (sites.Count == 0)
            {
                sb.AppendLine("No references found.");
                return sb.ToString();
            }

            sb.AppendLine($"{sites.Count} reference(s):");
            sb.AppendLine();

            var groups = sites
                .GroupBy(l => l.Document.FilePath ?? l.Document.Name, RoslynWorkspaceService.PathComparer)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            var shown = 0;
            foreach (var group in groups)
            {
                if (shown >= maxResults)
                    break;

                sb.AppendLine($"{group.Key}");

                var text = await group.First().Document.GetTextAsync(ct);
                var ordered = group
                    .OrderBy(l => l.Location.SourceSpan.Start)
                    .Take(maxResults - shown)
                    .ToArray();

                foreach (var site in ordered)
                {
                    var position = text.Lines.GetLinePosition(site.Location.SourceSpan.Start);
                    var source = text.Lines[position.Line].ToString().Trim();
                    sb.AppendLine($"  {position.Line + 1}:{position.Character + 1}  {SymbolFormatter.Truncate(source, 200)}");
                }

                shown += ordered.Length;
                sb.AppendLine();
            }

            SymbolFormatter.AppendTruncationNote(sb, shown, sites.Count, "reference(s)");
            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_implementations")]
    [Description("Find implementations of an interface or interface member, types derived from a class, and overrides of a virtual or abstract member.")]
    public Task<string> ImplementationsAsync(
        [Description("Symbol id from csharp_find_symbol, or a type or member name")] string? symbol = null,
        [Description("Absolute path to a .cs file, when locating by position")] string? filePath = null,
        [Description("0-based line number, when locating by position")] int line = 0,
        [Description("0-based character position, when locating by position")] int character = 0,
        [Description("Only report results declared in this solution's source. Turn off to include types from referenced assemblies, which for a BCL interface can be hundreds. Default true.")] bool sourceOnly = true,
        [Description("Maximum results (default 100)")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_implementations", async ct =>
        {
            var resolved = await SymbolResolver.ResolveArgumentsAsync(
                workspace, symbol, filePath, line, character, null, ct);

            if (resolved.Error is { } failure)
                return failure;

            var (target, solution) = (resolved.Symbol!, resolved.Solution!);
            var texts = new SourceTextCache(solution);

            var results = new List<(string Relation, ISymbol Symbol)>();

            if (target is INamedTypeSymbol type)
            {
                if (type.TypeKind == TypeKind.Interface)
                {
                    foreach (var found in await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: ct))
                        results.Add(("implements", found));
                }
                else
                {
                    foreach (var found in await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: ct))
                        results.Add(("derives from", found));
                }
            }
            else
            {
                foreach (var found in await SymbolFinder.FindImplementationsAsync(target, solution, cancellationToken: ct))
                    results.Add(("implements", found));

                foreach (var found in await SymbolFinder.FindOverridesAsync(target, solution, cancellationToken: ct))
                    results.Add(("overrides", found));
            }

            if (sourceOnly)
                results = results.Where(r => r.Symbol.Locations.Any(l => l.IsInSource)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Implementations of {target.ToDisplayString(SymbolFormatter.Signature)}");
            sb.AppendLine();

            if (results.Count == 0)
            {
                sb.AppendLine(sourceOnly
                    ? "None found in source. Pass sourceOnly=false to include referenced assemblies."
                    : "None found.");
                return sb.ToString();
            }

            sb.AppendLine($"{results.Count} result(s):");
            sb.AppendLine();

            foreach (var (relation, found) in results
                .OrderBy(r => r.Symbol.ToDisplayString(), StringComparer.Ordinal)
                .Take(maxResults))
            {
                sb.AppendLine($"- {relation}: {found.ToDisplayString(SymbolFormatter.Signature)}");
                if (SymbolResolver.IdFor(found) is { } id)
                    sb.AppendLine($"  id: {id}");

                var location = found.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is not null)
                    SymbolFormatter.AppendLocationLine(sb, location, await texts.ForAsync(location, ct), "    ");
            }

            SymbolFormatter.AppendTruncationNote(sb, Math.Min(maxResults, results.Count), results.Count, "result(s)");
            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_type_hierarchy")]
    [Description("Show a type's full hierarchy in one call: its base class chain and interfaces above it, and the tree of types deriving from or implementing it below.")]
    public Task<string> TypeHierarchyAsync(
        [Description("Symbol id from csharp_find_symbol, or a type name")] string? symbol = null,
        [Description("Absolute path to a .cs file, when locating by position")] string? filePath = null,
        [Description("0-based line number, when locating by position")] int line = 0,
        [Description("0-based character position, when locating by position")] int character = 0,
        [Description("Only report derived types declared in this solution's source. Default true.")] bool sourceOnly = true,
        [Description("Maximum derived types (default 100)")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_type_hierarchy", async ct =>
        {
            var resolved = await SymbolResolver.ResolveArgumentsAsync(
                workspace, symbol, filePath, line, character, null, ct);

            if (resolved.Error is { } failure)
                return failure;

            if (resolved.Symbol is not INamedTypeSymbol type)
                return $"Error: {resolved.Symbol!.ToDisplayString()} is a {resolved.Symbol.Kind}, not a type.";

            return await DescribeHierarchyAsync(resolved.Solution!, type, sourceOnly, maxResults, ct);
        }, cancellationToken);
    }

    internal static async Task<string> DescribeHierarchyAsync(
        Solution solution, INamedTypeSymbol type, bool sourceOnly, int maxResults, CancellationToken ct)
    {
        type = type.OriginalDefinition;
        var texts = new SourceTextCache(solution);

        var sb = new StringBuilder();
        sb.AppendLine($"Hierarchy of {type.ToDisplayString(SymbolFormatter.Signature)}");
        if (SymbolResolver.IdFor(type) is { } typeId)
            sb.AppendLine($"id: {typeId}");

        // Above: the base chain from the root down to this type, so it reads like the tree below it.
        var chain = new List<INamedTypeSymbol>();
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            chain.Insert(0, current);

        if (chain.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Base types:");
            for (var i = 0; i < chain.Count; i++)
                sb.AppendLine($"{new string(' ', 2 + i * 2)}{chain[i].ToDisplayString()}");
            sb.AppendLine($"{new string(' ', 2 + chain.Count * 2)}{type.ToDisplayString()}   <- this type");
        }

        if (type.AllInterfaces.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Interfaces:");
            foreach (var iface in type.AllInterfaces.OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal))
            {
                // An interface reached through a base type or another interface is worth flagging,
                // since it is not on this type's own declaration.
                var direct = type.Interfaces.Contains(iface, SymbolEqualityComparer.Default) ? "" : "   (inherited)";
                sb.AppendLine($"  {iface.ToDisplayString()}{direct}");
            }
        }

        // Below: every derived type, then each hung under its nearest ancestor in the result.
        var derived = type.TypeKind == TypeKind.Interface
            ? (await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: ct))
                .Concat(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: ct))
            : await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: ct);

        var below = derived
            .Select(d => d.OriginalDefinition)
            .Where(d => !sourceOnly || d.Locations.Any(l => l.IsInSource))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToList();

        sb.AppendLine();
        if (below.Count == 0)
        {
            sb.AppendLine(type.IsSealed || type.TypeKind is TypeKind.Struct or TypeKind.Enum
                ? "Derived types: none (sealed)."
                : sourceOnly
                    ? "Derived types: none in source. Pass sourceOnly=false to include referenced assemblies."
                    : "Derived types: none.");
            return sb.ToString();
        }

        var members = new HashSet<INamedTypeSymbol>(below, SymbolEqualityComparer.Default) { type };
        var children = below.ToLookup(d => ParentOf(d, members, type), SymbolEqualityComparer.Default);

        sb.AppendLine($"Derived types ({below.Count}):");
        var shown = 0;
        await AppendChildrenAsync(type, 1);

        SymbolFormatter.AppendTruncationNote(sb, shown, below.Count, "derived type(s)");
        return sb.ToString();

        async Task AppendChildrenAsync(INamedTypeSymbol parent, int depth)
        {
            foreach (var child in children[parent].OrderBy(c => c.ToDisplayString(), StringComparer.Ordinal))
            {
                if (shown++ >= maxResults)
                    return;

                var indent = new string(' ', depth * 2);
                sb.AppendLine($"{indent}{child.ToDisplayString()}");
                if (child.Locations.FirstOrDefault(l => l.IsInSource) is { } location)
                    SymbolFormatter.AppendLocationLine(sb, location, await texts.ForAsync(location, ct), indent + "    ");

                await AppendChildrenAsync(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// The nearest ancestor of a derived type that is itself in the hierarchy: its base class when
    /// that is in the set, otherwise the first of its own interfaces that is.
    /// </summary>
    private static INamedTypeSymbol ParentOf(
        INamedTypeSymbol derived, HashSet<INamedTypeSymbol> members, INamedTypeSymbol root)
    {
        var candidates = new[] { derived.BaseType }.Concat(derived.Interfaces)
            .OfType<INamedTypeSymbol>()
            .Select(c => c.OriginalDefinition);

        // A type reaching the root only through something filtered out of the set (a base class in
        // a referenced assembly, say) still belongs under the root rather than vanishing.
        return candidates.FirstOrDefault(members.Contains) ?? root;
    }

    [McpServerTool(Name = "csharp_callers")]
    [Description("Find the methods that call a given method or access a given property or field, with the calling source line.")]
    public Task<string> CallersAsync(
        [Description("Symbol id from csharp_find_symbol, or a name like 'SymbolResolver.ResolveAsync'")] string? symbol = null,
        [Description("Absolute path to a .cs file, when locating by position")] string? filePath = null,
        [Description("0-based line number, when locating by position")] int line = 0,
        [Description("0-based character position, when locating by position")] int character = 0,
        [Description("Maximum callers to return (default 100)")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_callers", async ct =>
        {
            var resolved = await SymbolResolver.ResolveArgumentsAsync(
                workspace, symbol, filePath, line, character, null, ct);

            if (resolved.Error is { } failure)
                return failure;

            var (target, solution) = (resolved.Symbol!, resolved.Solution!);
            var texts = new SourceTextCache(solution);

            var callers = (await SymbolFinder.FindCallersAsync(target, solution, ct)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Callers of {target.ToDisplayString(SymbolFormatter.Signature)}");
            sb.AppendLine();

            if (callers.Count == 0)
            {
                sb.AppendLine("No callers found.");
                return sb.ToString();
            }

            sb.AppendLine($"{callers.Count} calling symbol(s):");
            sb.AppendLine();

            foreach (var caller in callers
                .OrderBy(c => c.CallingSymbol.ToDisplayString(), StringComparer.Ordinal)
                .Take(maxResults))
            {
                var indirect = caller.IsDirect ? "" : " (indirect)";
                sb.AppendLine($"- {caller.CallingSymbol.ToDisplayString(SymbolFormatter.Signature)}{indirect}");
                if (SymbolResolver.IdFor(caller.CallingSymbol) is { } id)
                    sb.AppendLine($"  id: {id}");

                foreach (var location in caller.Locations.Where(l => l.IsInSource))
                    SymbolFormatter.AppendLocationLine(sb, location, await texts.ForAsync(location, ct), "    ");
            }

            SymbolFormatter.AppendTruncationNote(sb, Math.Min(maxResults, callers.Count), callers.Count, "caller(s)");
            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_callees")]
    [Description("Find what a method, constructor or property calls: the methods, constructors and properties its body invokes, including inside lambdas and local functions. The reverse of csharp_callers.")]
    public Task<string> CalleesAsync(
        [Description("Symbol id from csharp_find_symbol, or a name like 'SymbolResolver.ResolveAsync'")] string? symbol = null,
        [Description("Absolute path to a .cs file, when locating by position")] string? filePath = null,
        [Description("0-based line number, when locating by position")] int line = 0,
        [Description("0-based character position, when locating by position")] int character = 0,
        [Description("Only report callees declared in this solution's source, hiding BCL and package calls. Default false.")] bool sourceOnly = false,
        [Description("Include reads and writes of auto-properties and positional record properties, which are field access in all but name. Turn off to see only calls that run code. Default true.")] bool includeAutoProperties = true,
        [Description("Maximum callees to return (default 100)")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_callees", async ct =>
        {
            var resolved = await SymbolResolver.ResolveArgumentsAsync(
                workspace, symbol, filePath, line, character, null, ct);

            if (resolved.Error is { } failure)
                return failure;

            return await DescribeCalleesAsync(resolved.Solution!, resolved.Symbol!, sourceOnly, includeAutoProperties, maxResults, ct);
        }, cancellationToken);
    }

    internal static async Task<string> DescribeCalleesAsync(
        Solution solution, ISymbol target, bool sourceOnly, bool includeAutoProperties, int maxResults, CancellationToken ct)
    {
        if (target.DeclaringSyntaxReferences.Length == 0)
            return $"Error: {target.ToDisplayString()} is declared in metadata, so it has no body here. Use csharp_decompile to read it.";

        var sites = new List<(ISymbol Callee, Location Site)>();

        // Partial declarations each contribute a body, so every one is walked.
        foreach (var reference in target.DeclaringSyntaxReferences)
        {
            var declaration = await reference.GetSyntaxAsync(ct);
            if (solution.GetDocument(declaration.SyntaxTree) is not { } document ||
                await document.GetSemanticModelAsync(ct) is not { } model)
            {
                continue;
            }

            foreach (var root in OperationRoots(declaration, model, ct))
            {
                foreach (var operation in root.DescendantsAndSelf())
                {
                    ISymbol? callee = operation switch
                    {
                        IInvocationOperation invocation => invocation.TargetMethod,
                        IObjectCreationOperation creation => creation.Constructor,
                        IPropertyReferenceOperation property => property.Property,
                        IMethodReferenceOperation methodGroup => methodGroup.Method,
                        _ => null,
                    };

                    if (callee is null || operation.IsImplicit)
                        continue;

                    // Report the declaration rather than one constructed or reduced form of it, so
                    // List<int>.Add and List<string>.Add, or an extension call either way, group together.
                    callee = callee is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : callee.OriginalDefinition;

                    if (sourceOnly && !callee.Locations.Any(l => l.IsInSource))
                        continue;

                    if (!includeAutoProperties && callee is IPropertySymbol calledProperty && IsAutoProperty(calledProperty))
                        continue;

                    sites.Add((callee, NameLocation(operation.Syntax)));
                }
            }
        }

        var texts = new SourceTextCache(solution);
        var sb = new StringBuilder();
        sb.AppendLine($"Callees of {target.ToDisplayString(SymbolFormatter.Signature)}");
        sb.AppendLine();

        var grouped = sites
            .GroupBy(s => s.Callee, SymbolEqualityComparer.Default)
            .OrderBy(g => g.Min(s => s.Site.SourceSpan.Start))
            .ToList();

        if (grouped.Count == 0)
        {
            sb.AppendLine(sourceOnly
                ? "No calls into source. Pass sourceOnly=false to include BCL and package calls."
                : "No calls found.");
            return sb.ToString();
        }

        // Ordered by first call site, which reads as the order the body does things in.
        sb.AppendLine($"{grouped.Count} callee(s), in order of first call:");
        sb.AppendLine();

        foreach (var group in grouped.Take(maxResults))
        {
            var callee = group.Key!;
            var origin = callee.Locations.Any(l => l.IsInSource)
                ? ""
                : $"   [{callee.ContainingAssembly?.Name ?? "metadata"}]";

            sb.AppendLine($"- {callee.Kind} {callee.ToDisplayString(SymbolFormatter.Signature)}{origin}");
            if (SymbolResolver.IdFor(callee) is { } id)
                sb.AppendLine($"  id: {id}");

            // The operation walk visits the outermost call of a chain first, so a.F().F() would
            // otherwise list the second F before the first.
            foreach (var (_, site) in group
                .OrderBy(s => s.Site.SourceTree?.FilePath, StringComparer.Ordinal)
                .ThenBy(s => s.Site.SourceSpan.Start))
            {
                SymbolFormatter.AppendLocationLine(sb, site, await texts.ForAsync(site, ct), "    ");
            }
        }

        SymbolFormatter.AppendTruncationNote(sb, Math.Min(maxResults, grouped.Count), grouped.Count, "callee(s)");
        return sb.ToString();
    }

    /// <summary>
    /// Where to point at a call: the called member's own name rather than the start of the
    /// expression, which for a chain like <c>a.B().C()</c> is the same <c>a</c> for every link.
    /// </summary>
    private static Location NameLocation(SyntaxNode syntax) => syntax switch
    {
        InvocationExpressionSyntax { Expression: var callee } => NameLocation(callee),
        MemberAccessExpressionSyntax access => access.Name.GetLocation(),
        MemberBindingExpressionSyntax binding => binding.Name.GetLocation(),
        _ => syntax.GetLocation(),
    };

    /// <summary>
    /// A source property with no hand-written accessor body: an auto-property or a positional
    /// record property. Reading one runs no code of the caller's, so it is data access, not a call.
    /// A property from metadata cannot be told apart and is never treated as one.
    /// </summary>
    private static bool IsAutoProperty(IPropertySymbol property) =>
        property.DeclaringSyntaxReferences.Length > 0 &&
        property.DeclaringSyntaxReferences.All(r => r.GetSyntax() switch
        {
            ParameterSyntax => true,
            PropertyDeclarationSyntax { ExpressionBody: null, AccessorList: { } accessors } =>
                accessors.Accessors.All(a => a.Body is null && a.ExpressionBody is null),
            _ => false,
        });

    /// <summary>
    /// The outermost nodes under a declaration that have an operation tree: a method's body, each
    /// accessor of a property, an expression body, an initializer. Their descendants cover the rest,
    /// lambdas and local functions included.
    /// </summary>
    private static IEnumerable<IOperation> OperationRoots(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        if (model.GetOperation(node, ct) is { } operation)
        {
            yield return operation;
            yield break;
        }

        foreach (var child in node.ChildNodes())
        {
            foreach (var root in OperationRoots(child, model, ct))
                yield return root;
        }
    }

    /// <summary>
    /// Turns a kind name into a filter. Every name the tool documents is a member of either
    /// <see cref="TypeKind"/> (Class, Struct, Interface, Enum) or <see cref="SymbolKind"/> (Method,
    /// Property, Field, Event, Namespace), so the enums do the parsing.
    /// </summary>
    private static bool TryParseKind(string kind, out Func<ISymbol, bool> predicate)
    {
        predicate = _ => true;
        kind = kind.Trim();

        // Enum.TryParse also accepts the underlying numbers; only names are meant to work here.
        if (kind.Length == 0 || !char.IsLetter(kind[0]))
            return false;

        if (Enum.TryParse<TypeKind>(kind, ignoreCase: true, out var typeKind))
        {
            predicate = s => s is INamedTypeSymbol named && named.TypeKind == typeKind;
            return true;
        }

        if (Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out var symbolKind))
        {
            predicate = s => s.Kind == symbolKind;
            return true;
        }

        return false;
    }

    private static string[] DescribeModifiers(ISymbol symbol)
    {
        var modifiers = new List<string>();
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsSealed) modifiers.Add("sealed");
        if (symbol.IsExtern) modifiers.Add("extern");
        if (symbol is IMethodSymbol { IsAsync: true }) modifiers.Add("async");
        if (symbol is INamedTypeSymbol { IsRecord: true }) modifiers.Add("record");
        if (symbol is INamedTypeSymbol { IsReadOnly: true } or IFieldSymbol { IsReadOnly: true }) modifiers.Add("readonly");
        return [.. modifiers];
    }

    private static void AppendTypeRelationships(StringBuilder sb, INamedTypeSymbol type)
    {
        sb.AppendLine($"Type kind:  {type.TypeKind}");

        if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            sb.AppendLine($"Base type:  {baseType.ToDisplayString(SymbolFormatter.Signature)}");

        var interfaces = type.Interfaces;
        if (interfaces.Length > 0)
        {
            sb.AppendLine("Interfaces:");
            foreach (var contract in interfaces)
                sb.AppendLine($"  {contract.ToDisplayString(SymbolFormatter.Signature)}");
        }
    }
}
