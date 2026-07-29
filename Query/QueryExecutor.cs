using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Connections;

namespace ReadOnlyDbMcp.Query;

public sealed record ReadResult(List<string> Columns, List<object?[]> Rows, bool Truncated, string Sql);

public sealed class QueryExecutor(ConnectionRegistry registry, AppConfig config)
{
    private const int MaxCellChars = 4000;

    public async Task<ReadResult> ExecuteAsync(string connectionName, BuiltQuery query, CancellationToken ct)
    {
        await using var connection = await registry.OpenAsync(connectionName, ct);
        // Belt on top of session read-only: everything runs in a transaction that is always rolled back.
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = query.Sql;
            command.CommandTimeout = config.File.CommandTimeoutSeconds;
            foreach (var (name, value) in query.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            var rows = new List<object?[]>();
            var truncated = false;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= query.EffectiveLimit)
                {
                    truncated = true;
                    break;
                }
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = Sanitize(reader.GetValue(i));
                rows.Add(row);
            }

            return new ReadResult(query.OutputColumns, rows, truncated, query.Sql);
        }
        finally
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* already closed/rolled back */ }
        }
    }

    private static object? Sanitize(object value) => value switch
    {
        DBNull => null,
        DateTime dt => dt.ToString("o"),
        DateTimeOffset dto => dto.ToString("o"),
        TimeSpan ts => ts.ToString(),
        DateOnly d => d.ToString("o"),
        TimeOnly t => t.ToString("o"),
        Guid g => g.ToString(),
        byte[] bytes => bytes.Length <= 64
            ? Convert.ToBase64String(bytes)
            : $"<binary, {bytes.Length} bytes>",
        string s => s.Length > MaxCellChars ? s[..MaxCellChars] + "…[truncated]" : s,
        bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        _ => value.ToString(),
    };
}
