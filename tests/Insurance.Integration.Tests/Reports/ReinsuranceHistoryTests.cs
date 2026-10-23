using System.Numerics;
using Dapper;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The reinsurer's share of paid claims and of RBNS in a month's report stays what it was when later months bring new payments, reserve
/// movements, a closed treaty or a new one (legacy reinsurance-history.test.ts, AG-R8; docs/04 rules 16a and 26: recognised when paid; a
/// closed month's history is not rewritten). The old test's fake database answered the SQL it was sent and checked the date bounds in
/// its text; here the rows are in SQL Server and the bounds are shown by movements on the boundary days themselves. GEL, no schedule
/// (so no receivable). Worked by hand in the old test, in tetri: (paid, RBNS, IBNR, recovered, reinsurer paid, reinsurer RBNS).
/// </summary>
public sealed class ReinsuranceHistoryTests
{
    private static (BigInteger, BigInteger, BigInteger, BigInteger, BigInteger, BigInteger) Row(long paid, long rbns, long riPaid, long riRbns, long ibnr = 0) =>
        (paid, rbns, ibnr, 0, riPaid, riRbns);

    private sealed record World(ReportWorld W, PolicyRef Policy, Guid Line, Guid Quota, Guid Xl);

    private static async Task<World> BaseAsync()
    {
        var w = await CreateAsync();
        var p = await w.PolicyAsync("P-RH-3", "2026-03-01", "2027-03-01", [new LineSpec("3", 0m, 0m)], issueRows: false);
        var quota = await w.TreatyAsync("RI001", "quota_share", "2026-01-01", "2026-12-31", ["3"], cededPercent: 30m);
        var xl = await w.TreatyAsync("RI002", "excess_of_loss", "2026-01-01", "2026-12-31", ["3"], priority: 100m, limit: 500m);
        return new World(w, p, p.Lines[0], quota, xl);
    }

    private static async Task<(BigInteger, BigInteger, BigInteger, BigInteger, BigInteger, BigInteger)> ClaimsAsync(ReportWorld w, string month)
    {
        var r = await w.MonthAsync(month);
        var row = Assert.Single(r.Claims);
        Assert.Equal("3", row.ClassCode);
        var t = r.ClaimsTotals;
        Assert.Equal((row.PaidGel, row.RbnsGel, row.IbnrGel, row.RecoveredGel, row.ReinsurerPaidGel, row.ReinsurerRbnsGel), (t.PaidGel, t.RbnsGel, t.IbnrGel, t.RecoveredGel, t.ReinsurerPaidGel, t.ReinsurerRbnsGel));
        return (t.PaidGel, t.RbnsGel, t.IbnrGel, t.RecoveredGel, t.ReinsurerPaidGel, t.ReinsurerRbnsGel);
    }

    private static async Task PayAsync(ReportWorld w, Guid claim, string on, decimal amount)
    {
        await w.ClaimPaymentAsync(claim, on, amount);
        await w.ReserveAsync(claim, on, -amount);
    }

    /// <summary>One proportional 30 %: reserve 1000 on 28.06; July pays 200 (reserve → 800).</summary>
    private static async Task<(World World, Guid Claim)> QuotaAsync()
    {
        var b = await BaseAsync();
        var c = await b.W.ClaimAsync(b.Policy.Id, b.Line, "GEL", "2026-06-20", "2026-06-25");
        await b.W.ClaimCessionAsync(c.Id, b.Quota, 30m);
        await b.W.ReserveAsync(c.Id, "2026-06-28", 1000m);
        await PayAsync(b.W, c.Id, "2026-07-20", 200m);
        return (b, c.Id);
    }

