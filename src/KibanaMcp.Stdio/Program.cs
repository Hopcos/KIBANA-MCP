using KibanaMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddKibanaMcpCore();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithKibanaLogTools();

// The default stdio transport buffers output in JSON-RPC frames with Content-Length chunks, which
// some MCP clients do not parse. Replace it with a newline-delimited JSON transport so messages
// sent over stdout survive in any consumer.
builder.Services.RemoveAll<ITransport>();
builder.Services.AddSingleton<ITransport>(services =>
{
    IOptions<McpServerOptions> serverOptions = services.GetRequiredService<IOptions<McpServerOptions>>();
    ILoggerFactory? loggerFactory = services.GetService<ILoggerFactory>();
    string serverName = serverOptions.Value.ServerInfo?.Name ?? "KibanaMcp";
    return new RelaxedJsonStdioServerTransport(serverName, loggerFactory);
});

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

await builder.Build().RunAsync();
