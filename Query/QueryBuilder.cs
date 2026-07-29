using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Providers;
using ReadOnlyDbMcp.Schema;

namespace ReadOnlyDbMcp.Query;

public sealed class BuiltQuery
{
    public required string Sql { get; init; }
    public required List<(string Name, object Value)> Parameters { get; init; }
    public required List<string> OutputColumns { get; init; }
    public required int EffectiveLimit { get; init; }
}

/// <summary>
/// Turns a structured ReadRowsRequest into SQL. The security invariant lives here: every
/// identifier in the output is validated against introspected schema and quoted by the provider,
/// every value becomes a bound parameter, and no agent-supplied string is ever concatenated into
/// the statement text.
/// </summary>
public sealed partial class QueryBuilder(IDbProvider provider, SchemaModel schema, ConfigFile config)
{
    private const StringComparison Oic = StringComparison.OrdinalIgnoreCase;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex SafeAliasPattern();

    private sealed record Source(string Alias, TableInfo Table);

    private readonly List<Source> _sources = [];
    private readonly List<(string Name, object Value)> _parameters = [];

    public BuiltQuery Build(ReadRowsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.From))
            throw new QueryValidationException("'from' is required.");

        var fromTable = schema.ResolveTable(request.From);
        AddSource(request.FromAlias ?? fromTable.Name, fromTable);

        var joinClauses = new List<string>();
        foreach (var join in request.Joins ?? [])
            joinClauses.Add(BuildJoin(join));

        var (selectParts, outputColumns) = BuildSelectList(request);
        var whereClause = BuildWhere(request.Filters);
        var groupByClause = BuildGroupBy(request.GroupBy);
        var orderByClause = BuildOrderBy(request.OrderBy, request.Aggregates);

        var offset = request.Offset ?? 0;
        if (offset < 0)
            throw new QueryValidationException("'offset' must be >= 0.");
        if (offset > 0 && orderByClause is null)
            throw new QueryValidationException("'offset' requires 'orderBy' so that pagination is deterministic.");

        var effectiveLimit = Math.Clamp(request.Limit ?? config.DefaultLimit, 1, config.MaxRows);

        var sql = new StringBuilder("SELECT ");
        sql.Append(string.Join(", ", selectParts));
        sql.Append(" FROM ").Append(QuoteTable(fromTable)).Append(" AS ").Append(provider.QuoteIdentifier(_sources[0].Alias));
        foreach (var clause in joinClauses)
            sql.Append(' ').Append(clause);
        if (whereClause is not null)
            sql.Append(" WHERE ").Append(whereClause);
        if (groupByClause is not null)
            sql.Append(" GROUP BY ").Append(groupByClause);
        if (orderByClause is not null)
            sql.Append(" ORDER BY ").Append(orderByClause);

        // Ask for one row beyond the limit so truncation is reported accurately.
        var limited = provider.ApplyLimit(sql.ToString(), effectiveLimit + 1, offset, orderByClause is not null);

        return new BuiltQuery
        {
            Sql = limited,
            Parameters = _parameters,
            OutputColumns = outputColumns,
            EffectiveLimit = effectiveLimit,
        };
    }

    private void AddSource(string alias, TableInfo table)
    {
        if (!SafeAliasPattern().IsMatch(alias))
            throw new QueryValidationException(
                $"Alias '{alias}' is not valid. Use letters, digits, and underscores, starting with a letter or underscore.");
        if (_sources.Any(s => s.Alias.Equals(alias, Oic)))
            throw new QueryValidationException(
                $"Alias '{alias}' is used twice. Give the join an explicit 'as' alias.");
        _sources.Add(new Source(alias, table));
    }

    private string BuildJoin(JoinSpec join)
    {
        if (string.IsNullOrWhiteSpace(join.Table))
            throw new QueryValidationException("Each join needs a 'table'.");

        var table = schema.ResolveTable(join.Table);
        var alias = join.Alias ?? table.Name;
        var joinKeyword = (join.Type?.ToLowerInvariant() ?? "inner") switch
        {
            "inner" => "INNER JOIN",
            "left" => "LEFT JOIN",
            var t => throw new QueryValidationException($"Join type '{t}' is not supported. Use 'inner' or 'left'."),
        };

        string onSql;
        if (join.On is { } on)
        {
            AddSource(alias, table);
            var left = ResolveColumn(on.Left);
            var right = ResolveColumn(on.Right);
            onSql = $"{ColumnSql(left)} = {ColumnSql(right)}";
        }
        else
        {
            onSql = InferJoinCondition(table, alias);
            AddSource(alias, table);
        }

        return $"{joinKeyword} {QuoteTable(table)} AS {provider.QuoteIdentifier(alias)} ON {onSql}";
    }

    private string InferJoinCondition(TableInfo newTable, string newAlias)
    {
        var candidates = new List<(ForeignKeyInfo Fk, Source Existing)>();
        foreach (var source in _sources)
            foreach (var fk in schema.ForeignKeysBetween(source.Table.Key, newTable.Key))
                candidates.Add((fk, source));

        if (candidates.Count == 0)
            throw new QueryValidationException(
                $"No foreign key relates '{newTable.Key}' to the tables already in the query " +
                $"({string.Join(", ", _sources.Select(s => s.Table.Key))}). Specify join 'on' explicitly.");
        if (candidates.Count > 1)
            throw new QueryValidationException(
                $"Multiple foreign keys relate '{newTable.Key}' to this query " +
                $"({string.Join(", ", candidates.Select(c => c.Fk.Name))}). Specify join 'on' explicitly.");

        var (foreignKey, existing) = candidates[0];
        var newTableIsFkSource = foreignKey.FromTable.Equals(newTable.Key, Oic);

        var conditions = foreignKey.ColumnPairs.Select(pair =>
        {
            var (newColumn, existingColumn) = newTableIsFkSource ? (pair.From, pair.To) : (pair.To, pair.From);
            return $"{provider.QuoteIdentifier(newAlias)}.{provider.QuoteIdentifier(newColumn)} = " +
                   $"{provider.QuoteIdentifier(existing.Alias)}.{provider.QuoteIdentifier(existingColumn)}";
        });
        return string.Join(" AND ", conditions);
    }

    private (Source Source, ColumnInfo Column) ResolveColumn(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new QueryValidationException("Empty column reference.");

        var idx = reference.IndexOf('.');
        if (idx > 0)
        {
            var aliasPart = reference[..idx];
            var columnPart = reference[(idx + 1)..];
            var source = _sources.FirstOrDefault(s => s.Alias.Equals(aliasPart, Oic))
                ?? throw new QueryValidationException(
                    $"Unknown table alias '{aliasPart}' in '{reference}'. " +
                    $"Aliases in this query: {string.Join(", ", _sources.Select(s => s.Alias))}.");
            var column = source.Table.FindColumn(columnPart)
                ?? throw new QueryValidationException(
                    $"Table '{source.Table.Key}' has no column '{columnPart}'. Use describe_table to see its columns.");
            return (source, column);
        }

        var matches = _sources
            .Select(s => (Source: s, Column: s.Table.FindColumn(reference)))
            .Where(m => m.Column is not null)
            .ToList();
        return matches.Count switch
        {
            1 => (matches[0].Source, matches[0].Column!),
            0 => throw new QueryValidationException(
                $"Unknown column '{reference}'. Use describe_table to see available columns."),
            _ => throw new QueryValidationException(
                $"Column '{reference}' exists in multiple tables ({string.Join(", ", matches.Select(m => m.Source.Alias))}). " +
                "Qualify it as 'alias.column'."),
        };
    }

    private string ColumnSql((Source Source, ColumnInfo Column) resolved) =>
        $"{provider.QuoteIdentifier(resolved.Source.Alias)}.{provider.QuoteIdentifier(resolved.Column.Name)}";

    private string OutputName((Source Source, ColumnInfo Column) resolved) =>
        _sources.Count > 1 ? $"{resolved.Source.Alias}.{resolved.Column.Name}" : resolved.Column.Name;

    private (List<string> SelectParts, List<string> OutputColumns) BuildSelectList(ReadRowsRequest request)
    {
        var selectParts = new List<string>();
        var outputColumns = new List<string>();

        if (request.Aggregates is { Count: > 0 } aggregates)
        {
            // With aggregates, plain columns must come from groupBy.
            var groupRefs = request.GroupBy ?? [];
            var plainColumns = request.Columns ?? groupRefs;
            foreach (var reference in plainColumns)
            {
                if (!groupRefs.Any(g => g.Equals(reference, Oic)))
                    throw new QueryValidationException(
                        $"Column '{reference}' must appear in 'groupBy' when aggregates are used.");
                var resolved = ResolveColumn(reference);
                var name = OutputName(resolved);
                selectParts.Add($"{ColumnSql(resolved)} AS {provider.QuoteIdentifier(name)}");
                outputColumns.Add(name);
            }

            foreach (var aggregate in aggregates)
            {
                var fn = aggregate.Fn.ToLowerInvariant() switch
                {
                    "count" => "COUNT",
                    "sum" => "SUM",
                    "avg" => "AVG",
                    "min" => "MIN",
                    "max" => "MAX",
                    var f => throw new QueryValidationException(
                        $"Aggregate '{f}' is not supported. Use count, sum, avg, min, or max."),
                };

                string argSql, defaultAlias;
                if (aggregate.Column == "*")
                {
                    if (fn != "COUNT")
                        throw new QueryValidationException("Column '*' is only valid with the count aggregate.");
                    argSql = "*";
                    defaultAlias = "count_all";
                }
                else
                {
                    var resolved = ResolveColumn(aggregate.Column);
                    argSql = ColumnSql(resolved);
                    defaultAlias = $"{aggregate.Fn.ToLowerInvariant()}_{resolved.Column.Name}";
                }

                var alias = aggregate.Alias ?? defaultAlias;
                if (!SafeAliasPattern().IsMatch(alias))
                    throw new QueryValidationException(
                        $"Aggregate alias '{alias}' is not valid. Use letters, digits, and underscores.");
                selectParts.Add($"{fn}({argSql}) AS {provider.QuoteIdentifier(alias)}");
                outputColumns.Add(alias);
            }
        }
        else if (request.Columns is { Count: > 0 } columns)
        {
            foreach (var reference in columns)
            {
                var resolved = ResolveColumn(reference);
                var name = OutputName(resolved);
                selectParts.Add($"{ColumnSql(resolved)} AS {provider.QuoteIdentifier(name)}");
                outputColumns.Add(name);
            }
        }
        else
        {
            foreach (var source in _sources)
                foreach (var column in source.Table.Columns)
                {
                    var name = _sources.Count > 1 ? $"{source.Alias}.{column.Name}" : column.Name;
                    selectParts.Add(
                        $"{provider.QuoteIdentifier(source.Alias)}.{provider.QuoteIdentifier(column.Name)} AS {provider.QuoteIdentifier(name)}");
                    outputColumns.Add(name);
                }
        }

        if (selectParts.Count == 0)
            throw new QueryValidationException("Nothing to select: provide 'columns', 'aggregates', or omit both for all columns.");

        return (selectParts, outputColumns);
    }

    private string? BuildWhere(List<FilterSpec>? filters)
    {
        if (filters is not { Count: > 0 })
            return null;

        var conditions = new List<string>();
        foreach (var filter in filters)
        {
            var resolved = ResolveColumn(filter.Column);
            var columnSql = ColumnSql(resolved);
            var category = provider.Categorize(resolved.Column.DataType);
            var op = filter.Op.Trim().ToLowerInvariant().Replace(' ', '_');

            switch (op)
            {
                case "=" or "==":
                    conditions.Add($"{columnSql} = {BindScalar(filter, category, resolved)}");
                    break;
                case "!=" or "<>":
                    conditions.Add($"{columnSql} <> {BindScalar(filter, category, resolved)}");
                    break;
                case "<" or "<=" or ">" or ">=":
                    conditions.Add($"{columnSql} {op} {BindScalar(filter, category, resolved)}");
                    break;
                case "like" or "not_like":
                    {
                        var value = RequireValue(filter);
                        if (value.ValueKind != JsonValueKind.String)
                            throw new QueryValidationException($"'{filter.Op}' on '{filter.Column}' requires a string value.");
                        var placeholder = AddParameter(value.GetString()!);
                        conditions.Add($"{columnSql} {(op == "like" ? "LIKE" : "NOT LIKE")} {placeholder}");
                        break;
                    }
                case "in" or "not_in":
                    {
                        var value = RequireValue(filter);
                        if (value.ValueKind != JsonValueKind.Array)
                            throw new QueryValidationException($"'{filter.Op}' on '{filter.Column}' requires an array value.");
                        var items = value.EnumerateArray().ToList();
                        if (items.Count == 0)
                        {
                            conditions.Add(op == "in" ? "1 = 0" : "1 = 1");
                            break;
                        }
                        var placeholders = items.Select(item => AddParameter(CoerceScalar(item, category, resolved)));
                        conditions.Add($"{columnSql} {(op == "in" ? "IN" : "NOT IN")} ({string.Join(", ", placeholders)})");
                        break;
                    }
                case "is_null":
                    conditions.Add($"{columnSql} IS NULL");
                    break;
                case "is_not_null":
                    conditions.Add($"{columnSql} IS NOT NULL");
                    break;
                default:
                    throw new QueryValidationException(
                        $"Filter op '{filter.Op}' is not supported. " +
                        "Use =, !=, <, <=, >, >=, in, not_in, like, not_like, is_null, is_not_null.");
            }
        }

        return string.Join(" AND ", conditions);
    }

    private string BindScalar(FilterSpec filter, ColumnCategory category, (Source, ColumnInfo) resolved) =>
        AddParameter(CoerceScalar(RequireValue(filter), category, resolved));

    private static JsonElement RequireValue(FilterSpec filter) =>
        filter.Value is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } value
            ? value
            : throw new QueryValidationException(
                $"Filter on '{filter.Column}' with op '{filter.Op}' requires a value. Use is_null/is_not_null to test for NULL.");

    private object CoerceScalar(JsonElement element, ColumnCategory category, (Source Source, ColumnInfo Column) resolved)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False:
                return element.GetBoolean();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var longValue)) return longValue;
                if (element.TryGetDecimal(out var decimalValue)) return decimalValue;
                return element.GetDouble();
            case JsonValueKind.String:
                var text = element.GetString()!;
                return category switch
                {
                    ColumnCategory.Numeric => decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
                        ? number
                        : throw new QueryValidationException(
                            $"Value '{text}' is not numeric, but column '{resolved.Column.Name}' is {resolved.Column.DataType}."),
                    ColumnCategory.DateTime => CoerceDateTime(text, resolved.Column),
                    ColumnCategory.Boolean => bool.TryParse(text, out var flag)
                        ? flag
                        : throw new QueryValidationException(
                            $"Value '{text}' is not a boolean, but column '{resolved.Column.Name}' is {resolved.Column.DataType}."),
                    ColumnCategory.Uuid => Guid.TryParse(text, out var guid)
                        ? guid
                        : throw new QueryValidationException(
                            $"Value '{text}' is not a UUID, but column '{resolved.Column.Name}' is {resolved.Column.DataType}."),
                    _ => text,
                };
            default:
                throw new QueryValidationException(
                    $"Unsupported filter value kind '{element.ValueKind}' for column '{resolved.Column.Name}'.");
        }
    }

    private object CoerceDateTime(string text, ColumnInfo column)
    {
        var type = column.DataType.ToLowerInvariant();
        var wantsOffset = type.Contains("with time zone") || type == "datetimeoffset";
        if (wantsOffset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt;
        throw new QueryValidationException(
            $"Value '{text}' is not a valid date/time for column '{column.Name}' ({column.DataType}). Use ISO 8601, e.g. 2026-07-29 or 2026-07-29T14:30:00Z.");
    }

    private string AddParameter(object value)
    {
        var name = $"@p{_parameters.Count}";
        _parameters.Add((name, value));
        return name;
    }

    private string? BuildGroupBy(List<string>? groupBy)
    {
        if (groupBy is not { Count: > 0 })
            return null;
        return string.Join(", ", groupBy.Select(reference => ColumnSql(ResolveColumn(reference))));
    }

    private string? BuildOrderBy(List<OrderSpec>? orderBy, List<AggregateSpec>? aggregates)
    {
        if (orderBy is not { Count: > 0 })
            return null;

        var aggregateAliases = (aggregates ?? [])
            .Select(a => a.Alias ?? (a.Column == "*" ? "count_all" : $"{a.Fn.ToLowerInvariant()}_{a.Column.Split('.')[^1]}"))
            .ToList();

        var parts = new List<string>();
        foreach (var order in orderBy)
        {
            var direction = (order.Dir?.ToLowerInvariant() ?? "asc") switch
            {
                "asc" => "ASC",
                "desc" => "DESC",
                var d => throw new QueryValidationException($"Order direction '{d}' is not valid. Use 'asc' or 'desc'."),
            };

            string columnSql;
            if (aggregateAliases.Any(a => a.Equals(order.Column, Oic)))
                columnSql = provider.QuoteIdentifier(order.Column);
            else
                columnSql = ColumnSql(ResolveColumn(order.Column));
            parts.Add($"{columnSql} {direction}");
        }
        return string.Join(", ", parts);
    }

    private string QuoteTable(TableInfo table) =>
        string.IsNullOrEmpty(table.Schema)
            ? provider.QuoteIdentifier(table.Name)
            : $"{provider.QuoteIdentifier(table.Schema)}.{provider.QuoteIdentifier(table.Name)}";
}
