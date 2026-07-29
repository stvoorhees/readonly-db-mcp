using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReadOnlyDbMcp.Query;

public sealed class JoinOn
{
    [JsonPropertyName("left")] public string Left { get; set; } = "";
    [JsonPropertyName("right")] public string Right { get; set; } = "";
}

public sealed class JoinSpec
{
    [JsonPropertyName("table")] public string Table { get; set; } = "";
    [JsonPropertyName("as")] public string? Alias { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; } // inner (default) | left
    [JsonPropertyName("on")] public JoinOn? On { get; set; } // omit to infer from a foreign key
}

public sealed class FilterSpec
{
    [JsonPropertyName("column")] public string Column { get; set; } = "";
    [JsonPropertyName("op")] public string Op { get; set; } = "=";
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
}

public sealed class AggregateSpec
{
    [JsonPropertyName("fn")] public string Fn { get; set; } = ""; // count | sum | avg | min | max
    [JsonPropertyName("column")] public string Column { get; set; } = "*"; // "*" only valid for count
    [JsonPropertyName("as")] public string? Alias { get; set; }
}

public sealed class OrderSpec
{
    [JsonPropertyName("column")] public string Column { get; set; } = "";
    [JsonPropertyName("dir")] public string? Dir { get; set; } // asc (default) | desc
}

public sealed class ReadRowsRequest
{
    [JsonPropertyName("connection")] public string Connection { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("fromAlias")] public string? FromAlias { get; set; }
    [JsonPropertyName("joins")] public List<JoinSpec>? Joins { get; set; }
    [JsonPropertyName("columns")] public List<string>? Columns { get; set; }
    [JsonPropertyName("filters")] public List<FilterSpec>? Filters { get; set; }
    [JsonPropertyName("aggregates")] public List<AggregateSpec>? Aggregates { get; set; }
    [JsonPropertyName("groupBy")] public List<string>? GroupBy { get; set; }
    [JsonPropertyName("orderBy")] public List<OrderSpec>? OrderBy { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
    [JsonPropertyName("offset")] public int? Offset { get; set; }
}
