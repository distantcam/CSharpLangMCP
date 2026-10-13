using System.Composition.Hosting;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace CSharpLangMCP.Roslyn;

/// <summary>One code action offered at a span, flattened out of any nesting.</summary>
/// <param name="Title">
/// Display title, and the handle used to apply it. Nested actions are titled "Parent &gt; Child".
/// </param>
/// <param name="Kind">Whether this came from a diagnostic fix or a refactoring.</param>
/// <param name="DiagnosticId">The diagnostic this fixes, when it is a fix.</param>
/// <param name="Provider">Short name of the Roslyn provider that offered it.</param>
public sealed record AvailableCodeAction(
    string Title,
    CodeActionKind Kind,
    string? DiagnosticId,
    string Provider,
    CodeAction Action);

public enum CodeActionKind
{
    Fix,
    Refactoring,
}

/// <summary>
/// Offers Roslyn's built-in quick fixes and refactorings.
/// </summary>
/// <remarks>
/// Roslyn keeps <c>CodeFixService</c> and <c>CodeRefactoringService</c> internal, so the providers
/// are obtained the way OmniSharp does it: compose the Features assemblies in a MEF container and
/// drive each <see cref="CodeFixProvider"/> and <see cref="CodeRefactoringProvider"/> directly.
/// A bare container is enough - the providers do not need the workspace's own host services - so
/// the workspace is still created with Roslyn's defaults.
/// </remarks>
public sealed class CodeActionService(ILogger<CodeActionService> logger)
{
    /// <summary>Assemblies carrying the built-in providers.</summary>
    private static readonly string[] FeatureAssemblyNames =
    [
        "Microsoft.CodeAnalysis.Features",
        "Microsoft.CodeAnalysis.CSharp.Features",
    ];

    /// <summary>Guards against a pathological provider dragging a whole tool call down.</summary>
    private static readonly TimeSpan PerProviderBudget = TimeSpan.FromSeconds(10);

    /// <summary>How deep to expand nested action groups.</summary>
    private const int MaxNestingDepth = 3;

    /// <summary>
    /// The provider catalog, composed once. Costs a few hundred milliseconds, so it is built on
    /// first use rather than at startup; <see cref="Lazy{T}"/> defaults to doing that thread-safely.
    /// </summary>
    private readonly Lazy<(CodeFixProvider[] Fixes, CodeRefactoringProvider[] Refactorings)> _providers =
        new(() => Compose(logger));

    private static (CodeFixProvider[] Fixes, CodeRefactoringProvider[] Refactorings) Compose(ILogger logger)
    {
        var assemblies = FeatureAssemblyNames.Select(Assembly.Load).ToArray();
        var container = new ContainerConfiguration().WithAssemblies(assemblies).CreateContainer();

        CodeFixProvider[] fixes = [.. container.GetExports<CodeFixProvider>()
            .Where(p => SupportsCSharp(p.GetType().GetCustomAttribute<ExportCodeFixProviderAttribute>()?.Languages))
            .OrderBy(p => p.GetType().FullName, StringComparer.Ordinal)];

        CodeRefactoringProvider[] refactorings = [.. container.GetExports<CodeRefactoringProvider>()
            .Where(p => SupportsCSharp(p.GetType().GetCustomAttribute<ExportCodeRefactoringProviderAttribute>()?.Languages))
            .OrderBy(p => p.GetType().FullName, StringComparer.Ordinal)];

        logger.LogInformation(
            "Composed {Fixes} code fix and {Refactorings} refactoring provider(s)",
            fixes.Length, refactorings.Length);

        return (fixes, refactorings);
    }

