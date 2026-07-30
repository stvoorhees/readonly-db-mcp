using System.Data.Common;
using Microsoft.Data.SqlClient;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Providers;

public sealed class SqlServerProvider : IDbProvider
{
    public string Kind => "sqlserver";

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    // SQL Server has no session-level read-only mode. Enforcement rests on the structural
    // guarantee (this server authors all SQL and only ever authors SELECTs) plus a
    // db_datareader-only credential, which CheckWritePrivilegesAsync nags about.
    public Task ApplySessionReadOnlyAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;

    public async Task<string?> CheckWritePrivilegesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CAST(ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS int),
                   (SELECT COUNT(*) FROM fn_my_permissions(NULL, 'DATABASE')
                    WHERE permission_name IN ('INSERT','UPDATE','DELETE','ALTER','CONTROL'))
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            if (reader.GetInt32(0) == 1)
                return "credential is a member of the sysadmin server role";
            var writePerms = reader.GetInt32(1);
            if (writePerms > 0)
                return $"credential holds {writePerms} database-level write permission(s) (INSERT/UPDATE/DELETE/ALTER/CONTROL)";
        }
        return null;
    }

    public string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";

    public string ApplyLimit(string selectSql, int limit, int offset, bool hasOrderBy)
    {
        if (offset > 0)
            return $"{selectSql} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"; // builder guarantees ORDER BY when offset > 0
        return $"SELECT TOP ({limit}){selectSql["SELECT".Length..]}";
    }

    public ColumnCategory Categorize(string dataType) => dataType.ToLowerInvariant() switch
    {
        "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric"
            or "money" or "smallmoney" or "float" or "real" => ColumnCategory.Numeric,
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" => ColumnCategory.DateTime,
        "bit" => ColumnCategory.Boolean,
        "uniqueidentifier" => ColumnCategory.Uuid,
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => ColumnCategory.Binary,
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" => ColumnCategory.Text,
        _ => ColumnCategory.Other,
    };

    public async Task<string?> GetViewDefinitionAsync(DbConnection connection, string schema, string name, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON m.object_id = o.object_id
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @name AND o.type = 'V'
            """;
        AddParameter(cmd, "@schema", schema);
        AddParameter(cmd, "@name", name);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static void AddParameter(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    public async Task<SchemaModel> LoadSchemaAsync(DbConnection connection, CancellationToken ct)
    {
        var model = new SchemaModel();
        var tablesByKey = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.name, o.name, o.type
                FROM sys.objects o JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
                ORDER BY s.name, o.name
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = new TableInfo
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    Kind = reader.GetString(2).Trim() == "V" ? "view" : "table",
                };
                model.Tables.Add(table);
                tablesByKey[table.Key] = table;
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.name, o.name, c.name, t.name, c.is_nullable
                FROM sys.columns c
                JOIN sys.objects o ON c.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                JOIN sys.types t ON c.user_type_id = t.user_type_id
                WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
                ORDER BY s.name, o.name, c.column_id
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (tablesByKey.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table))
                    table.Columns.Add(new ColumnInfo
                    {
                        Name = reader.GetString(2),
                        DataType = reader.GetString(3),
                        IsNullable = reader.GetBoolean(4),
                    });
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fk.name, s1.name, t1.name, s2.name, t2.name, c1.name, c2.name
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
                JOIN sys.tables t1 ON fkc.parent_object_id = t1.object_id
                JOIN sys.schemas s1 ON t1.schema_id = s1.schema_id
                JOIN sys.tables t2 ON fkc.referenced_object_id = t2.object_id
                JOIN sys.schemas s2 ON t2.schema_id = s2.schema_id
                JOIN sys.columns c1 ON fkc.parent_object_id = c1.object_id AND fkc.parent_column_id = c1.column_id
                JOIN sys.columns c2 ON fkc.referenced_object_id = c2.object_id AND fkc.referenced_column_id = c2.column_id
                ORDER BY fk.name, fkc.constraint_column_id
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            ForeignKeyInfo? current = null;
            while (await reader.ReadAsync(ct))
            {
                var fkName = reader.GetString(0);
                if (current is null || current.Name != fkName)
                {
                    current = new ForeignKeyInfo
                    {
                        Name = fkName,
                        FromTable = $"{reader.GetString(1)}.{reader.GetString(2)}",
                        ToTable = $"{reader.GetString(3)}.{reader.GetString(4)}",
                    };
                    model.ForeignKeys.Add(current);
                }
                current.ColumnPairs.Add((reader.GetString(5), reader.GetString(6)));
            }
        }

        return model;
    }
}