    /// <summary>One GEL excess of loss, priority 100, limit 500: reserve 500 on 28.06; July pays 80 (reserve → 420).</summary>
    private static async Task<(World World, Guid Claim)> XlAsync()
    {
        var b = await BaseAsync();
        var c = await b.W.ClaimAsync(b.Policy.Id, b.Line, "GEL", "2026-06-20", "2026-06-25");
        await b.W.ClaimCessionAsync(c.Id, b.Xl, 0m, 100m, 500m);
        await b.W.ReserveAsync(c.Id, "2026-06-28", 500m);
        await PayAsync(b.W, c.Id, "2026-07-15", 80m);
        return (b, c.Id);
    }

    /// <summary>The later actions the program has: the treaty closed (its status only) and a new one added.</summary>
    private static async Task CloseAndAddAsync(ReportWorld w, Guid treaty)
    {
        await using var c = await w.Database.OpenAdminAsync();
        await c.ExecuteAsync("update core.ri_treaty set status = N'closed' where id = @treaty", new { treaty });
        await w.TreatyAsync("RI009", "quota_share", "2026-09-01", "2027-08-31", ["3"], cededPercent: 50m);
    }

    [Fact]
    public async Task Quota_30_percent_each_month_as_it_was_after_later_payments_a_closed_treaty_and_a_new_one()
    {
        var (b, claim) = await QuotaAsync();
        await using var owner = b.W;
        Assert.Equal(Row(0, 100000, 0, 30000), await ClaimsAsync(b.W, "2026-06"));
        Assert.Equal(Row(20000, 80000, 6000, 24000), await ClaimsAsync(b.W, "2026-07"));
        await PayAsync(b.W, claim, "2026-08-12", 300m);
        Assert.Equal(Row(30000, 50000, 9000, 15000), await ClaimsAsync(b.W, "2026-08"));
        Assert.Equal(Row(20000, 80000, 6000, 24000), await ClaimsAsync(b.W, "2026-07"));
        Assert.Equal(Row(0, 100000, 0, 30000), await ClaimsAsync(b.W, "2026-06"));
        await CloseAndAddAsync(b.W, b.Quota);
        Assert.Equal(Row(20000, 80000, 6000, 24000), await ClaimsAsync(b.W, "2026-07"));
        Assert.Equal(Row(30000, 50000, 9000, 15000), await ClaimsAsync(b.W, "2026-08"));
        // A payment dated after the month does not reach it.
        await b.W.ClaimPaymentAsync(claim, "2026-09-01", 100m);
        Assert.Equal(Row(30000, 50000, 9000, 15000), await ClaimsAsync(b.W, "2026-08"));
    }

    [Fact]
    public async Task Excess_of_loss_100_xs_500_on_the_terms_fixed_on_the_claim_not_the_treaty_row()
    {
        var (b, claim) = await XlAsync();
        await using var owner = b.W;
        Assert.Equal(Row(0, 50000, 0, 40000), await ClaimsAsync(b.W, "2026-06"));
        Assert.Equal(Row(8000, 42000, 0, 40000), await ClaimsAsync(b.W, "2026-07"));
        await PayAsync(b.W, claim, "2026-08-20", 120m);
        Assert.Equal(Row(12000, 30000, 10000, 30000), await ClaimsAsync(b.W, "2026-08"));
        Assert.Equal(Row(8000, 42000, 0, 40000), await ClaimsAsync(b.W, "2026-07"));
        await CloseAndAddAsync(b.W, b.Xl);
        Assert.Equal(Row(8000, 42000, 0, 40000), await ClaimsAsync(b.W, "2026-07"));
        Assert.Equal(Row(12000, 30000, 10000, 30000), await ClaimsAsync(b.W, "2026-08"));
        // The treaty row now holds other terms: the claim's own (100 / 500) still decide.
        await using (var c = await b.W.Database.OpenAdminAsync())
            await c.ExecuteAsync("update core.ri_treaty set priority = 50, treaty_limit = 50 where id = @id", new { id = b.Xl });
        Assert.Equal(Row(8000, 42000, 0, 40000), await ClaimsAsync(b.W, "2026-07"));
        Assert.Equal(Row(12000, 30000, 10000, 30000), await ClaimsAsync(b.W, "2026-08"));
    }

