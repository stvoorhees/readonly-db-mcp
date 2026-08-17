namespace ReadOnlyDbMcp.Tabular;

/// <summary>
/// A tabular model is intentionally distinct from SchemaModel. Its identifiers are validated
/// against SSAS metadata before the DAX builder can use them.
/// </summary>
public sealed class TabularModel
{
    public List<TabularTable> Tables { get; } = [];
    public List<TabularMeasure> Measures { get; } = [];
    public List<TabularRelationship> Relationships { get; } = [];

    public TabularTable ResolveTable(string name) =>
        Tables.FirstOrDefault(t => !t.IsHidden && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new Schema.QueryValidationException($"Unknown tabular table '{name}'. Use list_tabular_tables to see what is available.");

    public TabularColumn ResolveColumn(TabularTable table, string name) =>
        table.Columns.FirstOrDefault(c => !c.IsHidden && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new Schema.QueryValidationException($"Unknown column '{name}' on tabular table '{table.Name}'. Use describe_tabular_table to see its columns.");

    public TabularMeasure ResolveMeasure(TabularTable table, string name) =>
        Measures.FirstOrDefault(m => (m.TableId == table.Id || m.TableId.Length == 0) && !m.IsHidden && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new Schema.QueryValidationException($"Unknown measure '{name}' on tabular table '{table.Name}'. Use describe_tabular_table to see its measures.");
}

public sealed class TabularTable
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsHidden { get; init; }
    public List<TabularColumn> Columns { get; } = [];
}

public sealed class TabularColumn
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsHidden { get; init; }
}

public sealed class TabularMeasure
{
    public required string Id { get; init; }
    public required string TableId { get; init; }
    public required string Name { get; init; }
    public bool IsHidden { get; init; }
}

public sealed class TabularRelationship
{
    public required string FromTableId { get; init; }
    public required string FromColumnId { get; init; }
    public required string ToTableId { get; init; }
    public required string ToColumnId { get; init; }
    public bool IsActive { get; init; }
}
