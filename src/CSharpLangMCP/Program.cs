using CSharpLangMCP.Roslyn;
using CSharpLangMCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // The MCP protocol owns stdout, so every log line has to go to stderr.
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    var logLevel = LogLevel.Warning;
    if (args.Contains("--verbose") || args.Contains("-V"))
        logLevel = LogLevel.Debug;

    builder.Logging.SetMinimumLevel(logLevel);

    // One workspace shared by every tool; tool instances themselves are per-invocation.
    builder.Services.AddSingleton<RoslynWorkspaceService>();

    // Composing the Roslyn provider catalog is expensive, so it is shared and built lazily.
    builder.Services.AddSingleton<CodeActionService>();

    // Loading a project's analyzer assemblies is likewise expensive, and cached per project.
    builder.Services.AddSingleton<AnalyzerService>();

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = "csharplang-mcp",
                Version = "2.0.0"
            };
        })
        .WithStdioServerTransport()
        .WithTools<WorkspaceTools>()
        .WithTools<NavigationTools>()
        .WithTools<StructureTools>()
        .WithTools<EditTools>()
        .WithTools<CodeActionTools>()
        .WithTools<DecompileTools>();

    var app = builder.Build();
    await app.RunAsync();
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"MCP Server crashed: {ex.Message}");
    return 1;
}
