using System.Globalization;
using System.Text.Json;

namespace Insurance.Tools.DataMigration;

/// <summary>One text per value, the same whichever database it was read from, so rows and keys can be compared.</summary>
public static class Canon
{
    public static string Of(object? value) => value switch
    {
        null or DBNull => "∅",
        Guid g => g.ToString("D"),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.TimeOfDay == TimeSpan.Zero && t.Kind != DateTimeKind.Utc ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Micro(new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc))),
        DateTimeOffset o => Micro(o),
        decimal m => m.ToString("0.############################", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        string[] a => JsonSerializer.Serialize(a),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Micro(DateTimeOffset o) => (o.UtcTicks / 10).ToString(CultureInfo.InvariantCulture);

    public static string Row(IEnumerable<object?> values) => string.Join("␟", values.Select(Of));
}
