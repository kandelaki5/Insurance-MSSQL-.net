using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using Insurance.Integration.Tests.Policies;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.PolicyPremiumWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The cancellation writer (PolicyCancel.CancelPolicyInTxAsync) in one transaction of the user, on the reinsured
/// combined USD policy C (legacy policy-cancel-asuser.test.ts): cancelled 25.10.2026 in the open October; October closed;
/// brought forward to 20.10. The bring-forward failing at its second cession insert leaves nothing and the retry books
/// the accepted rows; two replays write nothing; the writer's reinsurance rows in the monthly report; a non-zero
/// reinsurance commission through the cancellation. The writer takes the day as a parameter, so the legacy days are kept.
/// The fault is a test-only trigger on this test's own database (Gaps.ArmAsync).
/// </summary>
public sealed class GapsPolicyCancelAsUserTests
{
    private sealed record World(ReportWorld W, Ids Ids, Guid C, Guid Q30, Guid Q50, Guid CC1, Guid CC2);

    /// <summary>The legacy reinsuredWorld: the History rows, C's instalment 438.00 USD, the closed 30 % treaty's cessions on C's originals, an active 50 % treaty.</summary>
    private static async Task<World> ReinsuredAsync(ReportWorld w, decimal commissionPercent = 0m)
    {
        var ids = await WriteAsync(w, Variant.History);
        var c = ids.Policies["pC"];
        await w.ExecAsync("insert core.installment (policy_id, due_on, amount) values (@c, '2026-10-15', 438.00)", new { c });
        var q30 = await w.TreatyAsync("RI-T-30", "quota_share", "2026-01-01", "2026-12-31", ["5.1", Liability], "USD", cededPercent: 30m, commissionPercent: commissionPercent);
        var commission = commissionPercent > 0;
        var cC1 = await w.CessionAsync(ids.Rows["tC1"], q30, 30m, 87.60m, 219.00m, "2026-10-15", "2026-10-15", "2027-10-15", commission ? 17.52m : 0m, commission ? 43.80m : 0m);
        var cC2 = await w.CessionAsync(ids.Rows["tC2"], q30, 30m, 43.80m, 109.50m, "2026-10-15", "2026-10-15", "2027-10-15", commission ? 8.76m : 0m, commission ? 21.90m : 0m);
        await w.ExecAsync("update core.ri_treaty set status = N'closed' where id = @q30", new { q30 });
        var q50 = await w.TreatyAsync("RI-T-50", "quota_share", "2026-01-01", "2026-12-31", ["5.1", Liability], "USD", cededPercent: 50m);
        await w.RateAsync("USD", "2026-10-31", 2.5m);
        await w.RateAsync("USD", "2026-11-30", 2.5m);
        return new World(w, ids, c, q30, q50, cC1, cC2);
    }

    private static Task<(PolicyCancel.CancelOutcome Outcome, long Retained)> CancelAsync(World x, string on, bool bringForward = false) =>
        x.W.WriteAsync(tx => PolicyCancel.CancelPolicyInTxAsync(tx, x.C, D(on), "სრული განადგურება (ტესტი)", x.W.Admin, bringForward));

    /// <summary>Every financial row of the database, in its writing order, as text: what a failure or a replay must leave exactly as it was.</summary>
    private static async Task<string> FinancialAsync(World x)
    {
        await using var c = await x.W.Database.OpenAdminAsync();
        var ledger = await c.QueryAsync<string>("select concat(id, '|', policy_line_id, '|', kind, '|', amount, '|', amount_gel, '|', booked_on, '|', cover_from, '|', cancels_transaction_id) from core.premium_transaction order by created_at, id");
        var cessions = await c.QueryAsync<string>("select concat(id, '|', premium_transaction_id, '|', treaty_id, '|', amount, '|', amount_gel, '|', commission, '|', commission_gel, '|', booked_on, '|', cancels_cession_id) from core.premium_cession order by created_at, id");
        var schedule = await c.QueryAsync<string>("select concat(installment_id, '|', booked_on, '|', change) from core.installment_adjustment order by created_at, id");
        var policy = await c.QuerySingleAsync<string>("select concat(status, '|', cancelled_on, '|', cancel_reason) from core.policy where id = @id", new { id = x.C });
        return string.Join("\n", [.. ledger, "--", .. cessions, "--", .. schedule, "--", policy]);
    }

