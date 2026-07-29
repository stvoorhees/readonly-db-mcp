using System.Collections.Concurrent;
using ReadOnlyDbMcp.Connections;

namespace ReadOnlyDbMcp.Schema;

public sealed class SchemaCache(ConnectionRegistry registry)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (DateTimeOffset LoadedAt, SchemaModel Model)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<SchemaModel> GetAsync(string connectionName, bool refresh, CancellationToken ct)
    {
        if (!refresh && _cache.TryGetValue(connectionName, out var entry) &&
            DateTimeOffset.UtcNow - entry.LoadedAt < Ttl)
            return entry.Model;

        var exposed = registry.Get(connectionName);
        await using var connection = await registry.OpenAsync(connectionName, ct);
        var model = await exposed.Provider.LoadSchemaAsync(connection, ct);
        _cache[connectionName] = (DateTimeOffset.UtcNow, model);
        return model;
    }
}
