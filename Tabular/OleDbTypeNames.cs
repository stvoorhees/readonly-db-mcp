using System.Globalization;

namespace ReadOnlyDbMcp.Tabular;

/// <summary>
/// Names the standard OLE DB DBTYPE values returned by MDSCHEMA_LEVELS. SSAS schema rowsets
/// expose these numeric codes when DMV column metadata is unavailable to model readers.
/// </summary>
public static class OleDbTypeNames
{
    public static string FromDeepestLevel(IEnumerable<(int LevelNumber, object? DbType)> levels)
    {
        var deepest = levels.OrderByDescending(level => level.LevelNumber).FirstOrDefault();
        return deepest == default ? "unknown" : FromLevelDbType(deepest.DbType);
    }

    public static string FromLevelDbType(object? value)
    {
        if (value is null ||
            !int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            return "unknown";

        return code switch
        {
            2 or 3 or 16 or 17 or 18 or 19 or 20 or 21 => "integer",
            4 or 5 => "number",
            6 or 14 or 131 => "decimal",
            7 or 133 or 134 or 135 or 146 => "datetime",
            8 or 129 or 130 => "string",
            11 => "boolean",
            72 => "guid",
            128 => "binary",
            _ => "unknown",
        };
    }
}
