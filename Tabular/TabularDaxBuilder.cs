using System.Globalization;
using System.Text.Json;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Tabular;

/// <summary>
/// Builds the small, deliberately bounded DAX subset used by read_tabular_rows. This accepts
/// metadata-resolved identifiers and typed JSON values only; it never accepts a DAX fragment.
/// </summary>
public sealed class TabularDaxBuilder(ConfigFile config, TabularModel model)
{
    private const int MaxFilterValues = 100;
    private const int MaxStringLength = 4_000;

    public BuiltTabularQuery Build(TabularReadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Table))
            throw new QueryValidationException("'table' is required.");

        var table = model.ResolveTable(request.Table);
        var columns = (request.Columns ?? []).Select(name => model.ResolveColumn(table, name)).ToList();
        var measures = (request.Measures ?? []).Select(name => model.ResolveMeasure(table, name)).ToList();
        if (columns.Count == 0 && measures.Count == 0)
            throw new QueryValidationException("Provide at least one column or measure. Use describe_tabular_table to see available fields.");
        RejectDuplicates(columns.Select(column => column.Name), "column");
        RejectDuplicates(measures.Select(measure => measure.Name), "measure");

        var effectiveLimit = request.Limit ?? config.DefaultLimit;
        if (effectiveLimit <= 0)
            throw new QueryValidationException("'limit' must be greater than zero.");
        effectiveLimit = Math.Min(effectiveLimit, config.MaxRows);

        var filters = (request.Filters ?? []).Select(filter => BuildFilter(table, filter)).ToList();
        var source = measures.Count > 0
            ? BuildSummary(table, columns, measures, filters)
            : BuildRows(table, columns, filters);
        var ordering = BuildOrdering(table, columns, measures, request.OrderBy);
        var dax = $"EVALUATE TOPN({effectiveLimit}, {source}{ordering})";
        return new BuiltTabularQuery(dax, [.. columns.Select(c => c.Name), .. measures.Select(m => m.Name)], effectiveLimit);
    }

    private static string BuildRows(TabularTable table, IReadOnlyList<TabularColumn> columns, IReadOnlyList<string> filters)
    {
        var filterArguments = filters.Count == 0 ? "" : ", " + string.Join(", ", filters);
        var projections = string.Join(", ", columns.Select(column => $"{DaxString(column.Name)}, {Column(table, column)}"));
        return $"SELECTCOLUMNS(CALCULATETABLE({Table(table)}{filterArguments}), {projections})";
    }

    private static string BuildSummary(TabularTable table, IReadOnlyList<TabularColumn> columns, IReadOnlyList<TabularMeasure> measures, IReadOnlyList<string> filters)
    {
        var arguments = new List<string>();
        arguments.AddRange(columns.Select(column => Column(table, column)));
        arguments.AddRange(filters);
        arguments.AddRange(measures.Select(measure => $"{DaxString(measure.Name)}, {Measure(measure)}"));
        return $"SUMMARIZECOLUMNS({string.Join(", ", arguments)})";
    }

    private string BuildFilter(TabularTable table, TabularFilter filter)
    {
        if (string.IsNullOrWhiteSpace(filter.Column))
            throw new QueryValidationException("Each filter requires a 'column'.");
        var column = model.ResolveColumn(table, filter.Column);
        var reference = Column(table, column);
        return filter.Op.Trim().ToLowerInvariant() switch
        {
            "=" => $"KEEPFILTERS({reference} = {Literal(RequiredValue(filter))})",
            "in" => $"KEEPFILTERS({reference} IN {{ {InLiterals(RequiredValue(filter))} }})",
            var operation => throw new QueryValidationException($"Filter operation '{operation}' is not supported. Use '=' or 'in'."),
        };
    }

    private static JsonElement RequiredValue(TabularFilter filter) =>
        filter.Value ?? throw new QueryValidationException($"Filter '{filter.Column}' requires 'value'.");

    private static string InLiterals(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw new QueryValidationException("'in' filters require a non-empty JSON array value.");
        if (value.GetArrayLength() > MaxFilterValues)
            throw new QueryValidationException($"'in' filters allow at most {MaxFilterValues} values.");
        return string.Join(", ", value.EnumerateArray().Select(Literal));
    }

    private static string Literal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => DaxString(value.GetString() ?? ""),
        JsonValueKind.Number when decimal.TryParse(value.GetRawText(), NumberStyles.Number, CultureInfo.InvariantCulture, out _) => value.GetRawText(),
        JsonValueKind.True => "TRUE()",
        JsonValueKind.False => "FALSE()",
        JsonValueKind.Null => "BLANK()",
        _ => throw new QueryValidationException("Filter values must be string, finite decimal number, boolean, null, or an array of those values."),
    };

    private static string BuildOrdering(TabularTable table, IReadOnlyList<TabularColumn> columns, IReadOnlyList<TabularMeasure> measures, TabularOrder? order)
    {
        if (order is null || string.IsNullOrWhiteSpace(order.Column))
            return "";

        var direction = (order.Dir ?? "asc").Trim().ToLowerInvariant() switch
        {
            "asc" => "ASC",
            "desc" => "DESC",
            var value => throw new QueryValidationException($"Order direction '{value}' is not valid. Use 'asc' or 'desc'."),
        };
        var column = columns.FirstOrDefault(c => c.Name.Equals(order.Column, StringComparison.OrdinalIgnoreCase));
        if (column is not null)
            return $", {Column(table, column)}, {direction}";
        var measure = measures.FirstOrDefault(m => m.Name.Equals(order.Column, StringComparison.OrdinalIgnoreCase));
        if (measure is not null)
            return $", {Measure(measure)}, {direction}";
        throw new QueryValidationException($"orderBy column '{order.Column}' must be included in columns or measures.");
    }

    private static void RejectDuplicates(IEnumerable<string> names, string kind)
    {
        var duplicate = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new QueryValidationException($"Duplicate {kind} '{duplicate.Key}' is not allowed.");
    }

    private static string Table(TabularTable table) => $"'{table.Name.Replace("'", "''", StringComparison.Ordinal)}'";
    private static string Column(TabularTable table, TabularColumn column) => $"{Table(table)}[{column.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static string Measure(TabularMeasure measure) => $"[{measure.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static string DaxString(string value)
    {
        if (value.Length > MaxStringLength)
            throw new QueryValidationException($"String filter values may not exceed {MaxStringLength} characters.");
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}

public sealed record BuiltTabularQuery(string Dax, List<string> OutputColumns, int EffectiveLimit);
