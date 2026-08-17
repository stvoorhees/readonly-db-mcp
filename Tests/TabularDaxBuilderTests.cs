using System.Text.Json;
using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Schema;
using ReadOnlyDbMcp.Tabular;
using Xunit;

namespace ReadOnlyDbMcp.Tests;

public sealed class TabularDaxBuilderTests
{
    [Fact]
    public void Build_escapes_typed_filter_values_and_caps_limit()
    {
        var builder = new TabularDaxBuilder(new ConfigFile { MaxRows = 10, DefaultLimit = 5 }, Model());

        var query = builder.Build(new TabularReadRequest
        {
            Table = "Sales",
            Columns = ["Region"],
            Filters = [new TabularFilter { Column = "Region", Value = Value("a\" ) EVALUATE ROW(\"x\", 1) //") }],
            Limit = 100,
        });

        Assert.Equal(10, query.EffectiveLimit);
        Assert.Contains("""KEEPFILTERS('Sales'[Region] = "a"" ) EVALUATE ROW(""x"", 1) //")""", query.Dax);
        Assert.DoesNotContain("a\" ) EVALUATE", query.Dax);
    }

    [Fact]
    public void Build_rejects_unresolved_identifiers_and_unsupported_operators()
    {
        var builder = new TabularDaxBuilder(new ConfigFile(), Model());

        var unknownColumn = Assert.Throws<QueryValidationException>(() => builder.Build(new TabularReadRequest
        {
            Table = "Sales",
            Columns = ["not_a_column"],
        }));
        Assert.Contains("Unknown column", unknownColumn.Message);

        var unsupportedOperator = Assert.Throws<QueryValidationException>(() => builder.Build(new TabularReadRequest
        {
            Table = "Sales",
            Columns = ["Region"],
            Filters = [new TabularFilter { Column = "Region", Op = "like", Value = Value("west") }],
        }));
        Assert.Contains("not supported", unsupportedOperator.Message);
    }

    [Fact]
    public void Build_groups_measures_by_selected_columns()
    {
        var builder = new TabularDaxBuilder(new ConfigFile(), Model());

        var query = builder.Build(new TabularReadRequest
        {
            Table = "Sales",
            Columns = ["Region"],
            Measures = ["Revenue"],
            OrderBy = new TabularOrder { Column = "Revenue", Dir = "desc" },
        });

        Assert.Contains("""SUMMARIZECOLUMNS('Sales'[Region], "Revenue", [Revenue])""", query.Dax);
        Assert.EndsWith(", [Revenue], DESC)", query.Dax);
    }

    private static TabularModel Model()
    {
        var model = new TabularModel();
        var table = new TabularTable { Id = "1", Name = "Sales" };
        table.Columns.Add(new TabularColumn { Id = "11", Name = "Region", DataType = "string" });
        model.Tables.Add(table);
        model.Measures.Add(new TabularMeasure { Id = "21", TableId = "1", Name = "Revenue" });
        return model;
    }

    private static JsonElement Value(string value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();
}
