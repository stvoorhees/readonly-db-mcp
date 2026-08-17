using Microsoft.AnalysisServices.AdomdClient;
using ReadOnlyDbMcp.Config;

namespace ReadOnlyDbMcp.Tabular;

public sealed record TabularReadResult(List<string> Columns, List<object?[]> Rows, bool Truncated);

public sealed class TabularQueryExecutor(TabularConnectionRegistry registry, AppConfig config)
{
    private const int MaxCellChars = 4_000;

    public Task<TabularReadResult> ExecuteAsync(string connectionName, BuiltTabularQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = registry.Open(connectionName);
        using var command = connection.CreateCommand();
        command.CommandText = query.Dax;
        command.CommandTimeout = config.File.CommandTimeoutSeconds;
        using var reader = command.ExecuteReader();

        var columns = query.OutputColumns;
        var rows = new List<object?[]>();
        var truncated = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count >= query.EffectiveLimit)
            {
                truncated = true;
                break;
            }
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
                row[index] = Sanitize(reader.IsDBNull(index) ? null : reader.GetValue(index));
            rows.Add(row);
        }

        return Task.FromResult(new TabularReadResult(columns, rows, truncated));
    }

    private static object? Sanitize(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dateTime => dateTime.ToString("o"),
        DateTimeOffset offset => offset.ToString("o"),
        TimeSpan span => span.ToString(),
        byte[] bytes => bytes.Length <= 64 ? Convert.ToBase64String(bytes) : $"<binary, {bytes.Length} bytes>",
        string text => text.Length > MaxCellChars ? text[..MaxCellChars] + "…[truncated]" : text,
        bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        _ => value.ToString(),
    };
}
