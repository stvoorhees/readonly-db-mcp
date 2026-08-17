using System.Collections.Concurrent;
using ReadOnlyDbMcp.Config;

namespace ReadOnlyDbMcp.Tabular;

public sealed class TabularSchemaCache(TabularConnectionRegistry registry, AppConfig config)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (DateTimeOffset LoadedAt, TabularModel Model)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<TabularModel> GetAsync(string connectionName, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _cache.TryGetValue(connectionName, out var entry) && DateTimeOffset.UtcNow - entry.LoadedAt < Ttl)
            return entry.Model;

        var connection = registry.Get(connectionName);
        var model = await TabularSchemaLoader.LoadAsync(connection.ConnectionString, config.File.CommandTimeoutSeconds, cancellationToken);
        _cache[connectionName] = (DateTimeOffset.UtcNow, model);
        return model;
    }
}
