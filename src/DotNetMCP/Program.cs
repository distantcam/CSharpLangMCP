using DotNetMCP.Lsp;
using DotNetMCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    var logLevel = LogLevel.Warning;
    if (args.Contains("--verbose") || args.Contains("-V"))
        logLevel = LogLevel.Debug;

    builder.Logging.SetMinimumLevel(logLevel);

    builder.Services.AddSingleton<SolutionFilter>();
    builder.Services.AddSingleton<LspClient>();

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = "dotnet-mcp",
                Version = "1.0.0"
            };
        })
        .WithStdioServerTransport()
        .WithTools<CSharpTools>();

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
