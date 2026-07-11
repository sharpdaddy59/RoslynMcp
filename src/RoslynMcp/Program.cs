using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MSBuildWorkspace needs a registered MSBuild before any Microsoft.Build type loads.
// Must happen first, and exactly once.
MSBuildLocator.RegisterDefaults();

var builder = Host.CreateApplicationBuilder(args);

// stdio transport: stdout belongs to the MCP protocol, so logs go to stderr
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
