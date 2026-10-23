using System.Globalization;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Integration.Tests.LegacyDbCheck;

namespace Insurance.Integration.Tests.Accounting;

/// <summary>
/// Legacy scripts/close-period-regression.ts (Codex H25, H28, H30), scripts/p6-close-test.ts (the real month close end to end, legacy run on
/// its own database insurance_close_test) and the month-series part of scripts/permissions-db-check.ts (PR250), on a real SQL Server.
/// Closing is irreversible, so every test has a database of its own (the legacy regression could not close a month on the shared
/// insurance_test: its real close was SKIP; here it is run). No month is reopened and no trigger is bypassed: a closed month is made only
/// by the real close (PeriodClose.CloseAsync). Months are relative to today in Tbilisi (M, M−1, M−2, M−3), as in the legacy scripts.
/// </summary>
public sealed class LegacyMonthCloseTests
{
    private static readonly DateOnly M = new(Rates.TodayInTbilisi().Year, Rates.TodayInTbilisi().Month, 1);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly Back(int n) => M.AddMonths(-n);

    private static async Task<CurrentUser> UserAsync(TestDatabase db, string role) =>
        new(await Fixtures.UserAsync(db, role), "test." + role, "სატესტო მომხმარებელი", role, role);

    private static async Task<string> PeriodsAsync(TestDatabase db)
    {
        await using var c = await db.OpenAdminAsync();
        return string.Join("|", await c.QueryAsync<string>("""
            select concat(convert(char(10), period_start, 23), ':', status, ':', convert(nvarchar(40), closed_at, 127), ':', lower(convert(nchar(36), closed_by)))
            from core.accounting_period order by period_start
            """));
    }

    private static async Task<(string Status, DateTimeOffset? At, Guid? By)?> PeriodAsync(TestDatabase db, DateOnly month)
    {
        await using var c = await db.OpenAdminAsync();
        return await c.QuerySingleOrDefaultAsync<(string, DateTimeOffset?, Guid?)?>("select status, closed_at, closed_by from core.accounting_period where period_start = @month", new { month });
    }

    // ================================================================ scripts/close-period-regression.ts

