using Microsoft.Extensions.Logging;

namespace CSharpLangMCP.Tools;

/// <summary>
/// Shared execution wrapper for the C# tools.
/// </summary>
/// <remarks>
/// Every tool returns a human-readable string and reports failure in-band as "Error: ...", so a
/// missing symbol or an unloaded workspace reads as an answer rather than an MCP protocol fault.
/// </remarks>
internal static class ToolBase
{
    /// <summary>
    /// Ceiling for a single tool call. Loading a large solution is slow enough to need real headroom.
    /// </summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs a tool body under its own timeout.
    /// </summary>
    /// <remarks>
    /// The MCP request token is deliberately not linked into the token passed downwards: the MCP SDK
    /// intercepts <see cref="OperationCanceledException"/> before these catch blocks run, so linking
    /// it would let a cancelled request abort an in-flight workspace load and leave the service in a
    /// half-initialized state. The token is still read, to word the error accurately.
    /// </remarks>
    public static async Task<string> ExecuteAsync(
        ILogger logger,
        string toolName,
        Func<CancellationToken, Task<string>> action,
        CancellationToken mcpToken)
    {
        using var cts = new CancellationTokenSource(OperationTimeout);

        try
        {
            return await action(cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Tool {ToolName} was cancelled", toolName);
            return mcpToken.IsCancellationRequested
                ? "Error: Operation was cancelled by the client."
                : $"Error: Operation timed out after {OperationTimeout.TotalSeconds} seconds.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing tool {ToolName}", toolName);
            return $"Error: {ex.Message}";
        }
    }
}
