using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReadOnlyDbMcp.Tabular;

public sealed class TabularFilter
{
    [JsonPropertyName("column")] public string Column { get; set; } = "";
    [JsonPropertyName("op")] public string Op { get; set; } = "="; // = | in
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
}

public sealed class TabularOrder
{
    [JsonPropertyName("column")] public string Column { get; set; } = "";
    [JsonPropertyName("dir")] public string? Dir { get; set; } // asc | desc
}

public sealed class TabularReadRequest
{
    [JsonPropertyName("connection")] public string Connection { get; set; } = "";
    [JsonPropertyName("table")] public string Table { get; set; } = "";
    [JsonPropertyName("columns")] public List<string>? Columns { get; set; }
    [JsonPropertyName("measures")] public List<string>? Measures { get; set; }
    [JsonPropertyName("filters")] public List<TabularFilter>? Filters { get; set; }
    [JsonPropertyName("orderBy")] public TabularOrder? OrderBy { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
}
