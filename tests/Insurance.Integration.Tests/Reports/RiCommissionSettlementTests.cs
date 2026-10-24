using System.Numerics;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Microsoft.Data.SqlClient;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The reinsurer's commission receivable (N1-4 00140) and its settlements on a real SQL Server (owner, 20.10.2026, docs/05 3ე):
/// the commission on 100% reinsured policies is accrued with the premium and stays a receivable until it is offset against what is
/// owed to the reinsurer or received from it; the accountant records the settlement per treaty as a total; 00140 is valued at the
/// reporting date's rate. Port of Insurance-Software PR #362 (head 540e81a) scripts/integration/ri-commission.test.ts — the old test ran
/// the real reader and actions over an in-memory store with the database rules modelled by name; here they run over real rows, the
/// real triggers (closed month, RC001, one reversal) and the application role. The database checks of the old
/// docs/tests/2026-10-20-ri-commission-db-check.md (real PostgreSQL) are the last tests. The fictional world is the old test's
/// (RiCommissionWorld): on 31.12.2026 accrued 300.00 USD, nothing settled → 300.00 × 2.7015 = 810.45 GEL in 00140.
/// </summary>
public sealed class RiCommissionSettlementTests
{
    private sealed record World(ReportWorld W, Guid Front, Guid Quota, Guid Viewer, Guid UsdIssue)
    {
        public CurrentUser Accountant => new(W.Accountant, "test.accountant", "სატესტო ბუღალტერი", "accountant", "accountant");
        public CurrentUser ViewerUser => new(Viewer, "test.viewer", "სატესტო მნახველი", "viewer", "viewer");
    }

    private static async Task<World> WorldAsync()
    {
        var w = await ReportWorld.CreateAsync();
        var ri = await RiCommissionWorld.BuildAsync(w);
        var viewer = await Fixtures.UserAsync(w.Database, "viewer");
        return new World(w, ri.Front, ri.Quota, viewer, ri.UsdIssue);
    }

    private static Task<RiCommissionLedger.Report> ReportAsync(World x, string asOf) =>
        x.W.App.ReadTxAsync(tx => RiCommissionLedger.BuildReportAsync(tx, x.W.Rates().ValueAsync, D(asOf)));

    private static async Task<string> RecordAsync(World x, string settledOn, string amount, string kind = "offset", string currency = "USD", Guid? treaty = null,
        string? reference = null, CurrentUser? user = null) =>
        (await RiCommissionLedger.RecordAsync(x.W.App, user ?? x.Accountant, Uuid.Text(treaty ?? x.Front), settledOn, kind, currency, amount, reference, null)).Outcome;

    private static async Task<string> ReverseAsync(World x, Guid settlement, string reversedOn) =>
        (await RiCommissionLedger.ReverseAsync(x.W.App, x.Accountant, Uuid.Text(settlement), reversedOn)).Outcome;

    private static async Task<List<(Guid Id, Guid TreatyId, DateOnly SettledOn, string Kind, string Currency, decimal Amount, Guid? ReversesId, string? Reference, string? Note, DateTimeOffset CreatedAt, Guid CreatedBy)>> RowsAsync(World x)
    {
        await using var c = await x.W.Database.OpenAdminAsync();
        return (await c.QueryAsync<(Guid, Guid, DateOnly, string, string, decimal, Guid?, string?, string?, DateTimeOffset, Guid)>(
            "select id, treaty_id, settled_on, kind, currency, amount, reverses_id, reference, note, created_at, created_by from core.ri_commission_settlement order by created_at, id")).ToList();
    }

    private static string Show(RiCommissionLedger.TreatyCommission p) => $"{p.Number} {p.Currency} {p.Accrued} {p.Settled} {p.Balance}";