    [Fact]
    public async Task The_first_and_the_last_day_belong_to_their_month_and_only_to_it()
    {
        // quota 30 %: June paid 100, reserve 900 → 30 / 270; July 200 + 300, reserve 400 → 150 / 120; August 50, reserve 350 → 15 / 105.
        var b = await BaseAsync();
        await using var owner = b.W;
        var c = await b.W.ClaimAsync(b.Policy.Id, b.Line, "GEL", "2026-06-20", "2026-06-25");
        await b.W.ClaimCessionAsync(c.Id, b.Quota, 30m);
        await b.W.ReserveAsync(c.Id, "2026-06-30", 1000m);
        foreach (var (on, amount) in new[] { ("2026-06-30", 100m), ("2026-07-01", 200m), ("2026-07-31", 300m), ("2026-08-01", 50m) }) await PayAsync(b.W, c.Id, on, amount);
        Assert.Equal(Row(10000, 90000, 3000, 27000), await ClaimsAsync(b.W, "2026-06"));
        var july = await ClaimsAsync(b.W, "2026-07");
        Assert.Equal(Row(50000, 40000, 15000, 12000), july);
        Assert.Equal(Row(5000, 35000, 1500, 10500), await ClaimsAsync(b.W, "2026-08"));
        await PayAsync(b.W, c.Id, "2026-08-31", 10m);
        Assert.Equal(july, await ClaimsAsync(b.W, "2026-07"));

        // XL 100 / 500, loss 600: June 0 / 500, July 100 / 400, August 20 / 380.
        var x = await BaseAsync();
        await using var xOwner = x.W;
        var cx = await x.W.ClaimAsync(x.Policy.Id, x.Line, "GEL", "2026-06-20", "2026-06-25");
        await x.W.ClaimCessionAsync(cx.Id, x.Xl, 0m, 100m, 500m);
        await x.W.ReserveAsync(cx.Id, "2026-06-30", 600m);
        foreach (var (on, amount) in new[] { ("2026-06-30", 50m), ("2026-07-01", 70m), ("2026-07-31", 80m), ("2026-08-01", 20m) }) await PayAsync(x.W, cx.Id, on, amount);
        Assert.Equal(Row(5000, 55000, 0, 50000), await ClaimsAsync(x.W, "2026-06"));
        Assert.Equal(Row(15000, 40000, 10000, 40000), await ClaimsAsync(x.W, "2026-07"));
        Assert.Equal(Row(2000, 38000, 2000, 38000), await ClaimsAsync(x.W, "2026-08"));
    }

    /// <summary>Premium on the class-3 line: gross and ceded this year, and rows outside July's year to date (last year; after July).</summary>
    private static async Task WithPremiumAsync(World b, decimal gross, decimal ceded)
    {
        var w = b.W;
        var pt1 = await w.TxAsync(b.Line, "issue", gross, gross, "2026-03-01", "2026-03-01", "2027-02-28");
        await w.CessionAsync(pt1, b.Quota, 30m, ceded, ceded, "2026-03-01", "2026-03-01", "2027-02-28");
        var pt0 = await w.TxAsync(b.Line, "issue", 5000m, 5000m, "2025-12-20", "2025-12-20", "2026-12-19");
        await w.CessionAsync(pt0, b.Quota, 20m, 1000m, 1000m, "2025-12-20", "2025-12-20", "2026-12-19");
        await w.TxAsync(b.Line, "issue", 9000m, 9000m, "2026-08-05", "2026-08-05", "2027-08-04");
    }

    [Theory]
    [InlineData(1000, 300, 3500)]
    [InlineData(2000, 600, 7000)]
    public async Task IBNR_on_the_years_net_written_premium_stays_out_of_the_reinsurers_shares(int gross, int ceded, long ibnr)
    {
        var (b, _) = await QuotaAsync();
        await using var owner = b.W;
        await WithPremiumAsync(b, gross, ceded);
        Assert.Equal(Row(20000, 80000, 6000, 24000, ibnr), await ClaimsAsync(b.W, "2026-07"));
        // Premium and no reported claim: IBNR alone, the reinsurer's shares 0.
        var empty = await BaseAsync();
        await using var emptyOwner = empty.W;
        await WithPremiumAsync(empty, gross, ceded);
        Assert.Equal(Row(0, 0, 0, 0, ibnr), await ClaimsAsync(empty.W, "2026-07"));
    }

