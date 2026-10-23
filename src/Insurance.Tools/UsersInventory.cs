using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Insurance.Tools;

/// <summary>
/// Read-only inventory of the fictional regression users of a test database (legacy scripts/reg-users-inventory.ts; owner:
/// inventory first, nothing deleted): for each user whose name starts with the prefix — role, active flag, created time,
/// live and expired sessions, and how many rows of every table that references core.app_user point at it. Writes nothing;
/// no password hash, token or secret is read or printed.
/// </summary>
public static class UsersInventory
{
    public sealed record Row(Guid Id, string Username, string Role, bool Active, string CreatedAt, int LiveSessions, int ExpiredSessions, IReadOnlyList<string> Links);

    public static async Task<IReadOnlyList<Row>> ReadAsync(string connection, string prefix)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        await using var transaction = (SqlTransaction)await db.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot);
        // Every foreign key to core.app_user, as table.column.
        var refs = (await db.QueryAsync<(string Table, string Column)>("""
            select object_name(fk.parent_object_id), col_name(fkc.parent_object_id, fkc.parent_column_id)
            from sys.foreign_keys fk join sys.foreign_key_columns fkc on fkc.constraint_object_id = fk.object_id
            where fk.referenced_object_id = object_id(N'core.app_user') and schema_name(objectproperty(fk.parent_object_id, 'SchemaId')) = N'core'
            order by 1, 2
            """, transaction: transaction)).ToList();
        var users = (await db.QueryAsync<(Guid Id, string Username, string Role, bool Active, string CreatedAt)>("""
            select id, username, role_code, is_active, format(switchoffset(created_at, '+04:00'), 'yyyy-MM-dd HH:mm')
            from core.app_user where username like @prefix + N'%' order by created_at, username
            """, new { prefix }, transaction)).ToList();
        var rows = new List<Row>();
        foreach (var u in users)
        {
            var (live, expired) = await db.QuerySingleAsync<(int, int)>("""
                select count(case when expires_at > sysdatetimeoffset() then 1 end), count(case when expires_at <= sysdatetimeoffset() then 1 end)
                from core.session where user_id = @id
                """, new { id = u.Id }, transaction);
            var links = new List<string>();
            foreach (var (table, column) in refs.Where(r => r.Table != "session"))
            {
                var n = await db.ExecuteScalarAsync<int>($"select count(*) from core.{QuoteName(table)} where {QuoteName(column)} = @id", new { id = u.Id }, transaction);
                if (n > 0) links.Add($"{table}.{column} {n.ToString(CultureInfo.InvariantCulture)}");
            }
            rows.Add(new Row(u.Id, u.Username, u.Role, u.Active, u.CreatedAt, live, expired, links));
        }
        await transaction.CommitAsync();
        return rows;
    }

    private static string QuoteName(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static void Print(IReadOnlyList<Row> rows, Action<string> log)
    {
        log("| id | მომხმარებელი | როლი | აქტიური | შექმნა (თბილისი) | სესიები ცოცხალი / ვადაგასული | კავშირები (ცხრილი.სვეტი რაოდენობა) |");
        log("|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            log($"| {r.Id.ToString()[..8]} | {r.Username} | {r.Role} | {(r.Active ? "კი" : "არა")} | {r.CreatedAt} | {r.LiveSessions} / {r.ExpiredSessions} | {(r.Links.Count == 0 ? "—" : string.Join("; ", r.Links))} |");
        log($"SUMMARY users {rows.Count}, active {rows.Count(r => r.Active)}, with live sessions {rows.Count(r => r.LiveSessions > 0)}, referenced {rows.Count(r => r.Links.Count > 0)}, unreferenced {rows.Count(r => r.Links.Count == 0)}");
    }
}
