using System.ComponentModel;
using System.Runtime.InteropServices;
using CSharpLangMCP.Roslyn;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLangMCP.Tools;

/// <summary>Reading the implementation of code that only exists as a compiled assembly.</summary>
[McpServerToolType]
public sealed class DecompileTools(ILogger<DecompileTools> logger, RoslynWorkspaceService workspace)
{
    [McpServerTool(Name = "csharp_decompile")]
    [Description(
        "Decompile a type or member from a referenced assembly (BCL, NuGet package) back to C#, to see how it is "
        + "implemented. For signatures and documentation alone, csharp_symbol_info is cheaper.")]
    public Task<string> DecompileAsync(
        [Description(
            "Symbol id like 'M:System.String.Join(System.String,System.String[])', or a type name like "
            + "'System.Collections.Generic.List`1'. Omit to use filePath/line/character instead.")]
        string? symbol = null,
        [Description(
            "Absolute path to a .cs file where the symbol is used, when locating by position")] string? filePath = null,
        [Description("0-based line number, when locating by position")] int line = 0,
        [Description("0-based character position, when locating by position")] int character = 0,
        CancellationToken cancellationToken = default)
    {
        return ToolBase.ExecuteAsync(logger, "csharp_decompile", async ct =>
        {
            var resolved = await SymbolResolver.ResolveArgumentsAsync(
                workspace, symbol, filePath, line, character, content: null, ct);

            var target = resolved.Symbol;

            // A bare name only searches source; a metadata type name needs the compilations.
            if (target is null && symbol is not null)
                target = await StructureTools.FindTypeAsync(resolved.Solution!, symbol.Trim(), ct);

            if (target is null)
                return resolved.Error ?? $"Error: No symbol '{symbol}' found.";

            return await DecompileSymbolAsync(resolved.Solution!, target, ct);
        }, cancellationToken);
    }

    internal static async Task<string> DecompileSymbolAsync(Solution solution, ISymbol target, CancellationToken ct)
    {
        // Reduced and constructed forms (an extension call, List<int>) decompile as their definition.
        target = target is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : target.OriginalDefinition;

        if (target.Locations.Any(l => l.IsInSource))
            return $"Error: {target.ToDisplayString()} is declared in source; read the file instead.";

        var type = target as INamedTypeSymbol ?? target.ContainingType;
        if (type is null || target.ContainingAssembly is not { } assembly)
            return $"Error: Only types and their members can be decompiled, not a {target.Kind}.";

        var path = await FindAssemblyPathAsync(solution, assembly, ct);
        if (path is null)
            return $"Error: Could not locate the file for assembly {assembly.Name}.";

        var note = "";
        var typeName = new FullTypeName(MetadataNameOf(type));

        if (IsReferenceAssembly(assembly))
        {
            // Reference assemblies have their bodies stripped, so read the running runtime's
            // implementation instead - which may be a newer version than the project targets.
            var implementation = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), assembly.Name + ".dll");
            if (File.Exists(implementation))
            {
                path = implementation;
                note = $"// {assembly.Name} is a reference assembly; decompiled from the .NET "
                    + $"{Environment.Version} runtime instead.\n";
            }
            else
            {
                note = $"// {assembly.Name} is a reference assembly and no implementation was found: "
                    + "method bodies are stubs.\n";
            }
        }

        var decompiler = CreateDecompiler(path);
        var definition = decompiler.TypeSystem.MainModule.GetTypeDefinition(typeName);

        // The lookup follows type forwards into other modules (the runtime's System.Collections
        // forwards List<T> to CoreLib), but a decompiler only reads its own module's handles.
        if (definition?.ParentModule?.MetadataFile?.FileName is { } actual && actual != path)
        {
            path = actual;
            decompiler = CreateDecompiler(path);
            definition = decompiler.TypeSystem.MainModule.GetTypeDefinition(typeName);
        }

        if (definition is null)
            return $"Error: {typeName.ReflectionName} was not found in {path}.";

        var header = $"// {path}\n{note}";

        if (target is INamedTypeSymbol)
            return header + decompiler.DecompileTypeAsString(typeName);

        // Roslyn and ILSpy both speak XML documentation ids, which pins down the exact overload.
        // Roslyn appends a '~ReturnType' suffix ILSpy omits outside conversion operators, so an exact
        // miss is retried without it.
        // ILSpy synthesizes members with no metadata behind them - a struct's parameterless
        // constructor - and computing their id throws. They have nothing to decompile anyway.
        var id = SymbolResolver.IdFor(target);
        var members = definition.Members.Where(m => !m.MetadataToken.IsNil).ToList();
        var member = members.FirstOrDefault(m => IdStringProvider.GetIdString(m) == id)
            ?? members.FirstOrDefault(m => WithoutReturnType(IdStringProvider.GetIdString(m)) == WithoutReturnType(id));

        return member is null
            ? header + $"// {id} was not matched; showing the whole type.\n"
                + decompiler.DecompileTypeAsString(typeName)
            : header + decompiler.DecompileAsString(member.MetadataToken);
    }

    private static CSharpDecompiler CreateDecompiler(string path)
        => new(path, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });

    private static string? WithoutReturnType(string? id)
        => id?.IndexOf('~', StringComparison.Ordinal) is >= 0 and var tilde ? id[..tilde] : id;

    private static async Task<string?> FindAssemblyPathAsync(
        Solution solution,
        IAssemblySymbol assembly,
        CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation?.GetMetadataReference(assembly) is PortableExecutableReference { FilePath: { } path })
                return path;
        }

        return null;
    }

    private static bool IsReferenceAssembly(IAssemblySymbol assembly)
        => assembly.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");

    /// <summary>The reflection name ILSpy expects: <c>Ns.Outer+Inner`1</c>.</summary>
    private static string MetadataNameOf(INamedTypeSymbol type)
        => type.ContainingType is { } outer
            ? MetadataNameOf(outer) + "+" + type.MetadataName
            : type.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? ns.ToDisplayString() + "." + type.MetadataName
                : type.MetadataName;
}
