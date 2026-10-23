using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// Reinsurance from the real writers (ReinsuranceStore: obligatory cessions on approval, facultative placement, cancellation mirrors, claim
/// shares) to the real monthly report, on SQL Server (legacy ri-commission-report, usd-ri-commission-report, usd-quota-cession,
/// two-quota-cent, late-premium-cession, cession-cancellation, cession-cancel-report, facultative-preflight / -repeat / -selected / -xl /
/// -cancel-report / -claim-chain, usd-facultative-report, surplus-chain, negative-endorsement(-claim), negative-commission-report,
/// xl-treaty-premium, usd-xl-valuation, usd-xl-prior-rate). Expected values worked by hand in the old tests.
/// </summary>
public sealed class ReinsuranceChainTests
{
    private sealed record CessionRow(Guid Row, Guid Treaty, decimal Share, decimal Amount, decimal AmountGel, decimal Commission, decimal CommissionGel,
        DateOnly Booked, DateOnly From, DateOnly To, Guid? Cancels, Guid Id);

    private static async Task<List<CessionRow>> CessionsAsync(ReportWorld w)
    {
        await using var c = await w.Database.OpenAdminAsync();
        return (await c.QueryAsync<CessionRow>("""
            select premium_transaction_id as Row, treaty_id as Treaty, share_percent as Share, amount as Amount, amount_gel as AmountGel, commission as Commission,
                   commission_gel as CommissionGel, booked_on as Booked, cover_from as [From], cover_to as [To], cancels_cession_id as Cancels, id as Id
            from core.premium_cession order by created_at, amount desc
            """)).ToList();
    }

    private static string Text(CessionRow c) =>
        string.Join("|", c.Share.ToString("0.0000"), c.Amount.ToString("0.00"), c.AmountGel.ToString("0.00"), c.Commission.ToString("0.00"), c.CommissionGel.ToString("0.00"),
            c.Booked.ToString("yyyy-MM-dd"), c.From.ToString("yyyy-MM-dd"), c.To.ToString("yyyy-MM-dd"));

    private static (BigInteger, BigInteger, BigInteger, BigInteger, BigInteger, BigInteger) Financial(PremiumReport.ClassRow c) =>
        (c.FinancialWrittenGel, c.EarnedGel, c.UnearnedGel, c.CededWrittenGel, c.CededUnearnedGel, c.RiCommissionGel);

    private static (BigInteger, BigInteger, BigInteger, BigInteger, BigInteger, BigInteger) Financial(PremiumReport.Totals c) =>
        (c.FinancialWrittenGel, c.EarnedGel, c.UnearnedGel, c.CededWrittenGel, c.CededUnearnedGel, c.RiCommissionGel);

    private static (BigInteger, BigInteger, BigInteger, BigInteger, BigInteger, BigInteger) F(long a, long b, long c, long d, long e, long f) => (a, b, c, d, e, f);

    /// <summary>A policy of one line (5.1 unless given) with its issue row; sum insured in GEL unless given.</summary>
    private static Task<PolicyRef> PolicyAsync(ReportWorld w, string number, decimal premium, decimal gel, string from = "2026-07-01", string to = "2027-07-01",
        string currency = "GEL", decimal rate = 1m, string classCode = "5.1", decimal sumInsured = 20000m, string? sumInsuredCurrency = null, string? bookedOn = null, Guid? replaces = null) =>
        w.PolicyAsync(number, from, to, [new LineSpec(classCode, premium, gel, sumInsured, sumInsuredCurrency ?? currency)], currency: currency, rate: rate, bookedOn: bookedOn, replaces: replaces);

    private static Task<string?> PlaceAsync(ReportWorld w, Guid policy, Guid treaty) =>
        Treaties.PlaceFacultativeAsync(w.App, new CurrentUser(w.Admin, "admin", "ადმინი", "admin", "ადმინისტრატორი"), policy.ToString(), treaty.ToString());

    private static Task CedeCancellationAsync(ReportWorld w, Guid row) => w.WriteAsync(async tx => { await ReinsuranceStore.CedeCancellationAsync(tx, row, w.Admin); return 0; });

