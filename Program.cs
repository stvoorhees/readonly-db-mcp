using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReadOnlyDbMcp.Cli;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Connections;
using ReadOnlyDbMcp.Query;
using ReadOnlyDbMcp.Schema;
using ReadOnlyDbMcp.Tools;

// Setup verbs run and exit here, before any MCP plumbing exists. They are CLI-only by
// construction: an agent talking to the running server has no channel to reach them.
if (args.Length > 0 && (args[0] == "init" || args[0] == "doctor"))
{
    try
    {
        return args[0] == "init"
            ? SetupCli.Init(args)
            : await SetupCli.DoctorAsync(args, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"readonly-db: {ex.Message}");
        return 1;
    }
}

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