    [Fact]
    public async Task Two_classes_each_row_its_own_claim_the_totals_add_them_and_a_later_month_of_one_moves_only_it()
    {
        var (b, _) = await QuotaAsync();
        await using var owner = b.W;
        var w = b.W;
        var pt3 = await w.TxAsync(b.Line, "issue", 1000m, 1000m, "2026-03-01", "2026-03-01", "2027-02-28");
        await w.CessionAsync(pt3, b.Quota, 30m, 300m, 300m, "2026-03-01", "2026-03-01", "2027-02-28");
        var p2 = await w.PolicyAsync("P-RH-51", "2026-04-01", "2027-02-28", [new LineSpec("5.1", 0m, 0m)], issueRows: false);
        var t5 = await w.TreatyAsync("RI005", "quota_share", "2026-01-01", "2026-12-31", ["5.1"], cededPercent: 20m);
        var c5 = await w.ClaimAsync(p2.Id, p2.Lines[0], "GEL", "2026-06-20", "2026-06-24");
        await w.ClaimCessionAsync(c5.Id, t5, 20m);
        await w.ReserveAsync(c5.Id, "2026-06-25", 1000m);
        await PayAsync(w, c5.Id, "2026-07-18", 300m);
        var pt5 = await w.TxAsync(p2.Lines[0], "issue", 2000m, 2000m, "2026-04-01", "2026-04-01", "2027-02-28");
        await w.CessionAsync(pt5, t5, 20m, 400m, 400m, "2026-04-01", "2026-04-01", "2027-02-28");
        static string Flat(Insurance.Application.Services.PremiumReport.ClaimsRow r) => $"{r.ClassCode}|{r.PaidGel}|{r.RbnsGel}|{r.IbnrGel}|{r.RecoveredGel}|{r.ReinsurerPaidGel}|{r.ReinsurerRbnsGel}";
        static string Totals(Insurance.Application.Services.PremiumReport.ClaimsTotals t) => $"{t.PaidGel}|{t.RbnsGel}|{t.IbnrGel}|{t.RecoveredGel}|{t.ReinsurerPaidGel}|{t.ReinsurerRbnsGel}";
        string[] july = ["3|20000|80000|3500|0|6000|24000", "5.1|30000|70000|8000|0|6000|14000"];
        var r = await w.MonthAsync("2026-07");
        Assert.Equal(july, r.Claims.Select(Flat));
        Assert.Equal("50000|150000|11500|0|12000|38000", Totals(r.ClaimsTotals));
        // August, class 5.1 alone: 100 paid, reserve 600, new premium 500 / ceded 100 on 05.08.
        await PayAsync(w, c5.Id, "2026-08-10", 100m);
        var pt6 = await w.TxAsync(p2.Lines[0], "issue", 500m, 500m, "2026-08-05", "2026-08-05", "2027-08-04");
        await w.CessionAsync(pt6, t5, 20m, 100m, 100m, "2026-08-05", "2026-08-05", "2027-08-04");
        var again = await w.MonthAsync("2026-07");
        Assert.Equal(july, again.Claims.Select(Flat));
        Assert.Equal("50000|150000|11500|0|12000|38000", Totals(again.ClaimsTotals));
        var august = await w.MonthAsync("2026-08");
        Assert.Equal(["3|0|80000|3500|0|0|24000", "5.1|10000|60000|10000|0|2000|12000"], august.Claims.Select(Flat));
        Assert.Equal("10000|140000|13500|0|2000|36000", Totals(august.ClaimsTotals));
    }
}
