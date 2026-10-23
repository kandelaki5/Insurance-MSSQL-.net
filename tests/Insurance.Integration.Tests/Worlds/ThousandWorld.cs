using Dapper;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests;

/// <summary>
/// The world of the legacy report-1000-world.ts: the old policy Q (GEL 120.00 from 01.10.2026, paid on 05.10; October closed) and a thousand
/// policies P from 01.11.2026, each with its own client and contract, two 5.1 lines (300.00 + 100.00), four instalments of 100.00, the first paid.
/// </summary>
public static class ThousandWorld
{
    public const int N = 1000;

    /// <summary>Q and October closed, then the thousand P written set-wise, in creation order forward or reversed.</summary>
    public static async Task<ReportWorld> CreateAsync(bool reverse = false)
    {
        var w = await ReportWorld.CreateAsync();
        var q = await w.PolicyAsync("P-TEST-Q", "2026-10-01", "2027-10-01", [new LineSpec("5.1", 120m, 120m)], [("2026-10-01", 120m, "client")], contractNumber: "C-TEST-Q");
        await w.PayAsync(q.Installments[0], "2026-10-05", 120m);
        await w.ClosePeriodAsync("2026-10");
        await w.WriteAsync(tx => tx.ExecuteAsync("""
            declare @n table (i int primary key, k int, party uniqueidentifier, contract uniqueidentifier, policy uniqueidentifier, l1 uniqueidentifier, l2 uniqueidentifier,
                              t1 uniqueidentifier, t2 uniqueidentifier, i1 uniqueidentifier, i2 uniqueidentifier, i3 uniqueidentifier, i4 uniqueidentifier, pay uniqueidentifier);
            insert @n (i, k, party, contract, policy, l1, l2, t1, t2, i1, i2, i3, i4, pay)
            select i, case when @reverse = 1 then @count - 1 - i else i end, newid(), newid(), newid(), newid(), newid(), newid(), newid(), newid(), newid(), newid(), newid(), newid()
            from (select top (@count) row_number() over (order by (select null)) - 1 as i from sys.all_objects a cross join sys.all_objects b) x;
            insert core.party (id, kind, foreign_id, name, is_resident, country_code, created_at)
              select party, N'organization', concat(N'F-P-', i), concat(N'ტესტი კლიენტი ', i), 1, N'GE', dateadd(second, k, cast('2026-11-03T08:00:00+04:00' as datetimeoffset)) from @n;
            insert core.contract (id, number, signed_on, policyholder_id, created_at, created_by)
              select contract, concat(N'C-TEST-', format(i, 'D4')), '2026-11-01', party, dateadd(second, k, cast('2026-11-03T09:00:00+04:00' as datetimeoffset)), @user from @n;
            insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, created_at, created_by, segment, approved_at, approved_by)
              select policy, contract, concat(N'P-TEST-', format(i, 'D4')), '2026-11-01', '2026-11-01', '2027-11-01', N'active', N'GEL', 1, dateadd(second, k, cast('2026-11-03T09:30:00+04:00' as datetimeoffset)), @user,
                     N'corporate', sysdatetimeoffset(), @user from @n;
            insert core.policy_line (id, policy_id, class_code, premium, premium_gel)
              select l1, policy, N'5.1', 300, 300 from @n union all select l2, policy, N'5.1', 100, 100 from @n;
            insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_at, created_by)
              select t1, l1, N'issue', 300, 300, '2026-11-01', '2026-11-01', '2027-11-01', dateadd(second, 2 * k, cast('2026-11-03T10:00:00+04:00' as datetimeoffset)), @user from @n
              union all select t2, l2, N'issue', 100, 100, '2026-11-01', '2026-11-01', '2027-11-01', dateadd(second, 2 * k + 1, cast('2026-11-03T10:00:00+04:00' as datetimeoffset)), @user from @n;
            insert core.installment (id, policy_id, due_on, amount, debtor_kind)
              select i1, policy, '2026-11-01', 100, N'client' from @n union all select i2, policy, '2027-02-01', 100, N'client' from @n
              union all select i3, policy, '2027-05-01', 100, N'client' from @n union all select i4, policy, '2027-08-01', 100, N'client' from @n;
            insert core.payment (id, received_on, amount_gel, method, payer_party_id, created_at, created_by)
              select pay, '2026-11-02', 100, N'bank', party, dateadd(second, k, cast('2026-11-03T12:00:00+04:00' as datetimeoffset)), @user from @n;
            insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on, created_at)
              select pay, i1, 100, 100, 1, '2026-11-03', dateadd(second, k, cast('2026-11-03T12:00:00+04:00' as datetimeoffset)) from @n;
            """, new { count = N, reverse, user = w.Admin }));
        return w;
    }

}