    // ri-commission.test.ts L66–74.
    [Fact]
    public async Task Accrued_with_the_premium_100_percent_cessions_only_cancellations_included_at_the_reporting_dates_rate()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        var oct = await ReportAsync(x, "2026-10-31");
        Assert.Equal(["T-FRONT USD 35000 0 35000"], oct.Positions.Select(Show));
        Assert.Equal(new BigInteger(94553), oct.ReceivableGel); // 350.00 × 2.7015 = 945.525 → 945.53
        var dec = await ReportAsync(x, "2026-12-31");
        Assert.Equal([("T-FRONT", "Fictional Re", new BigInteger(30000))], dec.Positions.Select(p => (p.Number, p.Reinsurer, p.Balance)));
        Assert.Equal(new BigInteger(81045), dec.ReceivableGel);
        Assert.Equal([new RiCommissionLedger.RateUsed("USD", 2.7015m, D("2026-12-31"))], dec.Rates);
        Assert.Equal(new BigInteger(81045), await x.W.App.ReadTxAsync(tx => RiCommissionLedger.ReceivableGelAsync(tx, x.W.Rates().ValueAsync, D("2026-12-31"))));
        // Before anything is booked: no position, no rate asked, 0.
        var rates = x.W.Rates();
        var sept = await x.W.App.ReadTxAsync(tx => RiCommissionLedger.BuildReportAsync(tx, rates.ValueAsync, D("2026-09-30")));
        Assert.Empty(sept.Positions);
        Assert.Equal(BigInteger.Zero, sept.ReceivableGel);
        Assert.Empty(rates.Asked);
    }

    // ri-commission.test.ts L75–84.
    [Fact]
    public async Task The_accountant_records_an_offset_above_what_is_left_is_refused_a_viewer_may_not()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        Assert.Equal("ok", await RecordAsync(x, "2026-12-01", "100.00", reference: "RI statement Q4"));
        Assert.Single(await RowsAsync(x));
        var after = await ReportAsync(x, "2026-12-31");
        Assert.Equal((new BigInteger(10000), new BigInteger(20000), new BigInteger(54030)), (after.Positions[0].Settled, after.Positions[0].Balance, after.ReceivableGel));
        Assert.Equal("exceeds", await RecordAsync(x, "2026-12-10", "250.00"));
        Assert.Equal("not-allowed", await RecordAsync(x, "2026-12-10", "1.00", user: x.ViewerUser));
        var rows = await RowsAsync(x);
        Assert.Single(rows); // nothing written on a refusal
        Assert.Equal(("RI statement Q4", (string?)null, x.W.Accountant), (rows[0].Reference, rows[0].Note, rows[0].CreatedBy));
    }

    // ri-commission.test.ts L85–92.
    [Fact]
    public async Task Invalid_input_a_future_day_and_a_closed_month_are_refused_nothing_written()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        Assert.Equal("invalid", await RecordAsync(x, "2026-12-10", "0"));
        Assert.Equal("invalid", await RecordAsync(x, "2026-12-10", "-5.00"));
        Assert.Equal("invalid", await RecordAsync(x, "2027-01-01", "1.00"));
        Assert.Equal("invalid", await RecordAsync(x, "2026-12-10", "1.00", kind: "refund"));
        Assert.Equal("period-closed", await RecordAsync(x, "2026-11-30", "1.00"));
        // The rest of the old action's checks: an id that is not one, a treaty that does not exist, a currency or amount not in its form.
        Assert.Equal("invalid", (await RiCommissionLedger.RecordAsync(x.W.App, x.Accountant, "not-a-uuid", "2026-12-10", "offset", "USD", "1.00", null, null)).Outcome);
        Assert.Equal("invalid", await RecordAsync(x, "2026-12-10", "1.00", treaty: Guid.NewGuid()));
        Assert.Equal("invalid", await RecordAsync(x, "2026-12-10", "1.00", currency: "usd"));
        Assert.Equal("invalid", await RecordAsync(x, "2026-12-10", "1.001"));
        Assert.Equal("invalid", await RecordAsync(x, "2026-12-31T", "1.00"));
        Assert.Empty(await RowsAsync(x));
        // Where each press is answered (the old redirect): the day when it is one, the outcome.
        Assert.Equal("/accounting/ri-commission?date=2026-11-30&result=period-closed", RiCommissionLedger.BackTo(new("period-closed", "2026-11-30")));
        Assert.Equal("/accounting/ri-commission?result=invalid", RiCommissionLedger.BackTo(new("invalid", "2026-12-31T")));
        Assert.Equal("/accounting/ri-commission?result=not-allowed", RiCommissionLedger.BackTo((await RiCommissionLedger.RecordAsync(x.W.App, x.ViewerUser, Uuid.Text(x.Front), "2026-12-10", "offset", "USD", "1.00", null, null))));
    }

    // ri-commission.test.ts L93–105.
    [Fact]
    public async Task A_settlement_reversed_the_amount_back_once_only_the_original_kept_as_it_was()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        Assert.Equal("ok", await RecordAsync(x, "2026-12-01", "100.00"));
        var id = (await RowsAsync(x))[0].Id;
        Assert.Equal("state", await ReverseAsync(x, Guid.Parse("00000000-0000-4000-8000-00000000dead"), "2026-12-05"));
        Assert.Equal("invalid", await ReverseAsync(x, id, "2026-11-30"));
        var original = (await RowsAsync(x))[0];
        Assert.Equal("reversed", await ReverseAsync(x, id, "2026-12-05"));
        var rows = await RowsAsync(x);
        Assert.Equal(original, rows[0]);
        Assert.Equal(((Guid?)id, -100.00m, "offset", "USD", D("2026-12-05")), (rows[1].ReversesId, rows[1].Amount, rows[1].Kind, rows[1].Currency, rows[1].SettledOn));
        Assert.Equal(new BigInteger(30000), (await ReportAsync(x, "2026-12-31")).Positions[0].Balance);
        Assert.Equal("already", await ReverseAsync(x, id, "2026-12-06"));
        // A reversal is not itself reversed; a viewer may not reverse; a future day is no day.
        Assert.Equal("state", await ReverseAsync(x, rows[1].Id, "2026-12-07"));
        Assert.Equal("not-allowed", (await RiCommissionLedger.ReverseAsync(x.W.App, x.ViewerUser, Uuid.Text(id), "2026-12-07")).Outcome);
        Assert.Equal("invalid", await ReverseAsync(x, id, "2027-01-01"));
        Assert.Equal(2, (await RowsAsync(x)).Count);
        // The page's list marks them: the settlement reversed, the reversal a reversal.
        var listed = (await ReportAsync(x, "2026-12-31")).Settlements;
        Assert.Equal([(id, true, (Guid?)null), (rows[1].Id, false, id)], listed.Select(s => (s.Id, s.Reversed, s.ReversesId)));
    }

    // ri-commission.test.ts L106–115 (Codex PR362_RI_COMMISSION_BACKDATED_SETTLEMENT_OVERDRAW_20261020).
    [Fact]
    public async Task A_backdated_settlement_may_not_uncover_one_already_recorded_later()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        // 300.00 USD accrued. 210.00 on 10.12, then 120.00 on 05.12: 05.12 alone would allow it, 10.12 would be left −30.00 → refused.
        Assert.Equal("ok", await RecordAsync(x, "2026-12-10", "210.00"));
        var before = await RowsAsync(x);
        Assert.Equal("exceeds", await RecordAsync(x, "2026-12-05", "120.00"));
        Assert.Equal(before, await RowsAsync(x)); // nothing written, the earlier row as it was
        // What fits is still taken on an earlier day: 90.00 on 05.12 leaves 0 on 10.12.
        Assert.Equal("ok", await RecordAsync(x, "2026-12-05", "90.00"));
        Assert.Equal(BigInteger.Zero, (await ReportAsync(x, "2026-12-31")).Positions[0].Balance);
    }

    // ri-commission.test.ts L116–122.
    [Fact]
    public async Task A_later_reversal_does_not_lend_to_the_days_before_it()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        Assert.Equal("ok", await RecordAsync(x, "2026-12-10", "210.00"));
        Assert.Equal("reversed", await ReverseAsync(x, (await RowsAsync(x))[0].Id, "2026-12-20"));
        // Today 300.00 is left, but between 10.12 and 20.12 only 90.00 was: 120.00 on 05.12 is refused, 90.00 is taken.
        Assert.Equal("exceeds", await RecordAsync(x, "2026-12-05", "120.00"));
        Assert.Equal("ok", await RecordAsync(x, "2026-12-05", "90.00"));
    }

    // ri-commission.test.ts L123–138.
    [Fact]
    public async Task In_order_and_on_the_same_day_as_before_another_currency_of_the_treaty_apart()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        Assert.Equal("ok", await RecordAsync(x, "2026-12-05", "210.00"));
        Assert.Equal("exceeds", await RecordAsync(x, "2026-12-10", "120.00"));
        Assert.Equal("ok", await RecordAsync(x, "2026-12-05", "90.00"));
        Assert.Equal("exceeds", await RecordAsync(x, "2026-12-05", "0.01"));
        // A GEL policy's 100% cession on the same treaty: 35.00 GEL, its own balance, not lowered by the USD settlements.
        var gel2 = await x.W.PolicyAsync("P-GEL2", "2026-12-01", "2027-12-01", [new LineSpec("13", 1000m, 1000m)]);
        await x.W.CessionAsync(gel2.IssueRows[0], x.Front, 100m, 1000m, 1000m, "2026-12-01", "2026-12-01", "2027-12-01", commission: 35m, commissionGel: 35m);
        Assert.Equal("exceeds", await RecordAsync(x, "2026-12-10", "35.01", currency: "GEL"));
        Assert.Equal("ok", await RecordAsync(x, "2026-12-10", "35.00", currency: "GEL"));
        var dec = await ReportAsync(x, "2026-12-31");
        Assert.Equal([("GEL", BigInteger.Zero), ("USD", BigInteger.Zero)], dec.Positions.Select(p => (p.Currency, p.Balance)));
        Assert.Equal(BigInteger.Zero, dec.ReceivableGel);
    }

    // ri-commission.test.ts L139–159 (owner, 20.10.2026: several treaties ceding 100% in total count).
    [Fact]
    public async Task Several_treaties_ceding_100_percent_in_total_count_each_its_own_commission_short_of_100_percent_none()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        var split = await x.W.PolicyAsync("P-SPLIT", "2026-12-01", "2027-12-01", [new LineSpec("13", 1000m, 2701.50m)], currency: "USD", rate: 2.7015m);
        var shortRow = await x.W.PolicyAsync("P-SHORT", "2026-12-01", "2027-12-01", [new LineSpec("13", 1000m, 2701.50m)], currency: "USD", rate: 2.7015m);
        // 1 000.00 USD ceded 60% to T-FRONT (3.5%: 21.00) and 40% to T-QUOTA (2%: 8.00): 100% in total — both count.
        await x.W.CessionAsync(split.IssueRows[0], x.Front, 60m, 600m, 1620.90m, "2026-12-01", "2026-12-01", "2027-12-01", commission: 21m, commissionGel: 56.73m);
        await x.W.CessionAsync(split.IssueRows[0], x.Quota, 40m, 400m, 1080.60m, "2026-12-01", "2026-12-01", "2027-12-01", commission: 8m, commissionGel: 21.61m);
        // 60% + 30% = 90%: neither counts.
        await x.W.CessionAsync(shortRow.IssueRows[0], x.Front, 60m, 600m, 1620.90m, "2026-12-01", "2026-12-01", "2027-12-01", commission: 21m, commissionGel: 56.73m);
        await x.W.CessionAsync(shortRow.IssueRows[0], x.Quota, 30m, 300m, 810.45m, "2026-12-01", "2026-12-01", "2027-12-01", commission: 6m, commissionGel: 16.21m);
        var dec = await ReportAsync(x, "2026-12-31");
        Assert.Equal([("T-FRONT", "USD", new BigInteger(32100)), ("T-QUOTA", "USD", new BigInteger(800))], dec.Positions.Select(p => (p.Number, p.Currency, p.Accrued)));
        // (321.00 + 8.00) USD × 2.7015: 867.18 + 21.61 = 888.79 GEL (each treaty at the rate, then added).
        Assert.Equal(new BigInteger(86718 + 2161), dec.ReceivableGel);
        // What a settlement may take follows the same rows: 8.00 on T-QUOTA, not the 30% one's 6.00 or the lone 30%'s 30.00 GEL.
        Assert.Equal("exceeds", await RecordAsync(x, "2026-12-10", "8.01", treaty: x.Quota));
        Assert.Equal("ok", await RecordAsync(x, "2026-12-10", "8.00", treaty: x.Quota));
    }

    /// <summary>
    /// Over-settled (a later cancellation takes back more than is left): no receivable, nothing in 00140, listed apart — what the
    /// excess means is the owner's open question (docs/05 3ე), not decided here (legacy commissionReceivableGel; rewrite test).
    /// </summary>
    [Fact]
    public async Task A_position_settled_beyond_its_accrual_adds_nothing_and_is_listed_apart()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        Assert.Equal("ok", await RecordAsync(x, "2026-12-05", "300.00"));
        // A cancellation booked 20.12 returns 100.00 more of the commission: accrued 200.00, settled 300.00.
        var usdLine = await x.W.App.ReadAsync(c => c.ExecuteScalarAsync<Guid>("select policy_line_id from core.premium_transaction where id = @id", new { id = x.UsdIssue }));
        var cancel = await x.W.TxAsync(usdLine, "cancellation", -2857.14m, -7718.56m, "2026-12-20", "2026-12-20", "2027-10-01", cancels: x.UsdIssue);
        await x.W.CessionAsync(cancel, x.Front, 100m, -2857.14m, -7718.56m, "2026-12-20", "2026-12-20", "2027-10-01", commission: -100m, commissionGel: -270.15m);
        var dec = await ReportAsync(x, "2026-12-31");
        Assert.Equal(["T-FRONT USD 20000 30000 -10000"], dec.Positions.Select(Show));
        Assert.Equal(["T-FRONT USD 20000 30000 -10000"], dec.OverSettled.Select(Show));
        Assert.Equal(BigInteger.Zero, dec.ReceivableGel);
        Assert.Empty(dec.Rates); // nothing owed in USD: no rate asked
        // Nothing more may be settled on any day from 05.12 on.
        Assert.Equal("exceeds", await RecordAsync(x, "2026-12-31", "0.01"));
    }

    /// <summary>The treaty's lock (legacy "for update"): two settlements at once cannot both take the same balance (rewrite test).</summary>
    [Fact]
    public async Task Two_settlements_at_once_cannot_both_take_the_same_balance()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RecordAsync(x, "2026-12-10", "200.00")));
        Assert.Equal(["exceeds", "exceeds", "exceeds", "ok"], outcomes.Order(StringComparer.Ordinal));
        Assert.Single(await RowsAsync(x));
    }

    // docs/tests/2026-10-20-ri-commission-db-check.md (legacy real PostgreSQL, 19/19): the database's own rules, through the application role.

    private static async Task<DbError?> InsertAsync(World x, string values, object param) =>
        await Record(() => x.W.App.AsUserAsync(x.W.Accountant, tx => tx.ExecuteAsync(
            $"insert core.ri_commission_settlement (id, treaty_id, settled_on, kind, currency, amount, reverses_id) values {values}", param)));

    private static async Task<DbError?> Record(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception e) when (DbError.From(e) is { } error) { return error; }
    }

    [Fact]
    public async Task The_database_keeps_the_ledger_append_only_audited_in_open_months_and_every_reversal_a_mirror_once()
    {
        var x = await WorldAsync();
        await using var _ = x.W;
        Assert.Equal("ok", await RecordAsync(x, "2026-12-01", "100.00"));
        var s = (await RowsAsync(x))[0];
        // Audited with the user; created_by the session's user.
        await using (var c = await x.W.Database.OpenAdminAsync())
        {
            var audit = await c.QuerySingleAsync<(Guid? UserId, string Action, string? Amount, string? CreatedBy)>("""
                select user_id, action, json_value(new_row, '$.amount'), json_value(new_row, '$.created_by') from core.audit_log
                where table_name = N'ri_commission_settlement' and row_id = @id
                """, new { id = Uuid.Text(s.Id) });
            Assert.Equal(((Guid?)x.W.Accountant, "insert", "100.00", Uuid.Text(x.W.Accountant)), audit);
        }
        Assert.Equal(x.W.Accountant, s.CreatedBy);
        // core_app may not change or remove a row (legacy 42501).
        await using (var app = await x.W.Database.OpenAppAsync())
        {
            foreach (var statement in new[] { "update core.ri_commission_settlement set note = N'x'", "delete from core.ri_commission_settlement" })
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => app.ExecuteAsync(statement))).Number);
        }
        // A closed month (23514, no constraint); a zero amount; a negative one that reverses nothing (the constraints).
        Assert.Equal(("23514", (string?)null), Pair(await InsertAsync(x, "(@id, @t, '2026-11-30', N'offset', N'USD', 1.00, null)", new { id = Guid.NewGuid(), t = x.Front })));
        Assert.Equal(("23514", "ri_commission_settlement_amount_check"), Pair(await InsertAsync(x, "(@id, @t, '2026-12-02', N'offset', N'USD', 0, null)", new { id = Guid.NewGuid(), t = x.Front })));
        Assert.Equal(("23514", "ri_commission_settlement_check"), Pair(await InsertAsync(x, "(@id, @t, '2026-12-02', N'offset', N'USD', -1.00, null)", new { id = Guid.NewGuid(), t = x.Front })));
        // RC001: a reversal that is not the mirror — positive, another amount, an earlier day, another treaty, kind or currency, of nothing.
        foreach (var (label, values, reverses) in new (string, string, Guid)[]
        {
            ("positive", "(@id, @t, '2026-12-05', N'offset', N'USD', 100.00, @r)", s.Id),
            ("another amount", "(@id, @t, '2026-12-05', N'offset', N'USD', -50.00, @r)", s.Id),
            ("an earlier day", "(@id, @t, '2026-11-30', N'offset', N'USD', -100.00, @r)", s.Id),
            ("another kind", "(@id, @t, '2026-12-05', N'receipt', N'USD', -100.00, @r)", s.Id),
            ("another currency", "(@id, @t, '2026-12-05', N'offset', N'GEL', -100.00, @r)", s.Id),
            ("of nothing", "(@id, @t, '2026-12-05', N'offset', N'USD', -100.00, @r)", Guid.NewGuid()),
        })
        {
            var e = await InsertAsync(x, values, new { id = Guid.NewGuid(), t = x.Front, r = reverses });
            // The earlier day is in the closed November: the open month is checked first, as the old trigger did.
            Assert.True(e?.Code == (label == "an earlier day" ? "23514" : "RC001"), $"{label}: {e}");
        }
        Assert.Equal("RC001", (await InsertAsync(x, "(@id, @t, '2026-12-05', N'offset', N'USD', -100.00, @r)", new { id = Guid.NewGuid(), t = x.Quota, r = s.Id }))?.Code);
        // The exact mirror, on the settlement's own day, is taken; a reversal of that reversal is not (RC001).
        var mirror = Guid.NewGuid();
        Assert.Null(await InsertAsync(x, "(@id, @t, '2026-12-01', N'offset', N'USD', -100.00, @r)", new { id = mirror, t = x.Front, r = s.Id }));
        Assert.Equal("RC001", (await InsertAsync(x, "(@id, @t, '2026-12-02', N'offset', N'USD', 100.00, @r)", new { id = Guid.NewGuid(), t = x.Front, r = mirror }))?.Code);
        // That mirror is the one reversal; a second one is refused by the unique index (23505).
        Assert.Equal(DbError.UniqueViolation, (await InsertAsync(x, "(@id, @t, '2026-12-03', N'offset', N'USD', -100.00, @r)", new { id = Guid.NewGuid(), t = x.Front, r = s.Id }))?.Code);
        // The balance is back after the reversal.
        Assert.Equal(new BigInteger(30000), (await ReportAsync(x, "2026-12-31")).Positions[0].Balance);
        // The database itself does not stop a settlement above the balance (legacy too: 10.00 on 0.00 was written) — the action does.
        Assert.Null(await InsertAsync(x, "(@id, @t, '2026-12-02', N'receipt', N'GEL', 10.00, null)", new { id = Guid.NewGuid(), t = x.Quota }));
        // What a settlement may take before anything is accrued: 0; from 01.12 the least balance after every event: 300.00.
        Assert.Equal(BigInteger.Zero, await x.W.App.ReadTxAsync(tx => RiCommissionLedger.SettleableFromAsync(tx, x.Front, "USD", D("2026-09-30"))));
        Assert.Equal(new BigInteger(30000), await x.W.App.ReadTxAsync(tx => RiCommissionLedger.SettleableFromAsync(tx, x.Front, "USD", D("2026-12-01"))));
        // An insert that names no id, time or user still gets them (the trigger's defaults); the user from the session.
        await x.W.App.AsUserAsync(x.W.Accountant, tx => tx.ExecuteAsync(
            "insert core.ri_commission_settlement (treaty_id, settled_on, kind, currency, amount) values (@t, '2026-12-02', N'receipt', N'USD', 1.00)", new { t = x.Front }));
        Assert.Contains(await RowsAsync(x), r => r.Amount == 1.00m && r.CreatedBy == x.W.Accountant && r.Id != Guid.Empty);
    }

    private static (string?, string?) Pair(DbError? e) => (e?.Code, e?.Constraint);
}
