using System.Data;
using Microsoft.AnalysisServices.AdomdClient;

namespace ReadOnlyDbMcp.Tabular;

public static class TabularSchemaLoader
{
    private const int MetadataLimit = 10_000;

    public static Task<TabularModel> LoadAsync(string connectionString, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new AdomdConnection(connectionString);
        connection.Open();
        return Task.FromResult(Load(connection, commandTimeoutSeconds, cancellationToken));
    }

    internal static TabularModel Load(AdomdConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            return LoadTmschema(connection, commandTimeoutSeconds, cancellationToken);
        }
        catch (AdomdException)
        {
            // TMSCHEMA DMVs are restricted to server administrators on some deployments. The
            // standard schema rowsets remain available to ordinary model readers.
            return LoadSchemaRowsets(connection, cancellationToken);
        }
    }

    private static TabularModel LoadTmschema(AdomdConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var model = new TabularModel();
        var tables = Read(connection, $"SELECT TOP {MetadataLimit} * FROM $SYSTEM.TMSCHEMA_TABLES", commandTimeoutSeconds, cancellationToken);
        foreach (var row in tables)
        {
            var id = Required(row, "ID");
            var name = First(row, "Name", "ExplicitName");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            model.Tables.Add(new TabularTable { Id = id, Name = name, IsHidden = Boolean(row, "IsHidden") });
        }

        var tableById = model.Tables.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var columns = Read(connection, $"SELECT TOP {MetadataLimit} * FROM $SYSTEM.TMSCHEMA_COLUMNS", commandTimeoutSeconds, cancellationToken);
        foreach (var row in columns)
        {
            var tableId = First(row, "TableID");
            var id = First(row, "ID");
            var name = First(row, "ExplicitName", "Name");
            if (tableId is null || id is null || name is null || !tableById.TryGetValue(tableId, out var table))
                continue;
            table.Columns.Add(new TabularColumn
            {
                Id = id,
                Name = name,
                DataType = First(row, "DataType") ?? "unknown",
                IsHidden = Boolean(row, "IsHidden"),
            });
        }

        var measures = Read(connection, $"SELECT TOP {MetadataLimit} * FROM $SYSTEM.TMSCHEMA_MEASURES", commandTimeoutSeconds, cancellationToken);
        foreach (var row in measures)
        {
            var tableId = First(row, "TableID");
            var id = First(row, "ID");
            var name = First(row, "Name", "ExplicitName");
            if (tableId is null || id is null || name is null || !tableById.ContainsKey(tableId))
                continue;
            model.Measures.Add(new TabularMeasure { Id = id, TableId = tableId, Name = name, IsHidden = Boolean(row, "IsHidden") });
        }

        var relationships = Read(connection, $"SELECT TOP {MetadataLimit} * FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS", commandTimeoutSeconds, cancellationToken);
        foreach (var row in relationships)
        {
            var fromTable = First(row, "FromTableID");
            var fromColumn = First(row, "FromColumnID");
            var toTable = First(row, "ToTableID");
            var toColumn = First(row, "ToColumnID");
            if (fromTable is null || fromColumn is null || toTable is null || toColumn is null)
                continue;
            model.Relationships.Add(new TabularRelationship
            {
                FromTableId = fromTable,
                FromColumnId = fromColumn,
                ToTableId = toTable,
                ToColumnId = toColumn,
                IsActive = !Has(row, "IsActive") || Boolean(row, "IsActive"),
            });
        }

        return model;
    }

    private static TabularModel LoadSchemaRowsets(AdomdConnection connection, CancellationToken cancellationToken)
    {
        var model = new TabularModel();
        var dimensions = SchemaRows(connection, "MDSCHEMA_DIMENSIONS");
        var levelTypeByHierarchy = SchemaRows(connection, "MDSCHEMA_LEVELS")
            .Select(row => new
            {
                Hierarchy = First(row, "HIERARCHY_UNIQUE_NAME"),
                LevelNumber = Number(row, "LEVEL_NUMBER"),
                DbType = row.GetValueOrDefault("LEVEL_DBTYPE"),
            })
            .Where(item => item.Hierarchy is not null)
            .GroupBy(item => item.Hierarchy!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => OleDbTypeNames.FromDeepestLevel(group.Select(item => (item.LevelNumber, item.DbType))),
                StringComparer.OrdinalIgnoreCase);
        var tableByUniqueName = new Dictionary<string, TabularTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dimensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uniqueName = First(row, "DIMENSION_UNIQUE_NAME");
            var name = First(row, "DIMENSION_CAPTION", "DIMENSION_NAME");
            if (uniqueName is null || name is null || IsMeasuresDimension(row, uniqueName))
                continue;

            var table = new TabularTable { Id = uniqueName, Name = name };
            model.Tables.Add(table);
            tableByUniqueName[uniqueName] = table;
        }

        foreach (var row in SchemaRows(connection, "MDSCHEMA_HIERARCHIES"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tableUniqueName = First(row, "DIMENSION_UNIQUE_NAME");
            var name = First(row, "HIERARCHY_CAPTION", "HIERARCHY_NAME");
            var hierarchyUniqueName = First(row, "HIERARCHY_UNIQUE_NAME");
            if (tableUniqueName is null || name is null || !tableByUniqueName.TryGetValue(tableUniqueName, out var table))
                continue;

            // Attribute hierarchies correspond to queryable tabular columns. A rowset without
            // origin information is accepted for compatibility with older SSAS versions.
            if (row.TryGetValue("HIERARCHY_ORIGIN", out var origin) && origin is not null &&
                int.TryParse(origin.ToString(), out var originValue) && originValue != 2)
                continue;
            table.Columns.Add(new TabularColumn
            {
                Id = $"{table.Id}:{name}",
                Name = name,
                DataType = hierarchyUniqueName is not null && levelTypeByHierarchy.TryGetValue(hierarchyUniqueName, out var dataType)
                    ? dataType
                    : "unknown",
            });
        }

        foreach (var row in SchemaRows(connection, "MDSCHEMA_MEASURES"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = First(row, "MEASURE_CAPTION", "MEASURE_NAME");
            if (name is null)
                continue;

            // Schema rowsets do not consistently expose the measure's home table. Empty means
            // globally addressable: measures can still be calculated from any selected table.
            var homeTable = First(row, "MEASUREGROUP_NAME");
            var table = model.Tables.FirstOrDefault(candidate => candidate.Name.Equals(homeTable, StringComparison.OrdinalIgnoreCase));
            model.Measures.Add(new TabularMeasure { Id = name, TableId = table?.Id ?? "", Name = name });
        }

        return model;
    }

    private static List<Dictionary<string, object?>> SchemaRows(AdomdConnection connection, string schema)
    {
        var dataSet = connection.GetSchemaDataSet(schema, null);
        if (dataSet.Tables.Count == 0)
            return [];

        return dataSet.Tables[0].Rows.Cast<DataRow>().Select(dataRow =>
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (DataColumn column in dataSet.Tables[0].Columns)
                row[column.ColumnName] = dataRow.IsNull(column) ? null : dataRow[column];
            return row;
        }).ToList();
    }

    private static bool IsMeasuresDimension(IReadOnlyDictionary<string, object?> row, string uniqueName) =>
        uniqueName.Equals("[Measures]", StringComparison.OrdinalIgnoreCase) ||
        (row.TryGetValue("DIMENSION_TYPE", out var type) && type is not null && Convert.ToInt32(type, System.Globalization.CultureInfo.InvariantCulture) == 2);

    private static List<Dictionary<string, object?>> Read(AdomdConnection connection, string statement, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = statement;
        command.CommandTimeout = commandTimeoutSeconds;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static bool Has(IReadOnlyDictionary<string, object?> row, string name) => row.ContainsKey(name);
    private static string Required(IReadOnlyDictionary<string, object?> row, string name) =>
        First(row, name) ?? throw new InvalidOperationException($"SSAS metadata rowset did not include required '{name}' column.");
    private static string? First(IReadOnlyDictionary<string, object?> row, params string[] names) =>
        names.Select(name => row.TryGetValue(name, out var value) ? value?.ToString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static bool Boolean(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) && value is not null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    private static int Number(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) && value is not null &&
        int.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MinValue;
}