    private static async Task<T> ScalarAsync<T>(ReportWorld w, string sql, object? param = null)
    {
        await using var c = await w.Database.OpenAdminAsync();
        return await c.QuerySingleAsync<T>(sql, param);
    }

    private static Task<int> CountAsync(World x, string table) => ScalarAsync<int>(x.W, $"select count(*) from core.{table}");

    private static async Task<List<string>> ScheduleAtAsync(World x, string asOf) =>
        (await x.W.App.ReadTxAsync(tx => Billing.LoadScheduleAsync(tx, x.C, D(asOf)))).Select(i => i.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).ToList();

    private static async Task<List<(Guid Line, decimal Amount, decimal AmountGel, decimal Commission, decimal CommissionGel, DateOnly Booked, DateOnly From, Guid Treaty, Guid? Cancels)>> AddedCessionsAsync(World x)
    {
        await using var c = await x.W.Database.OpenAdminAsync();
        return (await c.QueryAsync<(Guid, decimal, decimal, decimal, decimal, DateOnly, DateOnly, Guid, Guid?)>("""
            select t.policy_line_id, c.amount, c.amount_gel, c.commission, c.commission_gel, c.booked_on, c.cover_from, c.treaty_id, c.cancels_cession_id
            from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
            where c.cancels_cession_id is not null order by c.booked_on, c.cover_from desc, c.amount
            """)).ToList();
    }

