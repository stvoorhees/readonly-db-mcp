using System.Data.Common;
using System.Text.RegularExpressions;
using MySqlConnector;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Providers;

public sealed partial class MySqlProvider : IDbProvider
{
    public string Kind => "mysql";

    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public async Task ApplySessionReadOnlyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SET SESSION transaction_read_only = 1";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> CheckWritePrivilegesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SHOW GRANTS FOR CURRENT_USER()";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var grant = reader.GetString(0);
            if (WriteGrantPattern().IsMatch(grant))
                return $"credential holds write grants: {grant}";
        }
        return null;
    }

    [GeneratedRegex(@"\b(ALL PRIVILEGES|INSERT|UPDATE|DELETE|DROP|ALTER|CREATE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WriteGrantPattern();

    public string QuoteIdentifier(string name) => "`" + name.Replace("`", "``") + "`";

    public string ApplyLimit(string selectSql, int limit, int offset, bool hasOrderBy) =>
        offset > 0 ? $"{selectSql} LIMIT {limit} OFFSET {offset}" : $"{selectSql} LIMIT {limit}";

    public ColumnCategory Categorize(string dataType)
    {
        var t = dataType.ToLowerInvariant();
        if (t is "int" or "bigint" or "smallint" or "mediumint" or "tinyint" or "decimal" or "numeric" or "float" or "double")
            return ColumnCategory.Numeric;
        if (t is "date" or "datetime" or "timestamp" or "time" or "year")
            return ColumnCategory.DateTime;
        if (t is "bit" or "bool" or "boolean")
            return ColumnCategory.Boolean;
        if (t is "binary" or "varbinary" or "blob" or "tinyblob" or "mediumblob" or "longblob")
            return ColumnCategory.Binary;
        if (t is "char" or "varchar" or "text" or "tinytext" or "mediumtext" or "longtext" or "enum" or "set")
            return ColumnCategory.Text;
        return ColumnCategory.Other;
    }

    public async Task<string?> GetViewDefinitionAsync(DbConnection connection, string schema, string name, CancellationToken ct)
    {
        // Schema is always "" for MySQL (introspection is scoped to DATABASE()).
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT VIEW_DEFINITION
            FROM information_schema.VIEWS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @name
            """;
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
            // Scoped to the current database; Schema stays empty so table keys are bare names.
            cmd.CommandText = """
                SELECT table_name, table_type
                FROM information_schema.tables
                WHERE table_schema = DATABASE()
                ORDER BY table_name
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var table = new TableInfo
                {
                    Schema = "",
                    Name = reader.GetString(0),
                    Kind = reader.GetString(1) == "VIEW" ? "view" : "table",
                };
                model.Tables.Add(table);
                tablesByKey[table.Key] = table;
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT table_name, column_name, data_type, is_nullable
                FROM information_schema.columns
                WHERE table_schema = DATABASE()
                ORDER BY table_name, ordinal_position
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (tablesByKey.TryGetValue(reader.GetString(0), out var table))
                    table.Columns.Add(new ColumnInfo
                    {
                        Name = reader.GetString(1),
                        DataType = reader.GetString(2),
                        IsNullable = reader.GetString(3) == "YES",
                    });
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT constraint_name, table_name, column_name, referenced_table_name, referenced_column_name
                FROM information_schema.key_column_usage
                WHERE table_schema = DATABASE() AND referenced_table_name IS NOT NULL
                ORDER BY constraint_name, ordinal_position
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            ForeignKeyInfo? current = null;
            while (await reader.ReadAsync(ct))
            {
                var fkName = reader.GetString(0);
                var fromTable = reader.GetString(1);
                if (current is null || current.Name != fkName || current.FromTable != fromTable)
                {
                    current = new ForeignKeyInfo
                    {
                        Name = fkName,
                        FromTable = fromTable,
                        ToTable = reader.GetString(3),
                    };
                    model.ForeignKeys.Add(current);
                }
                current.ColumnPairs.Add((reader.GetString(2), reader.GetString(4)));
            }
        }

        return model;
    }
}
