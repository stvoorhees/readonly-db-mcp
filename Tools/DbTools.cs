using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Connections;
using ReadOnlyDbMcp.Query;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Tools;

[McpServerToolType]
public sealed class DbTools(ConnectionRegistry registry, SchemaCache schemaCache, QueryExecutor executor, AppConfig appConfig)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "list_connections")]
    [Description("Lists the database connections this server exposes: name and provider only. Connection strings are never available through any tool.")]
    public string ListConnections() =>
        Serialize(registry.List().Select(c => new { name = c.Name, provider = c.Provider.Kind }));

    [McpServerTool(Name = "list_tables")]
    [Description("Lists tables and views on a connection, with column counts. Set refresh=true to reload schema from the database (otherwise cached ~5 minutes).")]
    public async Task<string> ListTables(
        [Description("Connection name from list_connections.")] string connection,
        [Description("Reload schema from the database instead of using the cache.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        return await GuardAsync(async () =>
        {
            var schema = await schemaCache.GetAsync(connection, refresh, cancellationToken);
            return new
            {
                tables = schema.Tables.Select(t => new { table = t.Key, kind = t.Kind, columns = t.Columns.Count }),
            };
        });
    }

    [McpServerTool(Name = "describe_table")]
    [Description("Describes one table or view: columns (name, type, nullable) and foreign keys in both directions. Use foreign keys to plan joins for read_rows.")]
    public async Task<string> DescribeTable(
        [Description("Connection name from list_connections.")] string connection,
        [Description("Table name, optionally schema-qualified, e.g. 'orders' or 'dbo.orders'.")] string table,
        CancellationToken cancellationToken = default)
    {
        return await GuardAsync(async () =>
        {
            var schema = await schemaCache.GetAsync(connection, refresh: false, cancellationToken);
            var info = schema.ResolveTable(table);
            var oic = StringComparison.OrdinalIgnoreCase;
            return new
            {
                table = info.Key,
                kind = info.Kind,
                columns = info.Columns.Select(c => new { name = c.Name, type = c.DataType, nullable = c.IsNullable }),
                foreignKeysOut = schema.ForeignKeys
                    .Where(fk => fk.FromTable.Equals(info.Key, oic))
                    .Select(fk => new
                    {
                        name = fk.Name,
                        columns = fk.ColumnPairs.Select(p => p.From),
                        referencesTable = fk.ToTable,
                        referencedColumns = fk.ColumnPairs.Select(p => p.To),
                    }),
                foreignKeysIn = schema.ForeignKeys
                    .Where(fk => fk.ToTable.Equals(info.Key, oic))
                    .Select(fk => new
                    {
                        name = fk.Name,
                        fromTable = fk.FromTable,
                        fromColumns = fk.ColumnPairs.Select(p => p.From),
                        referencedColumns = fk.ColumnPairs.Select(p => p.To),
                    }),
            };
        });
    }

    [McpServerTool(Name = "read_rows")]
    [Description("""
        Reads rows via a structured, read-only query. The server constructs and parameterizes all SQL;
        raw SQL is never accepted. Supports joins (inner/left; 'on' may be omitted when exactly one
        foreign key relates the tables), filters (=, !=, <, <=, >, >=, in, not_in, like, not_like,
        is_null, is_not_null; filters are AND-combined), aggregates (count/sum/avg/min/max with
        groupBy; non-aggregated columns must appear in groupBy), orderBy, limit, and offset
        (offset requires orderBy). Column references are 'column' or 'alias.column', where alias is
        the table name or the join's 'as' value. Omit columns/aggregates to select every column.
        Row count is capped server-side; the response reports the generated SQL and whether results
        were truncated.
        """)]
    public async Task<string> ReadRows(ReadRowsRequest request, CancellationToken cancellationToken = default)
    {
        return await GuardAsync(async () =>
        {
            var exposed = registry.Get(request.Connection);
            var schema = await schemaCache.GetAsync(request.Connection, refresh: false, cancellationToken);
            var built = new QueryBuilder(exposed.Provider, schema, appConfig.File).Build(request);
            var result = await executor.ExecuteAsync(request.Connection, built, cancellationToken);
            return new
            {
                sql = result.Sql,
                columns = result.Columns,
                rows = result.Rows,
                rowCount = result.Rows.Count,
                truncated = result.Truncated,
            };
        });
    }

    [McpServerTool(Name = "count_rows")]
    [Description("Counts rows matching optional filters (same filter/join syntax as read_rows). Convenience wrapper over a COUNT(*) aggregate.")]
    public async Task<string> CountRows(
        [Description("Connection name from list_connections.")] string connection,
        [Description("Table name, optionally schema-qualified.")] string from,
        [Description("Optional joins, same shape as read_rows joins.")] List<JoinSpec>? joins = null,
        [Description("Optional filters, same shape as read_rows filters.")] List<FilterSpec>? filters = null,
        CancellationToken cancellationToken = default)
    {
        return await GuardAsync(async () =>
        {
            var request = new ReadRowsRequest
            {
                Connection = connection,
                From = from,
                Joins = joins,
                Filters = filters,
                Aggregates = [new AggregateSpec { Fn = "count", Column = "*", Alias = "count_all" }],
                Limit = 1,
            };
            var exposed = registry.Get(connection);
            var schema = await schemaCache.GetAsync(connection, refresh: false, cancellationToken);
            var built = new QueryBuilder(exposed.Provider, schema, appConfig.File).Build(request);
            var result = await executor.ExecuteAsync(connection, built, cancellationToken);
            return new { sql = result.Sql, count = result.Rows.Count > 0 ? result.Rows[0][0] : 0 };
        });
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    private static async Task<string> GuardAsync(Func<Task<object>> action)
    {
        try
        {
            return Serialize(await action());
        }
        catch (QueryValidationException ex)
        {
            return Serialize(new { error = ex.Message });
        }
        catch (DbException ex)
        {
            return Serialize(new { error = $"Database error: {ex.Message}" });
        }
    }
}