    [Fact]
    public async Task The_bring_forward_throws_at_its_second_cession_insert_nothing_stays_the_reports_as_before_the_retry_gives_the_accepted_rows()
    {
        await using var w = await CreateAsync();
        var x = await ReinsuredAsync(w);
        Assert.Equal((PolicyCancel.CancelOutcome.Done, 1200L), await CancelAsync(x, "2026-10-25"));
        await w.ClosePeriodAsync("2026-10");
        var before = await FinancialAsync(x);
        var reports = (await w.PolicyPremiumAsync("2026-10-01", "2026-10-31"), await w.PolicyPremiumAsync("2026-11-01", "2026-11-30"));
        var scheduleBefore = await ScheduleAtAsync(x, "2026-10-31");
        Assert.Equal((15, 4), (await CountAsync(x, "premium_transaction"), await CountAsync(x, "premium_cession")));

        await Gaps.ArmAsync(w.Database, "premium_cession", 2);
        try
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => CancelAsync(x, "2026-10-20", bringForward: true));
            Assert.Contains("injected failure", e.Message);
        }
        finally
        {
            await Gaps.DisarmAsync(w.Database);
        }
        // First financial check: every committed row and C's metadata exactly as before the failed call.
        Assert.Equal(before, await FinancialAsync(x));
        Assert.Equal((15, 4), (await CountAsync(x, "premium_transaction"), await CountAsync(x, "premium_cession")));
        var after = (await w.PolicyPremiumAsync("2026-10-01", "2026-10-31"), await w.PolicyPremiumAsync("2026-11-01", "2026-11-30"));
        Assert.Equal(reports.Item1.Rows, after.Item1.Rows);
        Assert.Equal(reports.Item2.Rows, after.Item2.Rows);
        Assert.Equal((reports.Item1.Totals, reports.Item2.Totals), (after.Item1.Totals, after.Item2.Totals));
        Assert.Equal(scheduleBefore, await ScheduleAtAsync(x, "2026-10-31"));
        Assert.Equal(D("2026-10-25"), await ScalarAsync<DateOnly>(w, "select cancelled_on from core.policy where id = @id", new { id = x.C }));

        // The same call again, without the fault: the accepted rows.
        Assert.Equal((PolicyCancel.CancelOutcome.Done, 600L), await CancelAsync(x, "2026-10-20", bringForward: true));
        Assert.Equal(("cancelled", D("2026-10-20")), await ScalarAsync<(string, DateOnly)>(w, "select status, cancelled_on from core.policy where id = @id", new { id = x.C }));
        Assert.Equal(["12.00"], await ScheduleAtAsync(x, "2026-10-31"));
        Assert.Equal(["6.00"], await ScheduleAtAsync(x, "2026-11-30"));
        Assert.Equal((19, 8), (await CountAsync(x, "premium_transaction"), await CountAsync(x, "premium_cession")));
        var lC1 = x.Ids.Lines["lC1"];
        var lC2 = x.Ids.Lines["lC2"];
        string Row(Guid line, decimal a, decimal g, string booked, string from) => $"{(line == lC1 ? "lC1" : "lC2")}|{a:0.00}|{g:0.00}|{booked}|{from}";
        Assert.Equal(new[]
        {
            Row(lC1, -85.20m, -213.00m, "2026-10-25", "2026-10-25"), Row(lC2, -42.60m, -106.50m, "2026-10-25", "2026-10-25"),
            Row(lC1, 85.20m, 213.00m, "2026-11-01", "2026-10-25"), Row(lC2, 42.60m, 106.50m, "2026-11-01", "2026-10-25"),
            Row(lC1, -86.40m, -216.00m, "2026-11-01", "2026-10-20"), Row(lC2, -43.20m, -108.00m, "2026-11-01", "2026-10-20"),
        }.Order(StringComparer.Ordinal), (await AddedCessionsAsync(x)).Select(c => Row(c.Line, c.Amount, c.AmountGel, $"{c.Booked:yyyy-MM-dd}", $"{c.From:yyyy-MM-dd}")).Order(StringComparer.Ordinal));
        Assert.All(await AddedCessionsAsync(x), c => Assert.Equal(x.Q30, c.Treaty));
        var november = await w.PolicyPremiumAsync("2026-11-01", "2026-11-30");
        Assert.Equal([
            "T-0003|5.1|USD|29200|0|1000|-1000|2026-10-15|2027-10-15|2026-10-20|-1000|0",
            $"T-0003|{Liability}|USD|14600|0|500|-500|2026-10-15|2027-10-15|2026-10-20|-500|0",
        ], november.Rows.Where(r => r.PolicyNumber == "T-0003").Select(Flat));
        var all = await AddedCessionsAsync(x);
        // In all, the originals and their mirrors: 4.50 GEL / 1.80 USD ceded (the legacy 450 / 180 tetri).
        Assert.Equal((4.50m, 1.80m), (328.50m + all.Sum(c => c.AmountGel), 131.40m + all.Sum(c => c.Amount)));
        var october = await w.PolicyPremiumAsync("2026-10-01", "2026-10-31");
        Assert.Equal(reports.Item1.Rows, october.Rows);
    }

    [Fact]
    public async Task The_accepted_bring_forward_replayed_twice_is_not_active_retained_0_and_writes_nothing()
    {
        await using var w = await CreateAsync();
        var x = await ReinsuredAsync(w);
        await CancelAsync(x, "2026-10-25");
        await w.ClosePeriodAsync("2026-10");
        Assert.Equal((PolicyCancel.CancelOutcome.Done, 600L), await CancelAsync(x, "2026-10-20", bringForward: true));
        var settled = await FinancialAsync(x);
        var views = (await w.PolicyPremiumAsync("2026-10-01", "2026-10-31"), await w.PolicyPremiumAsync("2026-11-01", "2026-11-30"));
        foreach (var _ in new[] { 1, 2 })
        {
            Assert.Equal((PolicyCancel.CancelOutcome.NotActive, 0L), await CancelAsync(x, "2026-10-20", bringForward: true));
            Assert.Equal(settled, await FinancialAsync(x));
        }
        var after = (await w.PolicyPremiumAsync("2026-10-01", "2026-10-31"), await w.PolicyPremiumAsync("2026-11-01", "2026-11-30"));
        Assert.Equal(views.Item1.Rows, after.Item1.Rows);
        Assert.Equal(views.Item1.Totals, after.Item1.Totals);
        Assert.Equal(views.Item2.Rows, after.Item2.Rows);
        Assert.Equal(views.Item2.Totals, after.Item2.Totals);
        Assert.Equal(["6.00"], await ScheduleAtAsync(x, "2026-11-30"));
        Assert.Equal((19, 8), (await CountAsync(x, "premium_transaction"), await CountAsync(x, "premium_cession")));
    }

    private static (string Code, BigInteger Ceded, BigInteger CededUnearned, BigInteger Commission)[] Ri(PremiumReport.Report r) =>
        [.. r.Classes.Select(c => (c.ClassCode, c.CededWrittenGel, c.CededUnearnedGel, c.RiCommissionGel))];

    [Fact]
    public async Task The_writers_reinsurance_rows_in_the_monthly_report_October_6_00_and_3_00_November_minus_3_00_and_minus_1_50_October_unchanged_by_the_bring_forward()
    {
        await using var w = await CreateAsync();
        var x = await ReinsuredAsync(w);
        await CancelAsync(x, "2026-10-25");
        var octoberAt25 = await w.MonthAsync("2026-10");
        await w.ClosePeriodAsync("2026-10");
        Assert.Equal((PolicyCancel.CancelOutcome.Done, 600L), await CancelAsync(x, "2026-10-20", bringForward: true));
        var october = await w.MonthAsync("2026-10");
        var november = await w.MonthAsync("2026-11");
        // By hand (tetri): October CASCO 219.00 − 213.00 = 6.00, liability 109.50 − 106.50 = 3.00; November +213.00 − 216.00 =
        // −3.00 and +106.50 − 108.00 = −1.50; the reinsurer's UPR 0 (both covers ended 25.10 / 20.10), commission 0.
        Assert.Equal([("5.1", new BigInteger(600), BigInteger.Zero, BigInteger.Zero), (Liability, new BigInteger(300), BigInteger.Zero, BigInteger.Zero)], Ri(october).Where(c => c.Ceded != 0 || c.Code is "5.1"));
        Assert.Equal((new BigInteger(900), BigInteger.Zero, BigInteger.Zero), (october.Totals.CededWrittenGel, october.Totals.CededUnearnedGel, october.Totals.RiCommissionGel));
        Assert.Equal([("5.1", new BigInteger(-300), BigInteger.Zero, BigInteger.Zero), (Liability, new BigInteger(-150), BigInteger.Zero, BigInteger.Zero)], Ri(november).Where(c => c.Ceded != 0 || c.Code is "5.1"));
        Assert.Equal((new BigInteger(-450), BigInteger.Zero, BigInteger.Zero), (november.Totals.CededWrittenGel, november.Totals.CededUnearnedGel, november.Totals.RiCommissionGel));
        // October's whole report the same before and after the bring-forward.
        Assert.Equal(octoberAt25.Classes, october.Classes);
        Assert.Equal(octoberAt25.Totals, october.Totals);
        Assert.Equal(octoberAt25.Receivables, october.Receivables);
    }

    [Fact]
    public async Task A_non_zero_reinsurance_commission_through_the_cancellation_each_mirrors_commission_in_USD_and_GEL_October_1_20_0_60_1_80_November_minus_0_60_minus_0_30_minus_0_90()
    {
        await using var w = await CreateAsync();
        var x = await ReinsuredAsync(w, commissionPercent: 20m);
        Assert.Equal((PolicyCancel.CancelOutcome.Done, 1200L), await CancelAsync(x, "2026-10-25"));
        var octoberAtEnd = await w.MonthAsync("2026-10");
        await w.ClosePeriodAsync("2026-10");
        Assert.Equal((PolicyCancel.CancelOutcome.Done, 600L), await CancelAsync(x, "2026-10-20", bringForward: true));
        var added = await AddedCessionsAsync(x);
        var lC1 = x.Ids.Lines["lC1"];
        // By hand: 25.10 on the originals −85.20 / −213.00, commission −17.04 / −42.60; −42.60 / −106.50, −8.52 / −21.30. 01.11
        // undoing those: +85.20 / +213.00, +17.04 / +42.60; +42.60 / +106.50, +8.52 / +21.30. 01.11 from 20.10 on the originals:
        // −86.40 / −216.00, −17.28 / −43.20; −43.20 / −108.00, −8.64 / −21.60.
        string Row(decimal a, decimal g, decimal c, decimal cg, string booked, string from) => $"{a:0.00}|{g:0.00}|{c:0.00}|{cg:0.00}|{booked}|{from}";
        Assert.Equal(new[]
        {
            Row(-85.20m, -213.00m, -17.04m, -42.60m, "2026-10-25", "2026-10-25"), Row(-42.60m, -106.50m, -8.52m, -21.30m, "2026-10-25", "2026-10-25"),
            Row(85.20m, 213.00m, 17.04m, 42.60m, "2026-11-01", "2026-10-25"), Row(42.60m, 106.50m, 8.52m, 21.30m, "2026-11-01", "2026-10-25"),
            Row(-86.40m, -216.00m, -17.28m, -43.20m, "2026-11-01", "2026-10-20"), Row(-43.20m, -108.00m, -8.64m, -21.60m, "2026-11-01", "2026-10-20"),
        }.Order(StringComparer.Ordinal), added.Select(c => Row(c.Amount, c.AmountGel, c.Commission, c.CommissionGel, $"{c.Booked:yyyy-MM-dd}", $"{c.From:yyyy-MM-dd}")).Order(StringComparer.Ordinal));
        Assert.Equal(8, await CountAsync(x, "premium_cession"));
        Assert.All(added, c => Assert.Equal(x.Q30, c.Treaty));
        // The 25.10 and 20.10 mirrors are on C's original cessions; the undoing corrections on the 25.10 mirrors.
        Assert.Equal(2, added.Count(c => c.Cancels == x.CC1));
        Assert.Equal(2, added.Count(c => c.Cancels == x.CC2));
        Assert.Equal(["12.00"], await ScheduleAtAsync(x, "2026-10-31"));
        Assert.Equal(["6.00"], await ScheduleAtAsync(x, "2026-11-30"));
        var october = await w.MonthAsync("2026-10");
        var november = await w.MonthAsync("2026-11");
        // October commission 43.80 − 42.60 = 1.20, 21.90 − 21.30 = 0.60 (1.80); November +42.60 − 43.20 = −0.60, +21.30 − 21.60 = −0.30 (−0.90).
        Assert.Equal([("5.1", new BigInteger(600), BigInteger.Zero, new BigInteger(120)), (Liability, new BigInteger(300), BigInteger.Zero, new BigInteger(60))], Ri(october).Where(c => c.Ceded != 0 || c.Code is "5.1"));
        Assert.Equal((new BigInteger(900), new BigInteger(180)), (october.Totals.CededWrittenGel, october.Totals.RiCommissionGel));
        Assert.Equal([("5.1", new BigInteger(-300), BigInteger.Zero, new BigInteger(-60)), (Liability, new BigInteger(-150), BigInteger.Zero, new BigInteger(-30))], Ri(november).Where(c => c.Ceded != 0 || c.Code is "5.1"));
        Assert.Equal((new BigInteger(-450), new BigInteger(-90)), (november.Totals.CededWrittenGel, november.Totals.RiCommissionGel));
        Assert.Equal(octoberAtEnd.Classes, october.Classes);
        Assert.Equal(octoberAtEnd.Totals, october.Totals);
    }
}
