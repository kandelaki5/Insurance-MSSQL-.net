using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Insurance.Tools.DataMigration;

/// <summary>
/// Compares the old PostgreSQL database with the SQL Server database it was imported into: every table's row count and
/// keys, the money of every ledger by currency, policy, claim and month, the schedules and what is owed on them, the
/// clients' advances, reserves, payments, recoveries, cessions, regress balances, the months' status, the number series,
/// and the relations (no orphan rows). Both sides are read in one snapshot each; nothing is written.
/// </summary>
public sealed class Reconcile(string postgresConnection, string sqlServerConnection)
{
    public sealed record Check(string Name, bool Equal, int Rows, string? FirstDifference);

    /// <summary>An aggregate computed the same way in both databases: rows of (key, value) texts.</summary>
    private sealed record Figure(string Name, string Postgres, string SqlServer);

    // Amounts are compared as exact decimals; keys are uuids as lower-case text and dates as yyyy-mm-dd on both sides.
    private static readonly Figure[] Figures =
    [
        new("premium by policy, currency and booking month",
            "select p.id::text || '|' || p.premium_currency || '|' || to_char(t.booked_on, 'YYYY-MM') as k, sum(t.amount)::text || '|' || sum(t.amount_gel)::text as v from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id group by 1",
            "select lower(convert(nchar(36), p.id)) + '|' + p.premium_currency + '|' + format(t.booked_on, 'yyyy-MM') as k, convert(nvarchar(50), sum(t.amount)) + '|' + convert(nvarchar(50), sum(t.amount_gel)) as v from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id group by lower(convert(nchar(36), p.id)) + '|' + p.premium_currency + '|' + format(t.booked_on, 'yyyy-MM')"),
        new("premium by line and kind",
            "select policy_line_id::text || '|' || kind as k, sum(amount)::text || '|' || sum(amount_gel)::text || '|' || count(*) as v from core.premium_transaction group by 1",
            "select lower(convert(nchar(36), policy_line_id)) + '|' + kind as k, convert(nvarchar(50), sum(amount)) + '|' + convert(nvarchar(50), sum(amount_gel)) + '|' + convert(nvarchar(20), count(*)) as v from core.premium_transaction group by lower(convert(nchar(36), policy_line_id)) + '|' + kind"),
        new("schedule per policy (instalments plus adjustments)",
            "select i.policy_id::text as k, (sum(i.amount) + coalesce((select sum(j.change) from core.installment_adjustment j join core.installment x on x.id = j.installment_id where x.policy_id = i.policy_id), 0))::text as v from core.installment i group by i.policy_id",
            "select lower(convert(nchar(36), i.policy_id)) as k, convert(nvarchar(50), sum(i.amount) + coalesce((select sum(j.change) from core.installment_adjustment j join core.installment x on x.id = j.installment_id where x.policy_id = i.policy_id), 0)) as v from core.installment i group by i.policy_id"),
        new("settled per instalment (allocations, policy currency and GEL)",
            "select installment_id::text as k, sum(amount)::text || '|' || sum(amount_gel)::text as v from core.payment_allocation group by installment_id",
            "select lower(convert(nchar(36), installment_id)) as k, convert(nvarchar(50), sum(amount)) + '|' + convert(nvarchar(50), sum(amount_gel)) as v from core.payment_allocation group by installment_id"),
        new("owed per policy (schedule less settled)",
            "select i.policy_id::text as k, (sum(i.amount) + coalesce((select sum(j.change) from core.installment_adjustment j join core.installment x on x.id = j.installment_id where x.policy_id = i.policy_id), 0) - coalesce((select sum(a.amount) from core.payment_allocation a join core.installment x on x.id = a.installment_id where x.policy_id = i.policy_id), 0))::text as v from core.installment i group by i.policy_id",
            "select lower(convert(nchar(36), i.policy_id)) as k, convert(nvarchar(50), sum(i.amount) + coalesce((select sum(j.change) from core.installment_adjustment j join core.installment x on x.id = j.installment_id where x.policy_id = i.policy_id), 0) - coalesce((select sum(a.amount) from core.payment_allocation a join core.installment x on x.id = a.installment_id where x.policy_id = i.policy_id), 0)) as v from core.installment i group by i.policy_id"),
        new("receipts, reversals and refunds by kind, method and month",
            "select kind || '|' || method || '|' || to_char(received_on, 'YYYY-MM') as k, sum(amount_gel)::text || '|' || count(*) as v from core.payment group by 1",
            "select kind + '|' + method + '|' + format(received_on, 'yyyy-MM') as k, convert(nvarchar(50), sum(amount_gel)) + '|' + convert(nvarchar(20), count(*)) as v from core.payment group by kind + '|' + method + '|' + format(received_on, 'yyyy-MM')"),
        new("advance (unallocated) per client",
            "select coalesce(p.payer_party_id::text, '-') as k, sum(p.amount_gel - coalesce((select sum(a.amount_gel) from core.payment_allocation a where a.payment_id = p.id), 0))::text as v from core.payment p group by 1",
            "select coalesce(lower(convert(nchar(36), p.payer_party_id)), '-') as k, convert(nvarchar(50), sum(p.amount_gel - coalesce(a.allocated, 0))) as v from core.payment p outer apply (select sum(x.amount_gel) as allocated from core.payment_allocation x where x.payment_id = p.id) a group by coalesce(lower(convert(nchar(36), p.payer_party_id)), '-')"),
        new("reserve level per claim",
            "select claim_id::text as k, sum(change)::text as v from core.claim_reserve group by claim_id",
            "select lower(convert(nchar(36), claim_id)) as k, convert(nvarchar(50), sum(change)) as v from core.claim_reserve group by claim_id"),
        new("claim payments per claim",
            "select claim_id::text as k, sum(amount)::text || '|' || sum(deductible_withheld)::text || '|' || sum(premium_offset)::text || '|' || sum(net_paid)::text || '|' || sum(net_paid_gel)::text as v from core.claim_payment group by claim_id",
            "select lower(convert(nchar(36), claim_id)) as k, convert(nvarchar(50), sum(amount)) + '|' + convert(nvarchar(50), sum(deductible_withheld)) + '|' + convert(nvarchar(50), sum(premium_offset)) + '|' + convert(nvarchar(50), sum(net_paid)) + '|' + convert(nvarchar(50), sum(net_paid_gel)) as v from core.claim_payment group by claim_id"),
        new("recoveries per claim and kind",
            "select claim_id::text || '|' || kind as k, sum(amount_gel)::text as v from core.claim_recovery group by 1",
            "select lower(convert(nchar(36), claim_id)) + '|' + kind as k, convert(nvarchar(50), sum(amount_gel)) as v from core.claim_recovery group by lower(convert(nchar(36), claim_id)) + '|' + kind"),
        new("premium cessions per treaty and month",
            "select treaty_id::text || '|' || to_char(booked_on, 'YYYY-MM') as k, sum(amount)::text || '|' || sum(amount_gel)::text || '|' || sum(commission)::text || '|' || sum(commission_gel)::text as v from core.premium_cession group by 1",
            "select lower(convert(nchar(36), treaty_id)) + '|' + format(booked_on, 'yyyy-MM') as k, convert(nvarchar(50), sum(amount)) + '|' + convert(nvarchar(50), sum(amount_gel)) + '|' + convert(nvarchar(50), sum(commission)) + '|' + convert(nvarchar(50), sum(commission_gel)) as v from core.premium_cession group by lower(convert(nchar(36), treaty_id)) + '|' + format(booked_on, 'yyyy-MM')"),
        new("claim shares per claim and treaty",
            "select claim_id::text || '|' || treaty_id::text as k, share_percent::text || '|' || coalesce(priority::text, '-') || '|' || coalesce(treaty_limit::text, '-') as v from core.claim_cession",
            "select lower(convert(nchar(36), claim_id)) + '|' + lower(convert(nchar(36), treaty_id)) as k, convert(nvarchar(50), share_percent) + '|' + coalesce(convert(nvarchar(50), priority), '-') + '|' + coalesce(convert(nvarchar(50), treaty_limit), '-') as v from core.claim_cession"),
        new("regress movements per case and kind",
            "select case_id::text || '|' || kind as k, sum(amount_gel)::text || '|' || count(*) as v from core.subrogation_movement group by 1",
            "select lower(convert(nchar(36), case_id)) + '|' + kind as k, convert(nvarchar(50), sum(amount_gel)) + '|' + convert(nvarchar(20), count(*)) as v from core.subrogation_movement group by lower(convert(nchar(36), case_id)) + '|' + kind"),
        new("accounting months",
            "select period_start::text as k, status || '|' || coalesce(closed_by::text, '-') || '|' || coalesce((extract(epoch from closed_at) * 1000000)::bigint::text, '-') as v from core.accounting_period",
            "select convert(nvarchar(10), period_start, 23) as k, status + '|' + coalesce(lower(convert(nchar(36), closed_by)), '-') + '|' + coalesce(convert(nvarchar(30), datediff_big(microsecond, '1970-01-01T00:00:00+00:00', closed_at)), '-') as v from core.accounting_period"),
        new("number series",
            "select code as k, prefix || '|' || next_value || '|' || padding as v from core.number_sequence",
            "select code as k, prefix + '|' + convert(nvarchar(20), next_value) + '|' + convert(nvarchar(20), padding) as v from core.number_sequence"),
        new("client name and reporting segment (computed in SQL Server)",
            "select id::text as k, coalesce(display_name, '-') || '|' || segment as v from core.party",
            "select lower(convert(nchar(36), id)) as k, coalesce(display_name, '-') + '|' + segment as v from core.party"),
        new("audit log",
            "select id::text as k, table_name || '|' || row_id || '|' || action || '|' || coalesce(user_id::text, '-') as v from core.audit_log",
            "select convert(nvarchar(20), id) as k, table_name + '|' + row_id + '|' + action + '|' + coalesce(lower(convert(nchar(36), user_id)), '-') as v from core.audit_log"),
    ];

