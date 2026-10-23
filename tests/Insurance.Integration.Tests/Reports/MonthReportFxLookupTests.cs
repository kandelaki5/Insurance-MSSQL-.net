using System.Numerics;
using Insurance.Application.Data;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The month report asks for each currency's month-end valuation once per report (legacy month-report-fx-lookup.test.ts,
/// PR299_MONTH_REPORT_REPEATED_FX_LOOKUP_20261018), through the real rate lookup (Rates) with the National Bank refusing every request.
/// </summary>
public sealed class MonthReportFxLookupTests
{
    private static async Task<ReportWorld> ForeignWorldAsync(bool withUsd = true)
    {
        var w = await CreateAsync();
        // An old GEL policy (base()'s Q).
        await w.PolicyAsync("P-Q", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 365m)], [("2026-01-01", 365m, "client")]);
        var usd = new List<PolicyRef>();
        for (var n = 1; n <= 3; n++)
            usd.Add(await w.PolicyAsync($"P-FX-{n}", "2026-11-01", "2027-11-01", [new LineSpec("5.1", 146m, 365m)], [("2026-11-01", 146m, "client")], "USD", 2.5m));
        await w.PolicyAsync("P-FX-4", "2026-11-01", "2027-11-01", [new LineSpec("5.1", 100m, 300m)], [("2026-11-01", 100m, "client")], "EUR", 3m);
        var claim = await w.ClaimAsync(usd[0].Id, usd[0].Lines[0], "USD", "2026-11-03", "2026-11-04");
        await w.ReserveAsync(claim.Id, "2026-11-05", 100m);
        await w.TreatyAsync("RX-FX", "excess_of_loss", "2026-11-01", "2026-11-30", ["13"], "USD", priority: 100m, treatyPremium: 3000m);
        if (withUsd) await w.RateAsync("USD", "2026-11-27", 2.654321m);
        await w.RateAsync("EUR", "2026-11-27", 2.912345m);
        if (withUsd) await w.RateAsync("USD", "2026-12-29", 2.688889m);
        await w.RateAsync("EUR", "2026-12-29", 2.95m);
        return w;
    }

    private static object Figures(PremiumReport.Report r) => new
    {
        Receivables = string.Join(";", r.Receivables.Where(x => x.Currency != "GEL").Select(x => $"{x.Currency} {x.Outstanding} {x.Overdue} {x.OutstandingGel} {x.Rate} {x.RateDate:yyyy-MM-dd}")),
        Rbns = r.Claims.FirstOrDefault(c => c.ClassCode == "5.1")?.RbnsGel,
        Treaty = r.TreatyPremiumGel,
    };

    // By hand (exact, half up): USD 3 × 146.00 × 2.654321 = 3 × 387.53; EUR 100.00 × 2.912345 = 291.23; RBNS 265.43; treaty 7 962.96.
    private static readonly object November = new
    {
        Receivables = "USD 43800 43800 116259 2.654321 2026-11-27;EUR 10000 10000 29123 2.912345 2026-11-27",
        Rbns = (BigInteger?)26543,
        Treaty = new BigInteger(796296),
    };

    [Fact]
    public async Task One_National_Bank_request_per_currency_and_the_figures_by_hand()
    {
        await using var w = await ForeignWorldAsync();
        var (rates, nbg) = RefusingNbg.For(w.App);
        var report = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-11"));
        Assert.Equal(2, nbg.Requests.Count);
        Assert.All(nbg.Requests, url => Assert.EndsWith("date=2026-11-30", url));
        Assert.Equal(November, Figures(report));
    }

    [Fact]
    public async Task The_next_report_asks_again_and_sees_a_rate_stored_meanwhile()
    {
        await using var w = await ForeignWorldAsync();
        var (rates, nbg) = RefusingNbg.For(w.App);
        await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-11"));
        nbg.Requests.Clear();
        var again = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-11"));
        Assert.Equal(2, nbg.Requests.Count);
        Assert.Equal(November, Figures(again));
        await w.RateAsync("USD", "2026-11-30", 2.7m);
        nbg.Requests.Clear();
        var later = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-11"));
        Assert.Single(nbg.Requests);
        var usd = later.Receivables.Single(x => x.Currency == "USD");
        Assert.Equal((new BigInteger(118260), 2.7m, D("2026-11-30"), (BigInteger?)27000, new BigInteger(810000)),
            (usd.OutstandingGel, usd.Rate, usd.RateDate, later.Claims.Single(c => c.ClassCode == "5.1").RbnsGel, later.TreatyPremiumGel));
    }

    [Fact]
    public async Task Two_reports_at_once_keep_their_own_months()
    {
        await using var w = await ForeignWorldAsync();
        var (rates, nbg) = RefusingNbg.For(w.App);
        var nov = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-11"));
        var dec = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-12"));
        nbg.Requests.Clear();
        var both = await Task.WhenAll(w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-11")), w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-12")));
        Assert.Equal(Figures(nov), Figures(both[0]));
        Assert.Equal(Figures(dec), Figures(both[1]));
        Assert.Equal([("USD", 2.688889m, D("2026-12-29")), ("EUR", 2.95m, D("2026-12-29"))], dec.Receivables.Where(x => x.Currency != "GEL").Select(x => (x.Currency, x.Rate, x.RateDate)));
        Assert.Equal(["date=2026-11-30", "date=2026-11-30", "date=2026-12-31", "date=2026-12-31"], nbg.Requests.Select(u => u[^15..]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task No_rate_stored_on_or_before_the_month_end_is_still_an_error()
    {
        await using var w = await ForeignWorldAsync(withUsd: false);
        var (rates, nbg) = RefusingNbg.For(w.App);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-11")));
        Assert.Contains("No USD rate on or before 2026-11-30", e.Message);
        Assert.Single(nbg.Requests);
    }

    [Fact]
    public async Task GEL_only_asks_nothing()
    {
        await using var w = await CreateAsync();
        await w.PolicyAsync("P-Q", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 365m)], [("2026-01-01", 365m, "client")]);
        var (rates, nbg) = RefusingNbg.For(w.App);
        var report = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-11"));
        Assert.Empty(nbg.Requests);
        Assert.Equal([("GEL", 1m, D("2026-11-30"))], report.Receivables.Select(x => (x.Currency, x.Rate, x.RateDate)));
    }
}
