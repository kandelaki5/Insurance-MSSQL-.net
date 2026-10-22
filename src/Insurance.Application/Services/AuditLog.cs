using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insurance.Application.Data;

namespace Insurance.Application.Services;

/// <summary>
/// The shared change log (owner, 17.10.2026, docs/05 ვ8): a read-only view of core.audit_log, newest first, filtered by
/// day (Tbilisi), table and user, a page at a time (legacy src/lib/audit-log.ts). Nothing here writes. Who may see it is
/// decided before this is called (the audit page, audit.log.view).
/// </summary>
public static partial class AuditLog
{
    /// <summary>Rows on one page; one more is read to know whether there is a next page.</summary>
    public const int PageSize = 50;

    public sealed record Filter(DateOnly? From, DateOnly? To, string? Table, Guid? UserId, long? Before);

    public sealed record Entry(long Id, string At, string TableName, string RowId, string Action, string? UserName, string? OldRow, string? NewRow);

    [GeneratedRegex("^[a-z][a-z_]{0,62}$")]
    private static partial Regex TableName();

    [GeneratedRegex(@"^[1-9]\d{0,18}$")]
    private static partial Regex Cursor();

    /// <summary>The filter as given in the address: anything empty, malformed or unknown is dropped, never passed on.</summary>
    public static Filter Parse(Func<string, string?> param)
    {
        string One(string key) => (param(key) ?? "").Trim();
        DateOnly? Day(string v) => DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var before = One("before");
        long? cursor = Cursor().IsMatch(before) && long.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : null;
        return new Filter(Day(One("from")), Day(One("to")), TableName().IsMatch(One("table")) ? One("table") : null,
            Guid.TryParseExact(One("user"), "D", out var u) ? u : null, cursor);
    }

    /// <summary>One page of the log under the filter, newest first by its own id, and the cursor of the next page (null at the end).</summary>
    public static async Task<(IReadOnlyList<Entry> Rows, long? Next)> ReadPageAsync(Tx db, Filter f)
    {
        var rows = (await db.QueryAsync<Entry>("""
            select top (@take) a.id as Id,
                   format(switchoffset(a.at, '+04:00'), 'dd.MM.yyyy HH:mm:ss') as At, a.table_name as TableName, a.row_id as RowId, a.action as Action,
                   u.full_name as UserName, a.old_row as OldRow, a.new_row as NewRow
            from core.audit_log a left join core.app_user u on u.id = a.user_id
            where (@from is null or convert(date, switchoffset(a.at, '+04:00')) >= @from)
              and (@to is null or convert(date, switchoffset(a.at, '+04:00')) <= @to)
              and (@table is null or a.table_name = @table)
              and (@userId is null or a.user_id = @userId)
              and (@before is null or a.id < @before)
            order by a.id desc
            """, new { take = PageSize + 1, from = f.From, to = f.To, table = f.Table, userId = f.UserId, before = f.Before })).ToList();
        var page = rows.Take(PageSize).ToList();
        return (page, rows.Count > PageSize ? page[^1].Id : null);
    }

    [GeneratedRegex("password|token|secret|hash", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    public sealed record Change(string Field, string From, string To);

    /// <summary>What a logged change did, field by field: for an insert what was set, for a delete what was there, for an update what moved.</summary>
    public static IReadOnlyList<Change> Changes(string? oldRow, string? newRow)
    {
        var before = oldRow is null ? new JsonObject() : JsonNode.Parse(oldRow)!.AsObject();
        var after = newRow is null ? new JsonObject() : JsonNode.Parse(newRow)!.AsObject();
        static string Json(JsonNode? n) => n is null ? "null" : n.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        static string Text(JsonNode? n) => n switch
        {
            null => "—",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>() ? "true" : "false",
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsNumberText(v),
            _ => Json(n),
        };
        return before.Select(p => p.Key).Concat(after.Select(p => p.Key)).Distinct()
            .Where(key => !Secret().IsMatch(key) && Json(before[key]) != Json(after[key]))
            .Order(StringComparer.Ordinal)
            .Select(key => new Change(key, Text(before[key]), Text(after[key])))
            .ToList();
    }

    /// <summary>A JSON number as JavaScript's String(n) shows it (10000.00 → "10000").</summary>
    private static string JsNumberText(JsonValue v) =>
        double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
}
