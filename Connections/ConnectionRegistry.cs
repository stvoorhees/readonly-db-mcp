using System.Data.Common;
using Microsoft.Extensions.Logging;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Providers;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Connections;

public sealed class ExposedConnection(string name, IDbProvider provider, string connectionString)
{
    public string Name { get; } = name;
    public IDbProvider Provider { get; } = provider;
    // Internal on purpose: no tool ever reads or returns this.
    internal string ConnectionString { get; } = connectionString;
    internal int PrivilegesChecked; // 0 = not yet; set via Interlocked
}

/// <summary>
/// The only path from a connection name to an open DbConnection. Holds exactly the connections
/// allowlisted via --connections; everything else in the config file does not exist as far as
/// tools are concerned.
/// </summary>
public sealed class ConnectionRegistry
{
    private static readonly IDbProvider[] Providers = [new SqlServerProvider(), new PostgresProvider(), new MySqlProvider()];

    private readonly Dictionary<string, ExposedConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ConnectionRegistry> _log;

    public ConnectionRegistry(AppConfig config, ILogger<ConnectionRegistry> log)
    {
        _log = log;
        foreach (var name in config.ExposedNames)
        {
            var cc = config.File.Connections[name];
            var provider = ResolveProvider(cc.Provider)
                ?? throw new InvalidOperationException(
                    $"Connection '{name}': unknown provider '{cc.Provider}'. Use sqlserver, postgres, or mysql.");

            var connectionString = cc.ConnectionString;
            if (connectionString is null && cc.ConnectionStringEnv is { } env)
                connectionString = Environment.GetEnvironmentVariable(env)
                    ?? throw new InvalidOperationException(
                        $"Connection '{name}': environment variable '{env}' is not set.");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    $"Connection '{name}': set connectionString or connectionStringEnv in the config file.");

            _connections[name] = new ExposedConnection(name, provider, connectionString);
        }
    }

    private static IDbProvider? ResolveProvider(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "sqlserver" or "mssql" or "sql-server" => Providers[0],
        "postgres" or "postgresql" or "pg" => Providers[1],
        "mysql" or "mariadb" => Providers[2],
        _ => null,
    };

    public IReadOnlyCollection<ExposedConnection> List() => _connections.Values;

    public ExposedConnection Get(string name) =>
        _connections.TryGetValue(name, out var c)
            ? c
            : throw new QueryValidationException(
                $"Unknown connection '{name}'. Available: {string.Join(", ", _connections.Keys)}.");

    public async Task<DbConnection> OpenAsync(string name, CancellationToken ct)
    {
        var exposed = Get(name);
        var connection = exposed.Provider.CreateConnection(exposed.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await exposed.Provider.ApplySessionReadOnlyAsync(connection, ct);

            if (Interlocked.Exchange(ref exposed.PrivilegesChecked, 1) == 0)
            {
                try
                {
                    var warning = await exposed.Provider.CheckWritePrivilegesAsync(connection, ct);
                    if (warning is not null)
                        _log.LogWarning(
                            "Connection '{Name}': {Warning}. The server still only constructs SELECT statements, " +
                            "but a read-only credential is strongly recommended as defense in depth.",
                            name, warning);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Privilege check failed for connection '{Name}'.", name);
                }
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
