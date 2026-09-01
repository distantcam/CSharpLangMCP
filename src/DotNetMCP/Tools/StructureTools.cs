using System.ComponentModel;
using System.Text;
using DotNetMCP.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotNetMCP.Tools;

/// <summary>Reading the shape of a file or a type.</summary>
[McpServerToolType]
public sealed class StructureTools(ILogger<StructureTools> logger, RoslynWorkspaceService workspace)
{
    [McpServerTool(Name = "csharp_outline")]
    [Description("List the declarations in a C# file as a nested outline - types, methods, properties, fields and events, with their signatures and line numbers.")]
    public Task<string> OutlineAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Unsaved file content (reads from disk if omitted)")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_outline", async ct =>
        {
            var full = Path.GetFullPath(filePath);
            if (content is not null)
                workspace.SetOverlay(full, content);

            var solution = await workspace.GetSolutionAsync(full, ct);

            var document = RoslynWorkspaceService.FindDocument(solution, full);
            if (document is null)
                return $"Error: File is not part of the loaded workspace: {full}";

            var root = await document.GetSyntaxRootAsync(ct);
            var model = await document.GetSemanticModelAsync(ct);
            if (root is null || model is null)
                return $"Error: Could not parse {full}";

            var sb = new StringBuilder();
            sb.AppendLine($"Outline of {full}");
            sb.AppendLine();

            var wrote = AppendMembers(sb, model, root.ChildNodes(), 0, ct);

            if (!wrote)
                sb.AppendLine("No declarations found.");

            return sb.ToString();
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_type_members")]
    [Description("List the members of a type, including types from referenced assemblies such as the BCL or a NuGet package. Use this instead of guessing what an API offers.")]
    public Task<string> TypeMembersAsync(
        [Description("Type name: 'LspClient', 'DotNetMCP.Roslyn.RoslynWorkspaceService', or a metadata name like 'System.Collections.Generic.List`1'")] string typeName,
        [Description("Include members inherited from base types (default false)")] bool includeInherited = false,
        [Description("Include non-public members (default false)")] bool includeNonPublic = false,
        [Description("Maximum members to return (default 200)")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_type_members", async ct =>
        {
            var solution = await workspace.GetSolutionAsync(null, ct);

            var type = await FindTypeAsync(solution, typeName, ct);
            if (type is null)
                return $"No type named '{typeName}' found in the workspace or its referenced assemblies.";

            var members = new List<(INamedTypeSymbol Owner, ISymbol Member)>();
            for (var current = type; current is not null; current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                    members.Add((current, member));

                if (!includeInherited)
                    break;
            }

            var visible = members
                .Where(m => includeNonPublic || m.Member.DeclaredAccessibility == Accessibility.Public)
                // Compiler-generated accessors and backing fields are noise in a member listing.
                .Where(m => !m.Member.IsImplicitlyDeclared)
                .Where(m => m.Member is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine(type.ToDisplayString(SymbolFormatter.Signature));
            if (SymbolResolver.IdFor(type) is { } typeId)
                sb.AppendLine($"id: {typeId}");
            sb.AppendLine($"Kind: {type.TypeKind}   Assembly: {type.ContainingAssembly?.Name ?? "unknown"}");

            if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
                sb.AppendLine($"Base: {baseType.ToDisplayString(SymbolFormatter.Member)}");
            if (type.Interfaces.Length > 0)
                sb.AppendLine($"Implements: {string.Join(", ", type.Interfaces.Select(i => i.ToDisplayString(SymbolFormatter.Member)))}");

            sb.AppendLine();

            if (visible.Count == 0)
            {
                sb.AppendLine("No matching members. Try includeNonPublic=true or includeInherited=true.");
                return sb.ToString();
            }

            sb.AppendLine($"{visible.Count} member(s):");

            foreach (var group in visible
                .GroupBy(m => m.Member.Kind)
                .OrderBy(g => KindOrder(g.Key)))
            {
                sb.AppendLine();
                sb.AppendLine($"{group.Key}:");

                foreach (var (owner, member) in group
                    .OrderBy(m => m.Member.Name, StringComparer.Ordinal)
                    .Take(maxResults))
                {
                    var inherited = SymbolEqualityComparer.Default.Equals(owner, type)
                        ? ""
                        : $"   (from {owner.Name})";
                    var accessibility = member.DeclaredAccessibility == Accessibility.Public
                        ? ""
                        : member.DeclaredAccessibility.ToString().ToLowerInvariant() + " ";

                    sb.AppendLine($"  {accessibility}{member.ToDisplayString(SymbolFormatter.Member)}{inherited}");
                }
            }

            SymbolFormatter.AppendTruncationNote(sb, Math.Min(maxResults, visible.Count), visible.Count, "member(s)");
            return sb.ToString();
        }, cancellationToken);
    }

    /// <summary>
    /// Resolves a type by metadata name first, then by a source declaration search, so both
    /// 'System.Collections.Generic.List`1' and a bare 'LspClient' work.
    /// </summary>
    private static async Task<INamedTypeSymbol?> FindTypeAsync(Solution solution, string typeName, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
                continue;

            if (compilation.GetTypeByMetadataName(typeName) is { } byMetadataName)
                return byMetadataName;
        }

        var lookup = await SymbolResolver.ResolveAsync(solution, typeName, ct);
        return lookup switch
        {
            SymbolLookup.Found { Symbol: INamedTypeSymbol found } => found,
            SymbolLookup.Ambiguous ambiguous => ambiguous.Candidates.OfType<INamedTypeSymbol>().FirstOrDefault(),
            _ => null,
        };
    }

    /// <summary>Walks declaration syntax, writing one indented line per member. Returns true if anything was written.</summary>
    private static bool AppendMembers(
        StringBuilder sb, SemanticModel model, IEnumerable<SyntaxNode> nodes, int depth, CancellationToken ct)
    {
        var indent = new string(' ', depth * 2);
        var wrote = false;

        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();

            switch (node)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    sb.AppendLine($"{indent}namespace {ns.Name}");
                    AppendMembers(sb, model, ns.Members, depth + 1, ct);
                    wrote = true;
                    break;

                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax
                    or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax:
                    wrote |= AppendDeclaration(sb, model, node, indent, depth, ct);
                    break;
            }
        }

