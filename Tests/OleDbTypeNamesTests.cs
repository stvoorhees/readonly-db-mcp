using ReadOnlyDbMcp.Tabular;
using Xunit;

namespace ReadOnlyDbMcp.Tests;

public sealed class OleDbTypeNamesTests
{
    [Theory]
    [InlineData(7, "datetime")]
    [InlineData(130, "string")]
    [InlineData(11, "boolean")]
    [InlineData(3, "integer")]
    [InlineData(131, "decimal")]
    [InlineData(999, "unknown")]
    public void FromLevelDbType_maps_standard_ole_db_codes(int code, string expected) =>
        Assert.Equal(expected, OleDbTypeNames.FromLevelDbType(code));

    [Fact]
    public void FromLevelDbType_returns_unknown_when_code_is_absent_or_invalid()
    {
        Assert.Equal("unknown", OleDbTypeNames.FromLevelDbType(null));
        Assert.Equal("unknown", OleDbTypeNames.FromLevelDbType("not-a-type"));
    }

    [Fact]
    public void FromDeepestLevel_uses_the_column_level_not_the_all_member_level()
    {
        var type = OleDbTypeNames.FromDeepestLevel([(0, (object?)3), (1, (object?)7)]);

        Assert.Equal("datetime", type);
    }
}
