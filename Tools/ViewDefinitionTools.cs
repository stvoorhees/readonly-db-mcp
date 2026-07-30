using System.ComponentModel;
using ModelContextProtocol.Server;
using ReadOnlyDbMcp.Connections;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Tools;

/// <summary>
/// Registered by Program.cs only when at least one exposed connection sets
/// exposeViewDefinitions — otherwise the tool does not exist as far as agents are concerned.
/// </summary>
[McpServerToolType]
public sealed class ViewDefinitionTools(ConnectionRegistry registry, SchemaCache schemaCache)
{
    [McpServerTool(Name = "get_view_definition")]
    [Description("""
        Returns the SQL a view is defined as (its SELECT). Call only when the view's underlying
        SQL is specifically needed — for general questions about a view's contents, describe_table
        and read_rows are sufficient. Views only; for tables, describe_table shows the structure.
        """)]
    public async Task<string> GetViewDefinition(
        [Description("Connection name from list_connections. Required.")] string? connection = null,
        [Description("View name, optionally schema-qualified, e.g. 'v_order_summary' or 'dbo.v_order_summary'. Required.")] string? view = null,
        [Description("Alias for 'view'; use one or the other.")] string? table = null,
        CancellationToken cancellationToken = default)
    {
        return await DbTools.GuardAsync(async () =>
        {
            var conn = DbTools.Require(connection, "connection", "get_view_definition");
            var name = DbTools.Require(view ?? table, "view", "get_view_definition");
            var exposed = registry.Get(conn);
            if (!exposed.ExposeViewDefinitions)
                throw new QueryValidationException(
                    $"View definitions are not enabled for connection '{conn}'. This is a deliberate configuration " +
                    "choice (exposeViewDefinitions in the server's config file). All data tools work normally; " +
                    "do not mention this unless the user asked for a view's definition.");

            var schema = await schemaCache.GetAsync(conn, refresh: false, cancellationToken);
            var info = schema.ResolveTable(name);
            if (info.Kind != "view")
                throw new QueryValidationException(
                    $"'{info.Key}' is a {info.Kind}, not a view. Use describe_table for its structure.");

            await using var dbConnection = await registry.OpenAsync(conn, cancellationToken);
            var definition = await exposed.Provider.GetViewDefinitionAsync(dbConnection, info.Schema, info.Name, cancellationToken);
            if (definition is not null)
                return new { view = info.Key, definition };

            var hint = exposed.Provider.ViewDefinitionRequiredPrivilege is { } privilege
                ? $"On {exposed.Provider.Kind} the credential needs the {privilege} privilege to read view definitions — " +
                  "a metadata-only grant that does not weaken read-only enforcement. All data tools work normally; " +
                  "do not mention this unless the user asked for a view's definition."
                : "The engine returned no definition for this view.";
            return (object)new { view = info.Key, definition = (string?)null, error = $"No definition available. {hint}" };
        });
    }
}
