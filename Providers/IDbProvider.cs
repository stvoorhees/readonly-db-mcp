using System.Data.Common;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Providers;

public enum ColumnCategory { Text, Numeric, DateTime, Boolean, Uuid, Binary, Other }

/// <summary>
/// Engine-specific behavior. The security-relevant contract: QuoteIdentifier must produce an
/// identifier the engine cannot interpret as anything else, ApplySessionReadOnly must make the
/// session reject writes where the engine supports it, and ApplyLimit only ever receives
/// server-computed integers.
/// </summary>
public interface IDbProvider
{
    string Kind { get; }

    DbConnection CreateConnection(string connectionString);

    /// <summary>Best-effort engine-level read-only for the session. No-op where unsupported (SQL Server).</summary>
    Task ApplySessionReadOnlyAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Returns a human-readable warning if the credential can write, else null. Best effort.</summary>
    Task<string?> CheckWritePrivilegesAsync(DbConnection connection, CancellationToken ct);

    string QuoteIdentifier(string name);

    /// <summary>Applies limit/offset to a complete SELECT statement. limit/offset are server-computed integers.</summary>
    string ApplyLimit(string selectSql, int limit, int offset, bool hasOrderBy);

    ColumnCategory Categorize(string dataType);

    Task<SchemaModel> LoadSchemaAsync(DbConnection connection, CancellationToken ct);

    /// <summary>
    /// Returns the view's defining SQL, or null if unavailable. schema/name come from the
    /// introspected schema model (already validated), and are passed as bound parameters.
    /// </summary>
    Task<string?> GetViewDefinitionAsync(DbConnection connection, string schema, string name, CancellationToken ct);

    /// <summary>
    /// The metadata privilege a credential needs before GetViewDefinitionAsync returns anything,
    /// or null if the engine exposes definitions to any reader. Used in error messages.
    /// </summary>
    string? ViewDefinitionRequiredPrivilege { get; }
}