    [Fact]
    public async Task A_20_percent_commission_from_the_premium_writer_to_the_July_report()
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-RIC", 365m, 365m);
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 30m, commissionPercent: 20m);
        await w.CedeAsync(p.IssueRows[0]);
        var c = Assert.Single(await CessionsAsync(w));
        Assert.Equal("30.0000|109.50|109.50|21.90|21.90|2026-07-01|2026-07-01|2027-07-01", Text(c));
        Assert.Null(c.Cancels);
        var r = await w.MonthAsync("2026-07");
        var row = Assert.Single(r.Classes);
        Assert.Equal((new BigInteger(10950), new BigInteger(10020), new BigInteger(2190)), (row.CededWrittenGel, row.CededUnearnedGel, row.RiCommissionGel));
        Assert.Equal((new BigInteger(10950), new BigInteger(10020), new BigInteger(2190)), (r.Totals.CededWrittenGel, r.Totals.CededUnearnedGel, r.Totals.RiCommissionGel));
        Assert.Equal(BigInteger.Zero, r.TreatyPremiumGel);
    }

    [Fact]
    public async Task A_USD_quota_with_a_20_percent_commission_cedes_each_currency_from_its_own_amount()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-07-01", 4m);
        var p = await PolicyAsync(w, "P-USD-RIC", 1.08m, 3.24m, currency: "USD", rate: 3m);
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], "USD", cededPercent: 30m, commissionPercent: 20m);
        var rates = w.Rates();
        await w.CedeAsync(p.IssueRows[0]);
        Assert.Equal("30.0000|0.32|0.97|0.06|0.19|2026-07-01|2026-07-01|2027-07-01", Text(Assert.Single(await CessionsAsync(w))));
        var r = await w.MonthAsync("2026-07", rates);
        var row = Assert.Single(r.Classes);
        Assert.Equal((new BigInteger(97), new BigInteger(89), new BigInteger(19)), (row.CededWrittenGel, row.CededUnearnedGel, row.RiCommissionGel));
        // The policy's receivable is not there (no instalment); the rate of 4 is never asked for this.
        Assert.Empty(r.Receivables);
        Assert.DoesNotContain(rates.Asked, a => a.Currency == "USD");
        // The premium report by policy: written 3.24, earned 0.28, UPR 2.96; ceded 0.97, earned 0.08, UPR 0.89; commission 0.02 / 0.17.
        var pp = Assert.Single((await w.PolicyPremiumAsync("2026-07-01", "2026-07-31")).Rows);
        Assert.Equal((324L, 28L, 296L, 97L, 8L, 89L, 2L, 17L), (pp.CorrectedGel, pp.EarnedGel, pp.UnearnedGel, pp.RiPremiumGel, pp.RiEarnedGel, pp.RiUnearnedGel, pp.CommissionEarnedGel, pp.CommissionUnearnedGel));
    }

    [Fact]
    public async Task A_USD_quota_cession_is_sliced_from_the_amount_booked_in_each_currency()
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-USD-Q", 1.02m, 3.06m, currency: "USD", rate: 3m);
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], "USD", cededPercent: 30m);
        await w.CedeAsync(p.IssueRows[0]);
        Assert.Equal("30.0000|0.31|0.92|0.00|0.00|2026-07-01|2026-07-01|2027-07-01", Text(Assert.Single(await CessionsAsync(w))));
    }

    [Fact]
    public async Task Two_50_percent_quotas_on_one_tetri_cede_it_once()
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-CENT", 0.01m, 0.01m);
        await w.TreatyAsync("RI-A", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 50m);
        await w.TreatyAsync("RI-B", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 50m);
        await w.CedeAsync(p.IssueRows[0]);
        var cessions = await CessionsAsync(w);
        Assert.Equal(2, cessions.Count);
        Assert.Equal(["0.00|0.00", "0.01|0.01"], cessions.Select(c => $"{c.Amount:0.00}|{c.AmountGel:0.00}").Order());
        Assert.Equal(100m, cessions.Sum(c => c.Share));
        var r = await w.MonthAsync("2026-07");
        Assert.Equal((BigInteger.One, BigInteger.One, BigInteger.Zero), (r.Totals.CededWrittenGel, r.Totals.CededUnearnedGel, r.Totals.RiCommissionGel));
    }

    [Fact]
    public async Task A_late_booked_premium_row_is_ceded_with_its_booking_date_and_its_original_cover_once()
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-LATE", 365m, 365m, "2026-03-01", "2027-03-01", bookedOn: "2026-04-01");
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 30m);
        await w.TreatyAsync("RI-CLOSED", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 10m, status: "closed");
        await w.TreatyAsync("RI-OTHER", "quota_share", "2026-01-01", "2027-12-31", ["5.2"], cededPercent: 10m);
        await w.CedeAsync(p.IssueRows[0]);
        Assert.Equal("30.0000|109.50|109.50|0.00|0.00|2026-04-01|2026-03-01|2027-03-01", Text(Assert.Single(await CessionsAsync(w))));
    }

    [Theory]
    [InlineData("365.00", "365.00", "109.50", "109.50", "-273.00", "-273.00", "2026-06-01", "2026-07-01", "-81.90", "-81.90", "GEL")]
    [InlineData("1.02", "1.02", "0.31", "0.31", "-0.68", "-0.68", "2026-07-01", "2026-08-01", "-0.21", "-0.21", "GEL")]
    [InlineData("100.00", "300.00", "30.00", "90.00", "-74.79", "-224.38", "2026-06-01", "2026-07-01", "-22.44", "-67.32", "USD")]
    [InlineData("1.02", "3.06", "0.31", "0.92", "-0.68", "-2.04", "2026-07-01", "2026-08-01", "-0.21", "-0.61", "USD")]
    public async Task A_cancellation_reverses_the_cession_actually_booked_on_its_own_treaty(string premium, string premiumGel, string ceded, string cededGel,
        string cancel, string cancelGel, string cancelFrom, string cancelBooked, string want, string wantGel, string currency)
    {
        await using var w = await CreateAsync();
        decimal M(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        var p = await PolicyAsync(w, "P-CANCEL", M(premium), M(premiumGel), "2026-03-01", "2027-03-01", currency, M(premiumGel) / M(premium), bookedOn: "2026-04-01");
        var old = await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], currency, cededPercent: 30m);
        var original = await w.CessionAsync(p.IssueRows[0], old, 30m, M(ceded), M(cededGel), "2026-04-01", "2026-03-01", "2027-03-01");
        await w.ExecAsync("update core.ri_treaty set status = N'closed' where id = @old", new { old });
        await w.TreatyAsync("RI050", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], currency, cededPercent: 50m);
        var row = await w.TxAsync(p.Lines[0], "cancellation", M(cancel), M(cancelGel), cancelBooked, cancelFrom, "2027-03-01", p.IssueRows[0]);
        await CedeCancellationAsync(w, row);
        var cessions = await CessionsAsync(w);
        Assert.Equal(2, cessions.Count);
        var mirror = cessions.Single(c => c.Row == row);
        Assert.Equal($"30.0000|{want}|{wantGel}|0.00|0.00|{cancelBooked}|{cancelFrom}|2027-03-01", Text(mirror));
        Assert.Equal((old, original), (mirror.Treaty, mirror.Cancels!.Value));
    }

    [Fact]
    public async Task A_cession_and_its_cancellation_from_the_real_writers_to_the_July_and_August_reports()
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-CCR", 1.02m, 1.02m, "2026-03-01", "2027-03-01", bookedOn: "2026-04-01");
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 30m);
        await w.CedeAsync(p.IssueRows[0]);
        Assert.Equal("30.0000|0.31|0.31|0.00|0.00|2026-04-01|2026-03-01|2027-03-01", Text(Assert.Single(await CessionsAsync(w))));
        var julyBefore = await w.MonthAsync("2026-07");
        Assert.Equal(F(0, 9, 59, 0, 18, 0), Financial(julyBefore.Classes.Single()));
        var row = await w.TxAsync(p.Lines[0], "cancellation", -0.68m, -0.68m, "2026-08-01", "2026-07-01", "2027-03-01", p.IssueRows[0]);
        await CedeCancellationAsync(w, row);
        var cessions = await CessionsAsync(w);
        Assert.Equal("30.0000|-0.21|-0.21|0.00|0.00|2026-08-01|2026-07-01|2027-03-01", Text(cessions.Single(c => c.Row == row)));
        Assert.Equal(cessions.Single(c => c.Row == p.IssueRows[0]).Id, cessions.Single(c => c.Row == row).Cancels);
        Assert.Equal(Financial(julyBefore.Classes.Single()), Financial((await w.MonthAsync("2026-07")).Classes.Single()));
        var august = await w.MonthAsync("2026-08");
        Assert.Equal(F(-68, -9, 0, -21, 0, 0), Financial(august.Classes.Single()));
        Assert.Equal(F(-68, -9, 0, -21, 0, 0), Financial(august.Totals));
    }

    [Theory]
    [InlineData(80, "292.00", "over-ceded")]
    [InlineData(70, "255.50", "placed")]
    public async Task A_facultative_placement_is_checked_on_every_premium_row_before_anything_is_written(int second, string secondAmount, string outcome)
    {
        await using var w = await CreateAsync();
        var p = await w.PolicyAsync("P-PRE", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 365m, 365m), new LineSpec("3.2", 365m, 365m)]);
        var fac = await w.TreatyAsync("RF030", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 30m);
        var q20 = await w.TreatyAsync("RI020", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 20m);
        var q2 = await w.TreatyAsync($"RI0{second}", "quota_share", "2026-01-01", "2027-12-31", ["3.2"], cededPercent: second);
        await w.CessionAsync(p.IssueRows[0], q20, 20m, 73m, 73m, "2026-07-01", "2026-07-01", "2027-07-01");
        await w.CessionAsync(p.IssueRows[1], q2, second, decimal.Parse(secondAmount), decimal.Parse(secondAmount), "2026-07-01", "2026-07-01", "2027-07-01");
        Assert.Equal(outcome, await PlaceAsync(w, p.Id, fac));
        var cessions = await CessionsAsync(w);
        if (outcome == "over-ceded") Assert.Equal(2, cessions.Count);
        else Assert.Equal(["30.0000|109.50|109.50|0.00|0.00|2026-07-01|2026-07-01|2027-07-01", "30.0000|109.50|109.50|0.00|0.00|2026-07-01|2026-07-01|2027-07-01"],
            cessions.Where(c => c.Treaty == fac).Select(Text));
    }

    [Fact]
    public async Task A_facultative_treaty_placed_by_hand_and_the_same_placement_again()
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-FAC", 365m, 365m);
        var other = await PolicyAsync(w, "P-OTHER", 365m, 365m);
        var fac = await w.TreatyAsync("RF030", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 30m);
        var q = await w.TreatyAsync("RI020", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 20m);
        await w.CessionAsync(p.IssueRows[0], q, 20m, 73m, 73m, "2026-07-01", "2026-07-01", "2027-07-01");
        await w.CessionAsync(other.IssueRows[0], fac, 30m, 109.50m, 109.50m, "2026-07-01", "2026-07-01", "2027-07-01");
        Assert.Equal("placed", await PlaceAsync(w, p.Id, fac));
        var after = await CessionsAsync(w);
        Assert.Equal(3, after.Count);
        Assert.Equal("30.0000|109.50|109.50|0.00|0.00|2026-07-01|2026-07-01|2027-07-01", Text(after.Single(c => c.Row == p.IssueRows[0] && c.Treaty == fac)));
        Assert.Equal("already-placed", await PlaceAsync(w, p.Id, fac));
        Assert.Equal(after.Select(Text), (await CessionsAsync(w)).Select(Text));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("quota")]
    [InlineData("missing")]
    [InlineData("active")]
    public async Task A_facultative_placement_uses_the_chosen_treaty_and_only_an_active_facultative_one(string chosen)
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-SEL", 365m, 365m);
        await w.TreatyAsync("RF040", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 40m);
        var selected = chosen switch
        {
            "missing" => Guid.NewGuid(),
            "quota" => await w.TreatyAsync("RF030", "quota_share", "2026-01-01", "2027-12-31", ["13"], cededPercent: 30m),
            _ => await w.TreatyAsync("RF030", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 30m, status: chosen == "closed" ? "closed" : "active"),
        };
        var outcome = await PlaceAsync(w, p.Id, selected);
        var cessions = await CessionsAsync(w);
        if (chosen != "active")
        {
            Assert.Equal("not-found", outcome);
            Assert.Empty(cessions);
        }
        else
        {
            Assert.Equal("placed", outcome);
            Assert.Equal((selected, "30.0000|109.50|109.50|0.00|0.00|2026-07-01|2026-07-01|2027-07-01"), (Assert.Single(cessions).Treaty, Text(cessions[0])));
        }
    }

    [Theory]
    [InlineData("parent", "xol-with-proportional")]
    [InlineData("closed", "placed")]
    [InlineData("other-class", "placed")]
    [InlineData("ended-before", "placed")]
    [InlineData("starts-on-end", "placed")]
    [InlineData("another-policy", "placed")]
    public async Task A_facultative_placement_and_the_excess_of_loss_treaties_around_the_policy(string xl, string outcome)
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-XL", 365m, 365m);
        await PolicyAsync(w, "P-XL-OTHER", 365m, 365m, classCode: "13");
        var fac = await w.TreatyAsync("RF030", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 30m);
        var (cls, from, to, status) = xl switch
        {
            "parent" => ("5", "2026-01-01", "2027-12-31", "active"),
            "closed" => ("5", "2026-01-01", "2027-12-31", "closed"),
            "other-class" => ("13", "2026-01-01", "2027-12-31", "active"),
            "ended-before" => ("5", "2025-01-01", "2026-06-30", "active"),
            "starts-on-end" => ("5", "2027-07-01", "2027-12-31", "active"),
            _ => ("13", "2026-01-01", "2027-12-31", "active"),
        };
        await w.TreatyAsync("RX-" + xl, "excess_of_loss", from, to, [cls], priority: 500m, limit: 5000m, status: status);
        Assert.Equal(outcome, await PlaceAsync(w, p.Id, fac));
        Assert.Equal(outcome == "placed" ? 1 : 0, (await CessionsAsync(w)).Count);
    }

    [Fact]
    public async Task A_quota_and_a_hand_placed_facultative_share_cancelled_together()
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-FCR", 1.02m, 1.02m, "2026-03-01", "2027-03-01", bookedOn: "2026-04-01");
        var q = await w.TreatyAsync("RI020", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 20m);
        var fac = await w.TreatyAsync("RF030", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 30m);
        await w.CedeAsync(p.IssueRows[0]);
        Assert.Equal("placed", await PlaceAsync(w, p.Id, fac));
        var originals = await CessionsAsync(w);
        Assert.Equal("20.0000|0.20|0.20|0.00|0.00|2026-04-01|2026-03-01|2027-03-01", Text(originals.Single(c => c.Treaty == q)));
        Assert.Equal("30.0000|0.31|0.31|0.00|0.00|2026-04-01|2026-03-01|2027-03-01", Text(originals.Single(c => c.Treaty == fac)));
        var julyBefore = await w.MonthAsync("2026-07");
        Assert.Equal(F(0, 9, 59, 0, 30, 0), Financial(julyBefore.Totals));
        var row = await w.TxAsync(p.Lines[0], "cancellation", -0.68m, -0.68m, "2026-08-01", "2026-07-01", "2027-03-01", p.IssueRows[0]);
        await CedeCancellationAsync(w, row);
        var all = await CessionsAsync(w);
        Assert.Equal(4, all.Count);
        Assert.Equal("20.0000|-0.13|-0.13|0.00|0.00|2026-08-01|2026-07-01|2027-03-01", Text(all.Single(c => c.Row == row && c.Treaty == q)));
        Assert.Equal("30.0000|-0.21|-0.21|0.00|0.00|2026-08-01|2026-07-01|2027-03-01", Text(all.Single(c => c.Row == row && c.Treaty == fac)));
        Assert.Equal(originals.Single(c => c.Treaty == q).Id, all.Single(c => c.Row == row && c.Treaty == q).Cancels);
        Assert.Equal(originals.Single(c => c.Treaty == fac).Id, all.Single(c => c.Row == row && c.Treaty == fac).Cancels);
        Assert.Equal(Financial(julyBefore.Totals), Financial((await w.MonthAsync("2026-07")).Totals));
        Assert.Equal(F(-68, -9, 0, -34, 0, 0), Financial((await w.MonthAsync("2026-08")).Totals));
    }

    [Fact]
    public async Task A_USD_quota_and_a_hand_placed_USD_facultative_share_each_column_on_its_own()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-07-31", 4m);
        var p = await PolicyAsync(w, "P-UFR", 1.02m, 3.06m, currency: "USD", rate: 3m);
        await w.TreatyAsync("RI020", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], "USD", cededPercent: 20m);
        var fac = await w.TreatyAsync("RF030", "facultative", "2026-01-01", "2027-12-31", [], "USD", cededPercent: 30m);
        await w.CedeAsync(p.IssueRows[0]);
        Assert.Equal("placed", await PlaceAsync(w, p.Id, fac));
        Assert.Equal(["20.0000|0.20|0.61|0.00|0.00|2026-07-01|2026-07-01|2027-07-01", "30.0000|0.31|0.92|0.00|0.00|2026-07-01|2026-07-01|2027-07-01"], (await CessionsAsync(w)).Select(Text));
        Assert.Equal(F(306, 26, 280, 153, 140, 0), Financial((await w.MonthAsync("2026-07")).Totals));
    }

    private static async Task<IReadOnlyList<ReinsuranceStore.ClaimShare>> SharesAsync(ReportWorld w, Guid claim, string on) =>
        await w.App.ReadTxAsync(tx => ReinsuranceStore.ClaimSharesAsync(tx, claim, D(on)));

    [Fact]
    public async Task A_hand_placed_facultative_share_reaches_the_claim_with_the_lines_quota()
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-FCC", 365m, 365m);
        var q = await w.TreatyAsync("RI020", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 20m);
        var fac = await w.TreatyAsync("RF030", "facultative", "2026-01-01", "2027-12-31", [], cededPercent: 30m);
        await w.CedeAsync(p.IssueRows[0]);
        Assert.Equal("placed", await PlaceAsync(w, p.Id, fac));
        Assert.Equal(["20.0000|73.00", "30.0000|109.50"], (await CessionsAsync(w)).Select(c => $"{c.Share:0.0000}|{c.Amount:0.00}"));
        await w.ExecAsync("update core.ri_treaty set status = N'closed' where id in (@q, @fac)", new { q, fac });
        await w.TreatyAsync("RI050", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 50m);
        var claim = await w.ClaimAsync(p.Id, p.Lines[0], "GEL", "2026-07-15", "2026-07-15");
        await w.AttachClaimCessionsAsync(claim.Id, p.Lines[0], "5.1", "2026-07-15");
        await using (var c = await w.Database.OpenAdminAsync())
            Assert.Equal([(fac, 30m), (q, 20m)], (await c.QueryAsync<(Guid, decimal)>("select treaty_id, share_percent from core.claim_cession where claim_id = @id order by share_percent desc", new { id = claim.Id })).ToList());
        await w.ReserveAsync(claim.Id, "2026-07-16", 1000m);
        var july19 = await SharesAsync(w, claim.Id, "2026-07-19");
        Assert.Equal([(fac, 30000L, 0L), (q, 20000L, 0L)], july19.Select(s => (s.TreatyId, s.InReserve, s.InPaid)).OrderByDescending(s => s.InReserve));
        await w.ClaimPaymentAsync(claim.Id, "2026-07-20", 200m);
        await w.ReserveAsync(claim.Id, "2026-07-20", -200m);
        var july31 = await SharesAsync(w, claim.Id, "2026-07-31");
        Assert.Equal([(fac, 24000L, 6000L), (q, 16000L, 4000L)], july31.Select(s => (s.TreatyId, s.InReserve, s.InPaid)).OrderByDescending(s => s.InReserve));
        Assert.Equal(july19.Select(s => (s.TreatyId, s.InReserve, s.InPaid)), (await SharesAsync(w, claim.Id, "2026-07-19")).Select(s => (s.TreatyId, s.InReserve, s.InPaid)));
    }

    [Theory]
    [InlineData(300000, "GEL", "66.6667", "200.00")]
    [InlineData(1000000, "GEL", "40.0000", "120.00")]
    [InlineData(100000, "GEL", null, null)]
    [InlineData(300000, "USD", null, null)]
    public async Task One_GEL_surplus_from_the_premium_writer_to_the_claim_writer_and_the_reader(int sumInsured, string sumCurrency, string? share, string? ceded)
    {
        await using var w = await CreateAsync();
        var p = await PolicyAsync(w, "P-SUR", 300m, 300m, sumInsured: sumInsured, sumInsuredCurrency: sumCurrency);
        await w.TreatyAsync("RS001", "surplus", "2026-01-01", "2027-12-31", ["5.1"], retention: 100000m, lines: 4);
        await w.CedeAsync(p.IssueRows[0]);
        var cessions = await CessionsAsync(w);
        if (share is null)
        {
            Assert.Empty(cessions);
            return;
        }
        Assert.Equal($"{share}|{ceded}|{ceded}|0.00|0.00|2026-07-01|2026-07-01|2027-07-01", Text(Assert.Single(cessions)));
        var claim = await w.ClaimAsync(p.Id, p.Lines[0], "GEL", "2026-07-10", "2026-07-10");
        await w.AttachClaimCessionsAsync(claim.Id, p.Lines[0], "5.1", "2026-07-10");
        await w.ReserveAsync(claim.Id, "2026-07-10", 300m);
        var at = Assert.Single(await SharesAsync(w, claim.Id, "2026-07-31"));
        Assert.Equal(decimal.Parse(share), at.SharePercent);
        Assert.Equal((share == "66.6667" ? 20000L : 12000L, 0L), (at.InReserve, at.InPaid));
        if (share != "66.6667") return;
        await w.ClaimPaymentAsync(claim.Id, "2026-07-20", 100m);
        await w.ReserveAsync(claim.Id, "2026-07-20", -100m);
        var after = Assert.Single(await SharesAsync(w, claim.Id, "2026-07-31"));
        Assert.Equal((13333L, 6667L), (after.InReserve, after.InPaid));
    }

    [Theory]
    [InlineData(0, "0.00", "0")]
    [InlineData(20, "-2.00", "-200")]
    public async Task A_booked_reduction_is_ceded_with_its_sign_and_read_in_the_monthly_report(int commission, string reductionCommission, string augustCommission)
    {
        await using var w = await CreateAsync();
        var original = await PolicyAsync(w, "P-ORIG", 365m, 365m);
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 30m, commissionPercent: commission);
        await w.CedeAsync(original.IssueRows[0]);
        var july = await w.MonthAsync("2026-07");
        Assert.Equal(F(36500, 3100, 33400, 10950, 10020, commission == 0 ? 0 : 2190), Financial(july.Totals));
        var reduction = await PolicyAsync(w, "P-ORIG/1", -33.40m, -33.40m, "2026-08-01", "2027-07-01", replaces: original.Id);
        await w.CedeAsync(reduction.IssueRows[0]);
        var neg = (await CessionsAsync(w)).Single(c => c.Row == reduction.IssueRows[0]);
        Assert.Equal($"30.0000|-10.02|-10.02|{reductionCommission}|{reductionCommission}|2026-08-01|2026-08-01|2027-07-01", Text(neg));
        Assert.Null(neg.Cancels);
        Assert.Equal(Financial(july.Totals), Financial((await w.MonthAsync("2026-07")).Totals));
        Assert.Equal(F(-3340, 2790, 27270, -1002, 8181, long.Parse(augustCommission)), Financial((await w.MonthAsync("2026-08")).Totals));
    }

    [Fact]
    public async Task A_claim_on_a_reduction_takes_the_positive_share_stored_on_its_negative_cession()
    {
        await using var w = await CreateAsync();
        var original = await PolicyAsync(w, "P-ORIG", 365m, 365m);
        var q = await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 30m);
        await w.CedeAsync(original.IssueRows[0]);
        var reduction = await PolicyAsync(w, "P-ORIG/1", -33.40m, -33.40m, "2026-08-01", "2027-07-01", replaces: original.Id);
        await w.CedeAsync(reduction.IssueRows[0]);
        await w.ExecAsync("update core.ri_treaty set status = N'closed' where id = @q", new { q });
        await w.TreatyAsync("RI050", "quota_share", "2026-01-01", "2027-01-01", ["5.1"], cededPercent: 50m);
        var claim = await w.ClaimAsync(reduction.Id, reduction.Lines[0], "GEL", "2026-08-15", "2026-08-15");
        await w.AttachClaimCessionsAsync(claim.Id, reduction.Lines[0], "5.1", "2026-08-15");
        await w.ReserveAsync(claim.Id, "2026-08-16", 1000m);
        var at19 = Assert.Single(await SharesAsync(w, claim.Id, "2026-08-19"));
        Assert.Equal((q, 30m, 30000L, 0L), (at19.TreatyId, at19.SharePercent, at19.InReserve, at19.InPaid));
        await w.ClaimPaymentAsync(claim.Id, "2026-08-20", 200m);
        await w.ReserveAsync(claim.Id, "2026-08-20", -200m);
        var at31 = Assert.Single(await SharesAsync(w, claim.Id, "2026-08-31"));
        Assert.Equal((24000L, 6000L), (at31.InReserve, at31.InPaid));
    }

    [Fact]
    public async Task The_fixed_premium_of_excess_of_loss_treaties_is_spread_by_days_and_closing_them_later_changes_no_past_month()
    {
        await using var w = await CreateAsync();
        var year = await w.TreatyAsync("RX101", "excess_of_loss", "2026-01-01", "2026-12-31", ["13"], priority: 100m, limit: 1000m, treatyPremium: 365m);
        var shortOne = await w.TreatyAsync("RX102", "excess_of_loss", "2026-07-15", "2026-08-14", ["13"], priority: 100m, limit: 1000m, treatyPremium: 31m);
        var rates = w.Rates();
        Assert.Equal(new BigInteger(2800), (await w.MonthAsync("2026-02", rates)).TreatyPremiumGel);
        var july = await w.MonthAsync("2026-07", rates);
        Assert.Equal(new BigInteger(4800), july.TreatyPremiumGel);
        Assert.Equal(new BigInteger(4500), (await w.MonthAsync("2026-08", rates)).TreatyPremiumGel);
        Assert.Equal((BigInteger.Zero, BigInteger.Zero), ((await w.MonthAsync("2025-12")).TreatyPremiumGel, (await w.MonthAsync("2027-01")).TreatyPremiumGel));
        Assert.Empty(july.Classes);
        await w.ExecAsync("update core.ri_treaty set status = N'closed' where id in (@year, @shortOne)", new { year, shortOne });
        Assert.Equal(july.TreatyPremiumGel, (await w.MonthAsync("2026-07")).TreatyPremiumGel);
    }

    [Fact]
    public async Task A_USD_excess_of_loss_premium_is_valued_at_the_stored_month_end_rate()
    {
        await using var w = await CreateAsync();
        await w.TreatyAsync("RX201", "excess_of_loss", "2026-01-01", "2026-12-31", ["13"], "USD", priority: 100m, limit: 1000m, treatyPremium: 365m);
        await w.RateAsync("USD", "2026-01-01", 2m);
        await w.RateAsync("USD", "2026-07-31", 3.1415m);
        await w.RateAsync("USD", "2026-08-31", 4m);
        await w.RateAsync("USD", "2026-09-30", 5m);
        var (rates, nbg) = RefusingNbg.For(w.App);
        var july = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-07"));
        Assert.Equal(new BigInteger(9739), july.TreatyPremiumGel);
        Assert.Equal(new BigInteger(12400), (await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-08"))).TreatyPremiumGel);
        Assert.Empty(nbg.Requests);
        await w.RateAsync("USD", "2026-10-31", 6m);
        Assert.Equal(july.TreatyPremiumGel, (await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-07"))).TreatyPremiumGel);
    }

    [Fact]
    public async Task With_no_rate_for_the_month_end_the_latest_earlier_stored_rate_is_used_and_none_is_an_error()
    {
        await using var w = await CreateAsync();
        await w.TreatyAsync("RX301", "excess_of_loss", "2026-01-01", "2026-12-31", ["13"], "USD", priority: 100m, limit: 1000m, treatyPremium: 365m);
        await w.RateAsync("USD", "2026-07-01", 2m);
        await w.RateAsync("USD", "2026-07-30", 3.1415m);
        await w.RateAsync("EUR", "2026-07-31", 7m);
        await w.RateAsync("USD", "2026-08-01", 9m);
        var (rates, nbg) = RefusingNbg.For(w.App);
        Assert.Equal((3.1415m, D("2026-07-30")), await rates.LatestRateOnOrBeforeAsync("USD", D("2026-07-31")));
        Assert.Equal(new BigInteger(9739), (await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-07"))).TreatyPremiumGel);
        Assert.NotEmpty(nbg.Requests);
        await w.RateAsync("USD", "2026-08-31", 10m);
        Assert.Equal(new BigInteger(9739), (await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-07"))).TreatyPremiumGel);
        await using var none = await CreateAsync();
        await none.TreatyAsync("RX301", "excess_of_loss", "2026-01-01", "2026-12-31", ["13"], "USD", priority: 100m, limit: 1000m, treatyPremium: 365m);
        await none.RateAsync("EUR", "2026-07-31", 7m);
        await none.RateAsync("USD", "2026-08-01", 9m);
        var (noneRates, _) = RefusingNbg.For(none.App);
        var helper = await Assert.ThrowsAsync<InvalidOperationException>(() => noneRates.LatestRateOnOrBeforeAsync("USD", D("2026-07-31")));
        Assert.Equal("No USD rate on or before 2026-07-31", helper.Message);
        var report = await Assert.ThrowsAsync<InvalidOperationException>(() => none.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, noneRates, "2026-07")));
        Assert.Equal(helper.Message, report.Message);
    }
}
