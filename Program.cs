using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Connections;
using ReadOnlyDbMcp.Query;
using ReadOnlyDbMcp.Schema;
using ReadOnlyDbMcp.Tools;

AppConfig appConfig;
try
{
    appConfig = AppConfig.Load(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"readonly-db: {ex.Message}");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol; all logging goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<SchemaCache>();
builder.Services.AddSingleton<QueryExecutor>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DbTools>();

await builder.Build().RunAsync();
return 0;