    [Fact]
    public async Task Close_period_regression_every_press_answered_refusals_and_a_repeat_write_nothing_and_twenty_presses_get_their_own()
    {
        await using var w = await ReportWorld.CreateAsync();
        var db = w.Database;
        await w.PolicyAsync("P-REG", Iso(Back(3)), Iso(Back(3).AddYears(1)), [new ReportWorld.LineSpec("5.1", 365m, 365m)]);
        var accountant = await UserAsync(db, "accountant");
        var operatorUser = await UserAsync(db, "sales");
        var viewer = await UserAsync(db, "viewer");
        // The closed month the legacy run found on insurance_test: closed here by the real close.
        Assert.Equal(new PeriodClose.Answer("done", Iso(Back(3))), await PeriodClose.CloseAsync(w.App, accountant, Iso(Back(3))));
        var before = await PeriodsAsync(db);
        var last = Back(3);
        var nextOpen = last.AddMonths(1);
        var afterNext = nextOpen.AddMonths(1);
        var thisMonth = Iso(M);

        async Task Expect(string who, CurrentUser user, string month, string outcome)
        {
            var got = await PeriodClose.CloseAsync(w.App, user, month);
            var want = new PeriodClose.Answer(outcome, Insurance.Domain.Common.Month.IsMonthStart(month) ? month : "");
            Assert.True(want == got, $"{who} closes \"{month}\": expected {want}, got {got}");
            Assert.Equal(before, await PeriodsAsync(db));
        }

        await Expect("operator", operatorUser, Iso(nextOpen), "forbidden");
        await Expect("viewer", viewer, Iso(nextOpen), "forbidden");
        foreach (var bad in new[] { "2026-00-01", "2026-13-01", "2026-02-30", "0000-01-01", "2101-01-01", "abc", "" })
            await Expect("accountant", accountant, bad, "invalid");
        await Expect("accountant", accountant, thisMonth, "not-ended");
        Assert.True(afterNext < M);
        await Expect("accountant", accountant, Iso(afterNext), "earlier-open");
        // A month closed before: the answer says so, and its closing time and author stay as they were.
        await Expect("accountant", accountant, Iso(last), "already");

        // Presses answered at the same time each get their own answer back (Codex H30): 20 presses by the accountant at once,
        // alternating a month that has not ended and an impossible one.
        var together = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => PeriodClose.CloseAsync(w.App, accountant, i % 2 == 0 ? thisMonth : "2026-13-01")));
        for (var i = 0; i < 20; i++)
            Assert.Equal(i % 2 == 0 ? new PeriodClose.Answer("not-ended", thisMonth) : new PeriodClose.Answer("invalid", ""), together[i]);
        Assert.Equal(before, await PeriodsAsync(db));

        // The legacy SKIP ("a real close is irreversible on the shared insurance_test"): the accountant closes the oldest open month.
        Assert.Equal(new PeriodClose.Answer("done", Iso(nextOpen)), await PeriodClose.CloseAsync(w.App, accountant, Iso(nextOpen)));
        var closed = await PeriodAsync(db, nextOpen);
        Assert.Equal(("closed", accountant.Id), (closed!.Value.Status, closed.Value.By!.Value));
        Assert.NotNull(closed.Value.At);
    }

    // ================================================================ scripts/permissions-db-check.ts, the period close's month series (PR250)

    [Fact]
    public async Task PR250_a_first_policy_from_mid_month_cannot_step_over_a_month_that_is_still_open()
    {
        // permissions-db-check: from the raw day (2026-09-15) the series to 2026-11-01 skipped October, from the 1st it did not. Here the
        // series is the close's own: a first policy from the 15th of M−3, M−3 closed, M−2 open — closing M−1 must answer earlier-open.
        await using var w = await ReportWorld.CreateAsync();
        var first = Back(3).AddDays(14);
        await w.PolicyAsync("P-PR250", Iso(first), Iso(first.AddYears(1)), [new ReportWorld.LineSpec("5.1", 365m, 365m)]);
        var accountant = await UserAsync(w.Database, "accountant");
        // The raw-day series of the earlier code (generate_series from the first policy's own day, monthly, up to the month before
        // the one asked) would hold only M−3's 15th: M−2 would be stepped over.
        var raw = new List<DateOnly>();
        for (var d = first; d <= Back(2); d = d.AddMonths(1)) raw.Add(new DateOnly(d.Year, d.Month, 1));
        Assert.Equal([Back(3)], raw);
        Assert.Equal(new PeriodClose.Answer("done", Iso(Back(3))), await PeriodClose.CloseAsync(w.App, accountant, Iso(Back(3))));
        Assert.Equal(new PeriodClose.Answer("earlier-open", Iso(Back(1))), await PeriodClose.CloseAsync(w.App, accountant, Iso(Back(1))));
        Assert.Null(await PeriodAsync(w.Database, Back(1)));
        Assert.Equal(new PeriodClose.Answer("done", Iso(Back(2))), await PeriodClose.CloseAsync(w.App, accountant, Iso(Back(2))));
        Assert.Equal(new PeriodClose.Answer("done", Iso(Back(1))), await PeriodClose.CloseAsync(w.App, accountant, Iso(Back(1))));
    }

    // ================================================================ scripts/p6-close-test.ts

    [Fact]
    public async Task P6_a_real_month_close_end_to_end()
    {
        var db = new TestDatabase();
        await db.InitializeAsync();
        try
        {
            var w = new World(db);
            Guid? Id(CurrentUser u) => u.Id;
            var accountant = await UserAsync(db, "accountant");
            var head = await UserAsync(db, "admin");
            var operatorUser = await UserAsync(db, "sales");
            var (m1, m2, m3) = (Back(1), Back(2), Back(3));
            Assert.Equal("", await PeriodsAsync(db));

            // ---- Fixture: a policy from M−3, 365.00 in four 91.25, approved by the head through the application; a receipt of 91.25 dated
            // M−3's 5th, not yet known which policy it pays (P7).
            var client = await w.ClientAsync();
            var policy = await w.PendingPolicyAsync(client, m3);
            Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, head, policy.Id.ToString())).Outcome);
            var receipt = await w.ReceiptAsync(client, m3.AddDays(4), 91.25m, head.Id, "P6 receipt");
            Task<string> Press(CurrentUser u, DateOnly month) => PeriodClose.CloseAsync(w.App, u, Iso(month)).ContinueWith(t => t.Result.Outcome);

            // ---- Refusals and order, nothing written.
            Assert.Equal("forbidden", await Press(operatorUser, m3)); // C9
            Assert.Null(await PeriodAsync(db, m3));
            Assert.Equal("not-ended", await Press(accountant, M)); // C3
            Assert.Null(await PeriodAsync(db, M));
            Assert.Equal("earlier-open", await Press(accountant, m2)); // C4
            Assert.Null(await PeriodAsync(db, m2));

            // ---- C1, C2: the first real close, then a repeat.
            Assert.Equal("done", await Press(accountant, m3));
            var afterC1 = (await PeriodAsync(db, m3))!.Value;
            Assert.Equal(("closed", Id(accountant)), (afterC1.Status, afterC1.By));
            Assert.NotNull(afterC1.At);
            Assert.Equal("already", await Press(accountant, m3));
            Assert.Equal(afterC1, (await PeriodAsync(db, m3))!.Value);

            // ---- C5: two real transactions close M−2 at once, the period lock held by a third session; both seen waiting before it is let go.
            await using (var holder = await HoldAsync(db, head.Id))
            {
                await holder.Run("exec core.xact_lock N'core.accounting_period'");
                var both = new[] { Press(accountant, m2), Press(accountant, m2) };
                var waiting = 0;
                await using (var monitor = await db.OpenAdminAsync())
                    for (var i = 0; i < 100 && waiting < 2; i++)
                    {
                        await Task.Delay(100);
                        waiting = await monitor.ExecuteScalarAsync<int>(
                            "select count(*) from sys.dm_tran_locks where resource_type = N'APPLICATION' and request_status = N'WAIT' and resource_database_id = db_id()");
                    }
                Assert.Equal(2, waiting);
                Assert.All(both, t => Assert.False(t.IsCompleted));
                await holder.Transaction.RollbackAsync();
                Assert.Equal(["already", "done"], (await Task.WhenAll(both)).Order(StringComparer.Ordinal));
            }
            Assert.Equal("closed", (await PeriodAsync(db, m2))!.Value.Status);
            Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.audit_log where table_name = N'accounting_period' and row_id = @row", new { row = Iso(m2) }));

            // ---- C6: nothing is booked into a closed month.
            var line = await w.ScalarAsync<Guid>("select id from core.policy_line where policy_id = @id", new { id = policy.Id });
            Assert.Equal("23514", await BareCodeOf(() => w.App.AsUserAsync(head.Id, tx => tx.ExecuteAsync("""
                insert core.premium_transaction (policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_by)
                values (@line, N'adjustment', 1, 1, @on, @from, @to, @by)
                """, new { line, on = m3.AddDays(10), from = m3, to = m3.AddDays(365), by = head.Id })))); // C6a
            Task<int> Receipts() => w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @client", new { client });
            var receiptsBefore = await Receipts();
            var c6b = await PaymentActions.RecordPaymentAsync(w.App, w.Rates, head, new Dictionary<string, string>
            {
                ["policy_id"] = policy.Id.ToString(), ["amount_gel"] = "10.00", ["received_on"] = Iso(m3.AddDays(19)), ["method"] = "bank", ["reference"] = "P6 late",
            });
            Assert.Equal(BillingTexts.Payment.Errors.PeriodClosed, c6b.Errors["received_on"]); // C6b
            Assert.Null(c6b.Redirect);
            Assert.Equal(receiptsBefore, await Receipts());

            // ---- C7 (P7): the receipt that arrived in closed M−3 is allocated today.
            var c7 = await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, head, policy.Id.ToString(), receipt.ToString());
            Assert.StartsWith($"/policies/{policy.Id}?advance=allocated", c7);
            var allocations = await w.QueryAsync<(DateOnly BookedOn, decimal Amount)>("select booked_on, amount from core.payment_allocation where payment_id = @receipt", new { receipt });
            Assert.NotEmpty(allocations);
            Assert.All(allocations, a => Assert.Equal(World.Today, a.BookedOn));
            Assert.Equal(91.25m, allocations.Sum(a => a.Amount));
            Assert.Equal(m3.AddDays(4), await w.ScalarAsync<DateOnly>("select received_on from core.payment where id = @receipt", new { receipt }));
            Assert.Equal(receiptsBefore, await Receipts());
            Assert.Equal(afterC1, (await PeriodAsync(db, m3))!.Value);

            // ---- C8: the closed month cannot be changed or removed, by the application or by the table's owner.
            async Task<string> AsApp(string sql) => await BareCodeOf(() => w.App.AsUserAsync(head.Id, tx => tx.ExecuteAsync(sql, new { m3, m1, head = head.Id })));
            async Task<string> AsOwner(string sql) => await BareCodeOf(async () =>
            {
                await using var c = await db.OpenAdminAsync();
                await c.ExecuteAsync(sql, new { m3, m1, head = head.Id });
            });
            Assert.Equal("42501", await AsApp("delete from core.accounting_period where period_start = @m3"));
            Assert.Equal("23514", await AsApp("update core.accounting_period set status = N'open', closed_at = null, closed_by = null where period_start = @m3"));
            Assert.Equal("23514", await AsOwner("delete from core.accounting_period where period_start = @m3"));
            Assert.Equal("23514", await AsOwner("update core.accounting_period set period_start = @m1 where period_start = @m3"));
            Assert.Equal("23514", await AsOwner("update core.accounting_period set closed_at = dateadd(day, -1, closed_at) where period_start = @m3"));
            Assert.Equal("23514", await AsOwner("update core.accounting_period set closed_by = @head where period_start = @m3"));
            Assert.Equal("ok", await AsApp("update core.accounting_period set status = status where period_start = @m3"));
            Assert.Equal(afterC1, (await PeriodAsync(db, m3))!.Value);

            // ---- C11a: who closed, when, from what to what — two inserts, M−3 and M−2, by the accountant; refused changes left nothing.
            var audit = await w.QueryAsync<(string Action, Guid? User, string Row, string? Status)>(
                "select action, user_id, row_id, json_value(new_row, '$.status') from core.audit_log where table_name = N'accounting_period' order by id");
            Assert.Equal([("insert", Id(accountant), Iso(m3), "closed"), ("insert", Id(accountant), Iso(m2), "closed")], audit);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }
}