        return wrote;
    }

    private static bool AppendDeclaration(
        StringBuilder sb, SemanticModel model, SyntaxNode node, string indent, int depth, CancellationToken ct)
    {
        // A field declaration can declare several variables; each is its own symbol.
        var symbols = node is BaseFieldDeclarationSyntax field
            ? field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, ct)).OfType<ISymbol>().ToArray()
            : model.GetDeclaredSymbol(node, ct) is { } single ? [single] : Array.Empty<ISymbol>();

        if (symbols.Length == 0)
            return false;

        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        foreach (var symbol in symbols)
            sb.AppendLine($"{indent}{symbol.ToDisplayString(SymbolFormatter.Member)}   [{symbol.Kind}, line {line}]");

        if (node is TypeDeclarationSyntax type)
        {
            // A positional record declares its properties in the primary constructor, so they are
            // absent from Members and have to be read off the type symbol instead.
            if (type is RecordDeclarationSyntax { ParameterList: { } parameters } &&
                symbols[0] is INamedTypeSymbol recordSymbol)
            {
                var childIndent = new string(' ', (depth + 1) * 2);
                foreach (var parameter in parameters.Parameters)
                {
                    var property = recordSymbol.GetMembers(parameter.Identifier.Text)
                        .FirstOrDefault(m => m.Kind == SymbolKind.Property);

                    if (property is not null)
                        sb.AppendLine($"{childIndent}{property.ToDisplayString(SymbolFormatter.Member)}   [Property, line {line}]");
                }
            }

            AppendMembers(sb, model, type.Members, depth + 1, ct);
        }
        else if (node is EnumDeclarationSyntax enumType)
        {
            AppendMembers(sb, model, enumType.Members, depth + 1, ct);
        }

        return true;
    }

    private static int KindOrder(SymbolKind kind) => kind switch
    {
        SymbolKind.NamedType => 0,
        SymbolKind.Field => 1,
        SymbolKind.Property => 2,
        SymbolKind.Event => 3,
        SymbolKind.Method => 4,
        _ => 5,
    };
}
