using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DebuggerNetMcp.Core.Engine;

var builder = Host.CreateApplicationBuilder(args);

// CRITICAL: all logging must go to stderr — stdout is the MCP wire protocol
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// DotnetDebugger manages a single OS-level debug session with a dedicated COM thread
// — must be singleton so state is preserved across tool calls
builder.Services.AddSingleton<DotnetDebugger>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DebuggerTools>();

await builder.Build().RunAsync();
