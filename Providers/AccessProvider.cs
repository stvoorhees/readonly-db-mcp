using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Providers;

/// <summary>
/// Microsoft Access (.mdb/.accdb) via the ACE ODBC driver
/// ("Microsoft Access Driver (*.mdb, *.accdb)"). One connection = one Access file, which is its
/// own catalog. The driver is Windows-only, so this provider only connects on Windows; the build
/// stays cross-platform. Read-only posture mirrors SQL Server: Access has no session read-only
/// mode, so enforcement rests on the structural guarantee (this server only ever authors SELECT
/// statements) plus opening the file read-only or against a copy.
/// </summary>
public sealed class AccessProvider : IDbProvider
{
    public string Kind => "access";

    public DbConnection CreateConnection(string connectionString) => new OdbcConnection(connectionString);

    // No session-level read-only in Access; see class remarks.
    public Task ApplySessionReadOnlyAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;

    // A file opened through the ODBC driver exposes no server privilege model to interrogate.
    public Task<string?> CheckWritePrivilegesAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";

    public string ApplyLimit(string selectSql, int limit, int offset, bool hasOrderBy)
    {
        // Access (Jet/ACE SQL) supports TOP n but has no OFFSET, and there is no robust way to
        // emulate it here without rewriting the ORDER BY. First page works; deeper pages error
        // cleanly rather than silently returning wrong rows.
        if (offset > 0)
            throw new QueryValidationException(
                "This Access connection does not support paging beyond the first page (Access SQL has no OFFSET). " +
                "Narrow the query with filters instead.");
        return $"SELECT TOP {limit}{selectSql["SELECT".Length..]}";
    }

    public ColumnCategory Categorize(string dataType) => dataType.Trim().ToUpperInvariant() switch
    {
        "COUNTER" or "AUTOINCREMENT" or "INTEGER" or "INT" or "LONG" or "SMALLINT" or "TINYINT" or "BYTE"
            or "REAL" or "SINGLE" or "DOUBLE" or "FLOAT" or "DECIMAL" or "NUMERIC" or "CURRENCY" or "MONEY" => ColumnCategory.Numeric,
        "DATETIME" or "DATE" or "TIME" or "TIMESTAMP" => ColumnCategory.DateTime,
        "BIT" or "YESNO" or "LOGICAL" or "BOOLEAN" => ColumnCategory.Boolean,
        "GUID" or "UNIQUEIDENTIFIER" => ColumnCategory.Uuid,
        "LONGBINARY" or "BINARY" or "VARBINARY" or "OLEOBJECT" or "IMAGE" => ColumnCategory.Binary,
        "TEXT" or "CHAR" or "VARCHAR" or "LONGCHAR" or "LONGTEXT" or "MEMO" or "HYPERLINK"
            or "WCHAR" or "WVARCHAR" or "WLONGVARCHAR" => ColumnCategory.Text,
        _ => ColumnCategory.Other,
    };

    // Access has no queryable catalog like sys.*; use the ODBC schema collections instead.
    public Task<SchemaModel> LoadSchemaAsync(DbConnection connection, CancellationToken ct)
    {
        var odbc = (OdbcConnection)connection;
        var model = new SchemaModel();
        var byName = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        DataTable tables = odbc.GetSchema("Tables");
        foreach (DataRow row in tables.Rows)
        {
            var type = Str(row, "TABLE_TYPE");
            var kind = type.Equals("VIEW", StringComparison.OrdinalIgnoreCase) ? "view"
                     : type.Equals("TABLE", StringComparison.OrdinalIgnoreCase) ? "table"
                     : null;
            if (kind is null) continue;                                   // skip SYSTEM TABLE etc.
            var name = Str(row, "TABLE_NAME");
            if (name.Length == 0 || name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)) continue;
            var table = new TableInfo { Schema = "", Name = name, Kind = kind };
            model.Tables.Add(table);
            byName[name] = table;
        }

        DataTable columns = odbc.GetSchema("Columns");
        var ordered = columns.Rows.Cast<DataRow>()
            .OrderBy(r => Str(r, "TABLE_NAME"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => Ordinal(r));
        foreach (DataRow row in ordered)
        {
            if (!byName.TryGetValue(Str(row, "TABLE_NAME"), out var table)) continue;
            table.Columns.Add(new ColumnInfo
            {
                Name = Str(row, "COLUMN_NAME"),
                DataType = Str(row, "TYPE_NAME"),
                IsNullable = IsNullable(row),
            });
        }

        // Access relationships are not exposed through a standard ODBC schema collection; foreign
        // keys are left empty (the server simply won't suggest FK-based joins for this connection).
        return Task.FromResult(model);

        static string Str(DataRow r, string col) =>
            r.Table.Columns.Contains(col) && r[col] is { } v && v != DBNull.Value ? v.ToString() ?? "" : "";
        static int Ordinal(DataRow r) =>
            r.Table.Columns.Contains("ORDINAL_POSITION") && r["ORDINAL_POSITION"] is { } v && v != DBNull.Value
                ? Convert.ToInt32(v) : 0;
        static bool IsNullable(DataRow r)
        {
            if (r.Table.Columns.Contains("IS_NULLABLE") && r["IS_NULLABLE"] is { } s && s != DBNull.Value)
                return (s.ToString() ?? "").Trim().StartsWith("Y", StringComparison.OrdinalIgnoreCase);
            if (r.Table.Columns.Contains("NULLABLE") && r["NULLABLE"] is { } n && n != DBNull.Value)
                return Convert.ToInt32(n) != 0;
            return true;
        }
    }

    // Access query definitions live in hidden system tables (MSysQueries) that a normal reader
    // cannot open, so view definitions are not exposed.
    public string? ViewDefinitionRequiredPrivilege => null;

    public Task<string?> GetViewDefinitionAsync(DbConnection connection, string schema, string name, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
