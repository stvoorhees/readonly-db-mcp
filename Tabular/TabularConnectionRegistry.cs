using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Extensions.Logging;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Tabular;

public sealed class ExposedTabularConnection(string name, string connectionString)
{
    public string Name { get; } = name;
    public string Provider => "ssas";
    internal string ConnectionString { get; } = connectionString;
}

/// <summary>
/// SSAS uses ADOMD rather than the relational DbConnection executor. Keeping it separate
/// prevents DAX and model metadata from inheriting SQL-server assumptions.
/// </summary>
public sealed class TabularConnectionRegistry
{
    private readonly Dictionary<string, ExposedTabularConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    public TabularConnectionRegistry(AppConfig config, ILogger<TabularConnectionRegistry> log)
    {
        foreach (var name in config.ExposedNames)
        {
            var connection = config.File.Connections[name];
            if (!IsTabularProvider(connection.Provider))
                continue;

            var (_, connectionString) = Resolve(name, connection);
            _connections[name] = new ExposedTabularConnection(name, connectionString);
        }
    }

    public static bool IsTabularProvider(string provider) =>
        provider.Trim().Equals("ssas", StringComparison.OrdinalIgnoreCase) ||
        provider.Trim().Equals("tabular", StringComparison.OrdinalIgnoreCase);

    public static (string Provider, string ConnectionString) Resolve(string name, ConnectionConfig config)
    {
        if (!IsTabularProvider(config.Provider))
            throw new InvalidOperationException($"Connection '{name}': unknown tabular provider '{config.Provider}'. Use ssas.");

        var connectionString = config.ConnectionString;
        if (connectionString is null && config.ConnectionStringEnv is { } environmentVariable)
            connectionString = Environment.GetEnvironmentVariable(environmentVariable)
                ?? throw new InvalidOperationException($"Connection '{name}': environment variable '{environmentVariable}' is not set.");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Connection '{name}': set connectionString or connectionStringEnv in the config file.");

        return ("ssas", connectionString);
    }

    public IReadOnlyCollection<ExposedTabularConnection> List() => _connections.Values;

    public ExposedTabularConnection Get(string name) =>
        _connections.TryGetValue(name, out var connection)
            ? connection
            : throw new QueryValidationException($"Unknown SSAS connection '{name}'. Use list_connections to see exposed connections.");

    internal AdomdConnection Open(string name)
    {
        var exposed = Get(name);
        var connection = new AdomdConnection(exposed.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
