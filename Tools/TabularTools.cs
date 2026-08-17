using System.ComponentModel;
using ModelContextProtocol.Server;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Schema;
using ReadOnlyDbMcp.Tabular;

namespace ReadOnlyDbMcp.Tools;

[McpServerToolType]
public sealed class TabularTools(TabularConnectionRegistry registry, TabularSchemaCache schemaCache, TabularQueryExecutor executor, AppConfig config)
{
    [McpServerTool(Name = "list_tabular_tables")]
    [Description("Lists the visible tables in an exposed SSAS tabular model, including column and measure counts. Set refresh=true to reload model metadata.")]
    public async Task<string> ListTabularTables(
        [Description("SSAS connection name from list_connections.")] string connection,
        [Description("Reload model metadata instead of using the five-minute cache.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        return await DbTools.GuardAsync(async () =>
        {
            var model = await schemaCache.GetAsync(connection, refresh, cancellationToken);
            return new
            {
                tables = model.Tables.Where(table => !table.IsHidden).Select(table => new
                {
                    name = table.Name,
                    columns = table.Columns.Count(column => !column.IsHidden),
                    measures = model.Measures.Count(measure => measure.TableId == table.Id && !measure.IsHidden),
                }),
                unassignedMeasures = model.Measures.Count(measure => measure.TableId.Length == 0 && !measure.IsHidden),
            };
        });
    }

    [McpServerTool(Name = "describe_tabular_table")]
    [Description("Describes one SSAS tabular-model table: visible columns, measures, and direct relationships.")]
    public async Task<string> DescribeTabularTable(
        [Description("SSAS connection name from list_connections.")] string connection,
        [Description("Tabular-model table name from list_tabular_tables.")] string table,
        CancellationToken cancellationToken = default)
    {
        return await DbTools.GuardAsync(async () =>
        {
            var model = await schemaCache.GetAsync(connection, refresh: false, cancellationToken);
            var info = model.ResolveTable(table);
            var tables = model.Tables.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var columns = model.Tables.SelectMany(item => item.Columns.Select(column => (item.Id, column))).ToDictionary(item => item.column.Id, item => item, StringComparer.OrdinalIgnoreCase);
            return new
            {
                table = info.Name,
                columns = info.Columns.Where(column => !column.IsHidden).Select(column => new { name = column.Name, type = column.DataType }),
                measures = model.Measures.Where(measure => measure.TableId == info.Id && !measure.IsHidden).Select(measure => new { name = measure.Name }),
                unassignedMeasures = model.Measures.Where(measure => measure.TableId.Length == 0 && !measure.IsHidden).Select(measure => new { name = measure.Name }),
                relationships = model.Relationships
                    .Where(relationship => relationship.FromTableId == info.Id || relationship.ToTableId == info.Id)
                    .Select(relationship => new
                    {
                        fromTable = tables.GetValueOrDefault(relationship.FromTableId)?.Name,
                        fromColumn = columns.GetValueOrDefault(relationship.FromColumnId).column?.Name,
                        toTable = tables.GetValueOrDefault(relationship.ToTableId)?.Name,
                        toColumn = columns.GetValueOrDefault(relationship.ToColumnId).column?.Name,
                        active = relationship.IsActive,
                    }),
            };
        });
    }

    [McpServerTool(Name = "read_tabular_rows")]
    [Description("""
        Reads a bounded result from an SSAS tabular model. The server constructs DAX from
        metadata-validated table/column/measure names and typed filter values; raw DAX, MDX, and
        XMLA are never accepted. Select columns for model rows, optionally measures for aggregates
        at the selected column granularity. Filters support '=' and 'in' only and apply to the
        selected table. Results are capped by the server's configured row limit.
        """)]
    public async Task<string> ReadTabularRows(
        [Description("SSAS connection name from list_connections.")] string? connection = null,
        [Description("Tabular-model table name. Required.")] string? table = null,
        [Description("Visible column names to return and, when measures are included, group by.")] List<string>? columns = null,
        [Description("Visible measure names to calculate at the selected column granularity.")] List<string>? measures = null,
        [Description("Optional '=' or 'in' filters on columns of the selected table.")] List<TabularFilter>? filters = null,
        [Description("Optional sort by a selected column or measure.")] TabularOrder? orderBy = null,
        [Description("Maximum rows to return; capped server-side.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return await DbTools.GuardAsync(async () =>
        {
            var request = new TabularReadRequest
            {
                Connection = DbTools.Require(connection, "connection", "read_tabular_rows"),
                Table = DbTools.Require(table, "table", "read_tabular_rows"),
                Columns = columns,
                Measures = measures,
                Filters = filters,
                OrderBy = orderBy,
                Limit = limit,
            };
            registry.Get(request.Connection);
            var model = await schemaCache.GetAsync(request.Connection, refresh: false, cancellationToken);
            var built = new TabularDaxBuilder(config.File, model).Build(request);
            var result = await executor.ExecuteAsync(request.Connection, built, cancellationToken);
            return new { columns = result.Columns, rows = result.Rows, rowCount = result.Rows.Count, truncated = result.Truncated };
        });
    }
}