    public async Task<IReadOnlyList<Check>> RunAsync()
    {
        await using var pg = new NpgsqlConnection(postgresConnection);
        await pg.OpenAsync();
        await using var pgTx = await pg.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await pg.ExecuteAsync("set transaction read only", transaction: pgTx);
        await using var db = new SqlConnection(sqlServerConnection);
        await db.OpenAsync();
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot);

        var checks = new List<Check>();
        var tables = (await db.QueryAsync<string>("select name from sys.tables where schema_id = schema_id(N'core') order by name", transaction: tx))
            .Where(t => !PostgresImport.TargetOnly.Contains(t) && !PostgresImport.NotCopied.Contains(t)).ToList();
        foreach (var table in tables)
        {
            var keys = (await db.QueryAsync<string>("""
                select c.name from sys.indexes i join sys.index_columns ic on ic.object_id = i.object_id and ic.index_id = i.index_id
                join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
                where i.object_id = object_id(@name) and i.is_primary_key = 1 order by ic.key_ordinal
                """, new { name = $"core.{table}" }, tx)).ToList();
            var types = (await db.QueryAsync<(string Name, string Type)>("select c.name, t.name from sys.columns c join sys.types t on t.user_type_id = c.user_type_id where c.object_id = object_id(@name)",
                new { name = $"core.{table}" }, tx)).ToDictionary(c => c.Name, c => c.Type);
            var pgKey = string.Join(" || '|' || ", keys.Select(k => $"\"{k}\"::text"));
            var sqlKey = string.Join(" + '|' + ", keys.Select(k => types[k] switch
            {
                "uniqueidentifier" => $"lower(convert(nchar(36), [{k}]))",
                "date" => $"convert(nvarchar(10), [{k}], 23)",
                "nchar" => $"rtrim([{k}])",
                _ => $"convert(nvarchar(400), [{k}])",
            }));
            var pgKeys = (await pg.QueryAsync<string>($"select {pgKey} from core.\"{table}\"", transaction: pgTx)).Select(k => k.TrimEnd()).Order(StringComparer.Ordinal).ToList();
            var sqlKeys = (await db.QueryAsync<string>($"select {sqlKey} from core.[{table}]", transaction: tx)).Order(StringComparer.Ordinal).ToList();
            var first = pgKeys.Except(sqlKeys).Select(k => "only in PostgreSQL: " + k).Concat(sqlKeys.Except(pgKeys).Select(k => "only in SQL Server: " + k)).FirstOrDefault();
            checks.Add(new Check($"rows and keys of core.{table} ({pgKeys.Count} / {sqlKeys.Count}, {Digest(pgKeys)[..12]} / {Digest(sqlKeys)[..12]})",
                pgKeys.Count == sqlKeys.Count && first is null, pgKeys.Count, first));
        }

