using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// MSBuildWorkspace needs a registered MSBuild before any Microsoft.Build type loads.
// Must happen first, and exactly once.
MSBuildLocator.RegisterDefaults();

var builder = Host.CreateApplicationBuilder(args);

// stdio transport: stdout belongs to the MCP protocol, so logs go to stderr
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        // v2 adds the write-side tools (apply_rename, extract_interface) and
        // find_unused_members - bump so clients can tell v1 (read-only) from v2 apart.
        options.ServerInfo = new Implementation { Name = "RoslynMcp", Version = "2.0.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
