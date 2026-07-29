namespace ReadOnlyDbMcp.Schema;

/// <summary>Thrown for any invalid agent request; the message is returned to the agent as a tool error.</summary>
public sealed class QueryValidationException(string message) : Exception(message);

public sealed class ColumnInfo
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsNullable { get; init; }
}

public sealed class TableInfo
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; } // "table" | "view"
    public List<ColumnInfo> Columns { get; } = [];

    public string Key => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";

    public ColumnInfo? FindColumn(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class ForeignKeyInfo
{
    public required string Name { get; init; }
    public required string FromTable { get; init; } // TableInfo.Key format
    public required string ToTable { get; init; }
    public List<(string From, string To)> ColumnPairs { get; } = [];
}

public sealed class SchemaModel
{
    private const StringComparison Oic = StringComparison.OrdinalIgnoreCase;

    public List<TableInfo> Tables { get; } = [];
    public List<ForeignKeyInfo> ForeignKeys { get; } = [];

    public TableInfo ResolveTable(string reference)
    {
        var idx = reference.LastIndexOf('.');
        if (idx > 0)
        {
            var schema = reference[..idx];
            var name = reference[(idx + 1)..];
            return Tables.FirstOrDefault(t => t.Schema.Equals(schema, Oic) && t.Name.Equals(name, Oic))
                ?? throw new QueryValidationException($"Unknown table '{reference}'. Use list_tables to see what is available.");
        }

        var matches = Tables.Where(t => t.Name.Equals(reference, Oic)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new QueryValidationException($"Unknown table '{reference}'. Use list_tables to see what is available."),
            _ => throw new QueryValidationException(
                $"Table name '{reference}' is ambiguous ({string.Join(", ", matches.Select(m => m.Key))}). Qualify it with a schema."),
        };
    }

    public IEnumerable<ForeignKeyInfo> ForeignKeysBetween(string tableA, string tableB) =>
        ForeignKeys.Where(fk =>
            (fk.FromTable.Equals(tableA, Oic) && fk.ToTable.Equals(tableB, Oic)) ||
            (fk.FromTable.Equals(tableB, Oic) && fk.ToTable.Equals(tableA, Oic)));
}