        foreach (var f in Figures)
        {
            var left = (await pg.QueryAsync<(string K, string V)>(f.Postgres, transaction: pgTx)).ToDictionary(r => r.K, r => Number(r.V), StringComparer.Ordinal);
            var right = (await db.QueryAsync<(string K, string V)>(f.SqlServer, transaction: tx)).ToDictionary(r => r.K, r => Number(r.V), StringComparer.Ordinal);
            var diff = left.Keys.Union(right.Keys)
                .Where(k => !left.TryGetValue(k, out var a) || !right.TryGetValue(k, out var b) || a != b)
                .Select(k => $"{k}: {left.GetValueOrDefault(k, "—")} / {right.GetValueOrDefault(k, "—")}")
                .FirstOrDefault();
            checks.Add(new Check(f.Name, diff is null, left.Count, diff));
        }

        // Relations: every foreign key and check validated over all rows (trusted), and no regress money without its movement.
        var untrusted = (await db.QueryAsync<string>("""
            select name from sys.foreign_keys where schema_id = schema_id(N'core') and (is_not_trusted = 1 or is_disabled = 1)
            union all select name from sys.check_constraints where schema_id = schema_id(N'core') and (is_not_trusted = 1 or is_disabled = 1)
            """, transaction: tx)).ToList();
        checks.Add(new Check("every foreign key and check constraint holds for every row (trusted)", untrusted.Count == 0, untrusted.Count, untrusted.FirstOrDefault()));
        var orphanMoney = await db.ExecuteScalarAsync<int>("select count(*) from core.case_recovery_without_movement", transaction: tx);
        checks.Add(new Check("regress money without its movement", orphanMoney == 0, orphanMoney, orphanMoney == 0 ? null : $"{orphanMoney} rows"));
        return checks;
    }

    /// <summary>"12.30|5" and "12.3|5" are the same figures: every part that is a number is compared as a number.</summary>
    private static string Number(string value) =>
        string.Join('|', value.Split('|').Select(p =>
            decimal.TryParse(p, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture) : p));

    private static string Digest(IEnumerable<string> keys) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keys))));
}
