using System.Data.Common;
using Npgsql;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Providers;

public sealed class PostgresProvider : IDbProvider
{
    public string Kind => "postgres";

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public async Task ApplySessionReadOnlyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> CheckWritePrivilegesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE((SELECT rolsuper FROM pg_roles WHERE rolname = current_user), false),
                   COALESCE((SELECT bool_or(privilege_type IN ('INSERT','UPDATE','DELETE','TRUNCATE'))
                             FROM information_schema.role_table_grants
                             WHERE grantee = current_user), false)
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            if (reader.GetBoolean(0))
                return "credential is a Postgres superuser";
            if (reader.GetBoolean(1))
                return "credential holds INSERT/UPDATE/DELETE/TRUNCATE grants on one or more tables";
        }
        return null;
    }

    public string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public string ApplyLimit(string selectSql, int limit, int offset, bool hasOrderBy) =>
        offset > 0 ? $"{selectSql} LIMIT {limit} OFFSET {offset}" : $"{selectSql} LIMIT {limit}";

    public ColumnCategory Categorize(string dataType)
    {
        var t = dataType.ToLowerInvariant();
        if (t is "integer" or "bigint" or "smallint" or "numeric" or "decimal" or "real" or "double precision" or "money")
            return ColumnCategory.Numeric;
        if (t.StartsWith("timestamp") || t is "date" || t.StartsWith("time"))
            return ColumnCategory.DateTime;
        if (t is "boolean")
            return ColumnCategory.Boolean;
        if (t is "uuid")
            return ColumnCategory.Uuid;
        if (t is "bytea")
            return ColumnCategory.Binary;
        if (t.StartsWith("character") || t is "text" or "citext" or "name")
            return ColumnCategory.Text;
        return ColumnCategory.Other;
    }

    public async Task<string?> GetViewDefinitionAsync(DbConnection connection, string schema, string name, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT pg_get_viewdef(c.oid, true)
            FROM pg_class c
            JOIN pg_namespace n ON c.relnamespace = n.oid
            WHERE n.nspname = @schema AND c.relname = @name AND c.relkind IN ('v', 'm')
            """;
        var pSchema = cmd.CreateParameter();
        pSchema.ParameterName = "@schema";
        pSchema.Value = schema;
        cmd.Parameters.Add(pSchema);
        var pName = cmd.CreateParameter();
        pName.ParameterName = "@name";
        pName.Value = name;
        cmd.Parameters.Add(pName);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<SchemaModel> LoadSchemaAsync(DbConnection connection, CancellationToken ct)
    {
        var model = new SchemaModel();
        var tablesByKey = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT table_schema, table_name, table_type
                FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog','information_schema')
                  AND table_type IN ('BASE TABLE','VIEW')
                ORDER BY table_schema, table_name
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = new TableInfo
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    Kind = reader.GetString(2) == "VIEW" ? "view" : "table",
                };
                model.Tables.Add(table);
                tablesByKey[table.Key] = table;
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT table_schema, table_name, column_name, data_type, is_nullable
                FROM information_schema.columns
                WHERE table_schema NOT IN ('pg_catalog','information_schema')
                ORDER BY table_schema, table_name, ordinal_position
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (tablesByKey.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table))
                    table.Columns.Add(new ColumnInfo
                    {
                        Name = reader.GetString(2),
                        DataType = reader.GetString(3),
                        IsNullable = reader.GetString(4) == "YES",
                    });
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT con.conname,
                       nsp.nspname, rel.relname,
                       fnsp.nspname, frel.relname,
                       att.attname, fatt.attname
                FROM pg_constraint con
                JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS u(fromnum, tonum, ord) ON true
                JOIN pg_class rel ON rel.oid = con.conrelid
                JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                JOIN pg_class frel ON frel.oid = con.confrelid
                JOIN pg_namespace fnsp ON fnsp.oid = frel.relnamespace
                JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = u.fromnum
                JOIN pg_attribute fatt ON fatt.attrelid = con.confrelid AND fatt.attnum = u.tonum
                WHERE con.contype = 'f'
                ORDER BY con.conname, u.ord
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            ForeignKeyInfo? current = null;
            while (await reader.ReadAsync(ct))
            {
                var fkName = reader.GetString(0);
                var fromTable = $"{reader.GetString(1)}.{reader.GetString(2)}";
                if (current is null || current.Name != fkName || current.FromTable != fromTable)
                {
                    current = new ForeignKeyInfo
                    {
                        Name = fkName,
                        FromTable = fromTable,
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