    /// <summary>Collects every fix and refactoring offered for a span, in a stable order.</summary>
    public async Task<IReadOnlyList<AvailableCodeAction>> GetActionsAsync(
        Document document,
        TextSpan span,
        IReadOnlyList<Diagnostic> diagnostics,
        bool includeFixes,
        bool includeRefactorings,
        CancellationToken ct)
    {
        var (fixProviders, refactoringProviders) = _providers.Value;
        var collected = new List<AvailableCodeAction>();

        if (includeFixes)
        {
            // Only diagnostics that actually touch the span are fixable here, and only providers
            // that claim the id are worth invoking - that is what keeps this from running 170
            // providers per call.
            foreach (var diagnostic in diagnostics.Where(d => d.Location.SourceSpan.IntersectsWith(span)))
            {
                foreach (var provider in fixProviders.Where(p => p.FixableDiagnosticIds.Contains(diagnostic.Id)))
                {
                    var actions = new List<CodeAction>();
                    var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), ct);

                    if (await RunProviderAsync(provider, () => provider.RegisterCodeFixesAsync(context), ct))
                        Flatten(collected, actions, CodeActionKind.Fix, diagnostic.Id, ProviderName(provider), prefix: null, depth: 0);
                }
            }
        }

        if (includeRefactorings)
        {
            foreach (var provider in refactoringProviders)
            {
                var actions = new List<CodeAction>();
                var context = new CodeRefactoringContext(document, span, actions.Add, ct);

                if (await RunProviderAsync(provider, () => provider.ComputeRefactoringsAsync(context), ct))
                    Flatten(collected, actions, CodeActionKind.Refactoring, null, ProviderName(provider), prefix: null, depth: 0);
            }
        }

        // Several providers can offer the same fix for duplicate diagnostics on one span.
        return [.. collected
            .GroupBy(a => a.Title, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(a => a.Kind)
            .ThenBy(a => a.Title, StringComparer.Ordinal)];
    }

    /// <summary>Turns a code action into the edits it would make, or null when it makes none.</summary>
    /// <remarks>
    /// An action that only groups other actions throws from <c>GetOperationsAsync</c>, so grouping
    /// parents are never returned by <see cref="GetActionsAsync"/> - only their expanded children.
    /// Operations other than <see cref="ApplyChangesOperation"/> (opening a document, navigating)
    /// have no meaning for this server and are ignored.
    /// </remarks>
    public static async Task<Solution?> GetChangedSolutionAsync(CodeAction action, CancellationToken ct)
    {
        if (action.NestedActions.Length > 0)
            return null;

        var operations = await action.GetOperationsAsync(ct);
        return operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
    }

    /// <summary>
    /// Flattens grouped actions into individually applicable leaves, titled "Parent &gt; Child".
    /// </summary>
    private static void Flatten(
        List<AvailableCodeAction> into,
        IEnumerable<CodeAction> actions,
        CodeActionKind kind,
        string? diagnosticId,
        string provider,
        string? prefix,
        int depth)
    {
        foreach (var action in actions)
        {
            var title = prefix is null ? action.Title : $"{prefix} > {action.Title}";

            if (action.NestedActions.Length > 0 && depth < MaxNestingDepth)
            {
                Flatten(into, action.NestedActions, kind, diagnosticId, provider, title, depth + 1);
                continue;
            }

            // A group we refuse to descend into cannot be applied, so it is not worth listing.
            if (action.NestedActions.Length > 0)
                continue;

            into.Add(new AvailableCodeAction(title, kind, diagnosticId, provider, action));
        }
    }

    /// <summary>
    /// Runs one provider, swallowing its failure. Providers are third-party-ish code driven outside
    /// the host they expect; one that throws should cost its own results, not the whole call.
    /// </summary>
    private async Task<bool> RunProviderAsync(object provider, Func<Task> run, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(PerProviderBudget);

        try
        {
            await run().WaitAsync(budget.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Code action provider {Provider} failed", ProviderName(provider));
            return false;
        }
    }

    private static bool SupportsCSharp(string[]? languages) =>
        languages is null || languages.Contains(LanguageNames.CSharp);

    private static string ProviderName(object provider)
    {
        var name = provider.GetType().Name;
        foreach (var suffix in (string[])["CodeFixProvider", "CodeRefactoringProvider", "CodeActionProvider"])
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return name[..^suffix.Length];
        }

        return name;
    }
}
