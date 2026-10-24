using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.Reports.PremiumReportPrecisionTests;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The monthly report's claims figures, exactly, on a real database: gross paid and recoveries (legacy precision-month-report-paid.test.ts),
/// RBNS (…-rbns.test.ts), statutory IBNR (…-ibnr.test.ts) and the reinsurer's shares (…-ri-shares.test.ts). Expected values by hand.
/// </summary>
public sealed class PremiumReportClaimsTests
{
    private const string L = "90071992547408.01", X = "9999999999999999.99";
    private static decimal M(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Tetri × a rate to the millionth, a half towards +∞ (Math.round's rule), by hand.</summary>
    private static BigInteger HalfUp(BigInteger minor, string rate)
    {
        var n = 2 * minor * BigInteger.Parse(rate.Replace(".", "")) + 1_000_000;
        var q = BigInteger.Divide(n, 2_000_000);
        return !(n % 2_000_000).IsZero && n.Sign < 0 ? q - 1 : q;
    }
    /// <summary>round(amount × rate, 2) half away from zero, by hand.</summary>
    private static BigInteger AtOwnRate(BigInteger minor, string rate) => ReportMath.DivRound(minor * BigInteger.Parse(rate.Replace(".", "")), 1_000_000);

    private static async Task<(Guid Policy, Guid Line)> LineAsync(ReportWorld w, string number, string classCode, string currency = "GEL")
    {
        var p = await w.PolicyAsync(number, "2026-09-01", "2027-09-01", [new LineSpec(classCode, 1000m, 1000m)], currency: currency, issueRows: false);
        return (p.Id, p.Lines[0]);
    }

    private static async Task<Guid> ClaimWithAsync(ReportWorld w, (Guid Policy, Guid Line) l, string currency, (string, string, string)[]? paid = null, (string, string)[]? recovered = null, (string, string)[]? reserves = null)
    {
        var claim = await w.ClaimAsync(l.Policy, l.Line, currency, "2026-09-05", "2026-09-06");
        Guid last = default;
        foreach (var (on, amount, rate) in paid ?? [])
        {
            if (amount.StartsWith('-')) await w.ClaimPaymentReversalAsync(last, on);
            else last = await w.ClaimPaymentAsync(claim.Id, on, M(amount), M(rate));
        }
        Guid lastRecovery = default;
        foreach (var (on, amount) in recovered ?? [])
            lastRecovery = await w.RecoveryAsync(claim.Id, "salvage", on, M(amount), amount.StartsWith('-') ? lastRecovery : null);
        foreach (var (on, change) in reserves ?? []) await w.ReserveAsync(claim.Id, on, M(change));
        return claim.Id;
    }

    // ---------------------------------------------------------------- paid and recovered

    [Fact]
    public async Task October_paid_and_recovered_to_the_tetri_in_two_classes()
    {
        await using var w = await CreateAsync();
        var a = await LineAsync(w, "P-PAID-A", "5.1");
        await ClaimWithAsync(w, a, "GEL", [("2026-10-03", "365.00", "1.000000")], [("2026-10-04", "365.00")]);
        await ClaimWithAsync(w, a, "GEL", [("2026-10-05", L, "1.000000")], [("2026-10-06", L)]);
        await ClaimWithAsync(w, a, "GEL", [("2026-10-20", X, "1.000000")], [("2026-10-21", X)]);
        var b = await LineAsync(w, "P-PAID-B", "13");
        await ClaimWithAsync(w, b, "GEL", [("2026-10-07", L, "1.000000")], [("2026-10-08", L)]);
        BigInteger c51 = T("365.00") + T(L) + T(X), c13 = T(L);
        Assert.Equal(BigInteger.Parse("1009007199254777300"), c51);
        var r = await w.MonthAsync("2026-10");
        Assert.Equal((c51, c51), (r.Claims.Single(c => c.ClassCode == "5.1").PaidGel, r.Claims.Single(c => c.ClassCode == "5.1").RecoveredGel));
        Assert.Equal((c13, c13), (r.Claims.Single(c => c.ClassCode == "13").PaidGel, r.Claims.Single(c => c.ClassCode == "13").RecoveredGel));
        Assert.Equal((c51 + c13, c51 + c13), (r.ClaimsTotals.PaidGel, r.ClaimsTotals.RecoveredGel));
        Assert.Equal(["5.1", "13"], r.Claims.Select(c => c.ClassCode));
    }

    [Theory]
    [InlineData(L, "90,071,992,547,408.01")]
    [InlineData(X, "9,999,999,999,999,999.99")]
    [InlineData("365.00", "365.00")]
    public async Task One_payment_and_one_recovery_alone(string amount, string text)
    {
        await using var w = await CreateAsync();
        await ClaimWithAsync(w, await LineAsync(w, "P-S", "5.1"), "GEL", [("2026-10-05", amount, "1.000000")], [("2026-10-06", amount)]);
        var r = await w.MonthAsync("2026-10");
        Assert.Equal((text, text), (Shown(r.ClaimsTotals.PaidGel), Shown(r.ClaimsTotals.RecoveredGel)));
    }

    [Fact]
    public async Task A_payment_is_valued_at_its_own_rate_rounded_on_its_own_and_a_reversal_half_away_from_zero()
    {
        await using var w = await CreateAsync();
        var l = await LineAsync(w, "P-R", "5.1");
        await ClaimWithAsync(w, l, "USD", [("2026-10-10", "100.00", "2.700000")]);
        var e = await w.ClaimAsync(l.Policy, l.Line, "EUR", "2026-09-05", "2026-09-06");
        Guid last = default;
        foreach (var day in new[] { "2026-10-11", "2026-10-12", "2026-10-13" }) last = await w.ClaimPaymentAsync(e.Id, day, 0.05m, 2.5m);
        Assert.Equal(new BigInteger[] { 27000, 13, -13 }, new[] { AtOwnRate(10000, "2.700000"), AtOwnRate(5, "2.500000"), AtOwnRate(-5, "2.500000") });
        Assert.Equal(new BigInteger(27039), (await w.MonthAsync("2026-10")).Claims.Single().PaidGel);
        await w.ClaimPaymentReversalAsync(last, "2026-10-14");
        Assert.Equal("270.26", Shown((await w.MonthAsync("2026-10")).Claims.Single().PaidGel));
    }

    [Fact]
    public async Task Payments_and_recoveries_belong_to_their_own_months()
    {
        await using var w = await CreateAsync();
        var l = await LineAsync(w, "P-D", "5.1");
        var claim = await w.ClaimAsync(l.Policy, l.Line, "GEL", "2026-09-05", "2026-09-06");
        await w.ClaimPaymentAsync(claim.Id, "2026-09-20", 100m);
        await w.ClaimPaymentAsync(claim.Id, "2026-10-02", M(L));
        await w.RecoveryAsync(claim.Id, "salvage", "2026-09-21", 50m);
        var octRecovery = await w.RecoveryAsync(claim.Id, "salvage", "2026-10-03", 0.01m);
        var sept = (await w.MonthAsync("2026-09")).Claims.Single();
        Assert.Equal((new BigInteger(10000), new BigInteger(5000)), (sept.PaidGel, sept.RecoveredGel));
        var october = await w.MonthAsync("2026-10");
        Assert.Equal((T(L), BigInteger.One), (october.Claims.Single().PaidGel, october.Claims.Single().RecoveredGel));
        await w.ClaimPaymentAsync(claim.Id, "2026-11-05", M(X));
        await w.RecoveryAsync(claim.Id, "salvage", "2026-11-06", -0.01m, octRecovery);
        await w.RecoveryAsync(claim.Id, "salvage", "2026-11-07", M(L));
        Assert.Equal(october.ClaimsTotals, (await w.MonthAsync("2026-10")).ClaimsTotals);
        var nov = (await w.MonthAsync("2026-11")).ClaimsTotals;
        Assert.Equal(("9,999,999,999,999,999.99", "90,071,992,547,408.00"), (Shown(nov.PaidGel), Shown(nov.RecoveredGel)));
    }

    [Fact]
    public async Task A_large_USD_payment_at_its_own_rate_to_the_tetri()
    {
        await using var w = await CreateAsync();
        await ClaimWithAsync(w, await LineAsync(w, "P-U", "5.1"), "USD", [("2026-10-12", L, "2.800000")]);
        var gel = AtOwnRate(T(L), "2.800000");
        Assert.Equal(BigInteger.Parse("25220157913274243"), gel);
        Assert.Equal(gel, (await w.MonthAsync("2026-10")).ClaimsTotals.PaidGel);
    }

    // ---------------------------------------------------------------- RBNS

    [Fact]
    public async Task RBNS_of_each_class_is_the_reserve_to_its_last_tetri_and_an_unapproved_request_is_no_reserve()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-10-31", 2.8m);
        await w.RateAsync("EUR", "2026-10-31", 2.5m);
        await w.RateAsync("USD", "2026-11-15", 9.9m);
        await w.RateAsync("USD", "2026-11-30", 3.0m);
        await w.RateAsync("EUR", "2026-11-30", 2.6m);
        var a = await LineAsync(w, "P-RBNS-A", "5.1");
        var ca = await ClaimWithAsync(w, a, "GEL", reserves: [("2026-09-10", "365.00")]);
        await w.ExecAsync("""
            insert core.claim_reserve_request (claim_id, booked_on, base_level, new_level, change, reason, created_by)
            values (@ca, '2026-10-10', 365.00, 1365.00, 1000.00, N'მოთხოვნა', @user)
            """, new { ca, user = w.Accountant });
        var cb = await ClaimWithAsync(w, a, "GEL", reserves: [("2026-10-05", L)]);
        await ClaimWithAsync(w, a, "GEL", reserves: [("2026-10-20", X), ("2026-10-25", "-0.99")]);
        await ClaimWithAsync(w, a, "USD", reserves: [("2026-10-12", "123.45")]);
        var b = await LineAsync(w, "P-RBNS-B", "13");
        await ClaimWithAsync(w, b, "GEL", reserves: [("2026-10-07", L)]);
        await ClaimWithAsync(w, b, "EUR", reserves: [("2026-10-08", "0.05")]);
        var oct51 = T("365.00") + T(L) + T(X) - 99 + HalfUp(T("123.45"), "2.800000");
        var oct13 = T(L) + HalfUp(5, "2.500000");
        Assert.Equal(new BigInteger[] { 34566, 13 }, new[] { HalfUp(T("123.45"), "2.800000"), HalfUp(5, "2.500000") });
        var rates = w.Rates();
        var r = await w.MonthAsync("2026-10", rates);
        Assert.Equal((oct51, oct13, oct51 + oct13), (r.Claims.Single(c => c.ClassCode == "5.1").RbnsGel, r.Claims.Single(c => c.ClassCode == "13").RbnsGel, r.ClaimsTotals.RbnsGel));
        Assert.Equal(BigInteger.Parse("1009007199254811767"), oct51);
        Assert.Equal("90,071,992,547,408.14", Shown(r.Claims.Single(c => c.ClassCode == "13").RbnsGel));
        // Each currency's month-end rate asked once for the report.
        Assert.Equal(2, rates.Asked.Count(x => x.Currency != "GEL"));
        // September has only September's 365.00; a November change leaves October as it was; November at its own month end.
        var sept = await w.MonthAsync("2026-09");
        Assert.Equal(new BigInteger(36500), sept.ClaimsTotals.RbnsGel);
        await w.ReserveAsync(cb, "2026-11-03", 500m);
        Assert.Equal(r.ClaimsTotals, (await w.MonthAsync("2026-10")).ClaimsTotals);
        var nov = await w.MonthAsync("2026-11");
        var nov51 = T("365.00") + T(L) + 50000 + T(X) - 99 + HalfUp(T("123.45"), "3.000000");
        var nov13 = T(L) + HalfUp(5, "2.600000");
        Assert.Equal((nov51, nov13), (nov.Claims.Single(c => c.ClassCode == "5.1").RbnsGel, nov.Claims.Single(c => c.ClassCode == "13").RbnsGel));
        _ = ca;
    }

    [Fact]
    public async Task A_large_USD_reserve_and_ordinary_amounts_at_the_month_end_rate_half_up()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-10-31", 2.8m);
        await w.RateAsync("EUR", "2026-10-31", 2.5m);
        var l = await LineAsync(w, "P-U", "5.1");
        await ClaimWithAsync(w, l, "USD", reserves: [("2026-10-12", L)]);
        Assert.Equal(BigInteger.Parse("25220157913274243"), (await w.MonthAsync("2026-10")).ClaimsTotals.RbnsGel);
        await using var o = await CreateAsync();
        await o.RateAsync("USD", "2026-10-31", 2.8m);
        await o.RateAsync("EUR", "2026-10-31", 2.5m);
        var ol = await LineAsync(o, "P-O", "5.1");
        await ClaimWithAsync(o, ol, "USD", reserves: [("2026-10-12", "123.45"), ("2026-10-30", "0.01")]);
        await ClaimWithAsync(o, ol, "EUR", reserves: [("2026-10-08", "77.77")]);
        var r = await o.MonthAsync("2026-10");
        Assert.Equal(HalfUp(12346, "2.800000") + HalfUp(7777, "2.500000"), r.ClaimsTotals.RbnsGel);
        Assert.Equal("540.12", Shown(r.ClaimsTotals.RbnsGel));
    }

    // ---------------------------------------------------------------- IBNR

    private static BigInteger Ibnr(BigInteger gross, BigInteger ceded, int percent)
    {
        var net = gross > ceded ? gross - ceded : 0;
        return (net * percent * 2 + 100) / 200;
    }

    private static async Task<(Guid Cessions, Guid Corrections)> RiTreatyAsync(ReportWorld w) =>
        (await w.TreatyAsync("RI-IBNR", "quota_share", "2025-01-01", "2027-12-31", ["5", "13", "4"], cededPercent: 50m),
         await w.TreatyAsync("RI-IBNR-K", "facultative", "2025-01-01", "2027-12-31", [], cededPercent: 50m));

    /// <summary>A policy line with its ledger rows (a negative one cancels the row before) and their cessions (a negative one corrects the cession before).</summary>
    private static async Task PremiumsAsync(ReportWorld w, (Guid Cessions, Guid Corrections) treaty, string number, string classCode, (string Booked, string Amount, (string, string)[]? Ceded)[] rows)
    {
        var p = await w.PolicyAsync(number, "2025-12-01", "2027-12-01", [new LineSpec(classCode, 1m, 1m)], issueRows: false);
        Guid lastTx = default;
        foreach (var (booked, amount, ceded) in rows)
        {
            var cancel = amount.StartsWith('-');
            var tx = await w.TxAsync(p.Lines[0], cancel ? "cancellation" : "issue", M(amount), M(amount), booked, booked, "2027-12-01", cancel ? lastTx : null);
            Guid lastCession = default;
            foreach (var (on, gel) in ceded ?? [])
                lastCession = await w.CessionAsync(tx, gel.StartsWith('-') ? treaty.Corrections : treaty.Cessions, 50m, M(gel), M(gel), on, booked, "2027-12-01", cancels: gel.StartsWith('-') ? lastCession : null);
            if (!cancel) lastTx = tx;
        }
    }

    [Fact]
    public async Task IBNR_beyond_a_JS_number_five_percent_and_health_two_percent()
    {
        await using var w = await CreateAsync();
        var t = await RiTreatyAsync(w);
        await PremiumsAsync(w, t, "P-IBNR-A", "5.1", [("2026-10-01", "9999999999999999.89", null)]);
        await PremiumsAsync(w, t, "P-IBNR-H", "4", [("2026-10-02", "9999999999999999.74", null)]);
        BigInteger a = Ibnr(T("9999999999999999.89"), 0, 5), h = Ibnr(T("9999999999999999.74"), 0, 2);
        Assert.Equal(new[] { BigInteger.Parse("49999999999999999"), BigInteger.Parse("19999999999999999") }, new[] { a, h });
        var r = await w.MonthAsync("2026-10");
        Assert.Equal(["4", "5.1"], r.Claims.Select(c => c.ClassCode));
        Assert.Equal(new[] { h, a }, r.Claims.Select(c => c.IbnrGel));
        Assert.Equal(("199,999,999,999,999.99", "499,999,999,999,999.99"), (Shown(h), Shown(a)));
        Assert.Equal(a + h, r.ClaimsTotals.IbnrGel);
    }

    [Fact]
    public async Task IBNR_on_the_net_to_its_last_tetri_never_negative()
    {
        await using var w = await CreateAsync();
        var t = await RiTreatyAsync(w);
        await PremiumsAsync(w, t, "P-IBNR-N", "5.1", [("2026-10-01", "90071992547408.11", [("2026-10-01", "90071992547408.01")])]);
        await PremiumsAsync(w, t, "P-IBNR-F", "13", [("2026-10-01", "365.00", [("2026-10-01", "365.00")])]);
        // Ceded above written in the year: last year's row of 101.00 ceded this October, this year's 100.00 written.
        await PremiumsAsync(w, t, "P-IBNR-O", "4", [("2025-12-15", "101.00", [("2026-10-01", "101.00")]), ("2026-10-01", "100.00", null)]);
        var r = await w.MonthAsync("2026-10");
        Assert.Equal([("5.1", BigInteger.One)], r.Claims.Select(c => (c.ClassCode, c.IbnrGel)));
        Assert.Equal("0.01", Shown(r.ClaimsTotals.IbnrGel));
    }

    [Fact]
    public async Task IBNR_each_class_rounded_on_its_own_then_summed()
    {
        await using var w = await CreateAsync();
        var t = await RiTreatyAsync(w);
        await PremiumsAsync(w, t, "P-P", "5.1", [("2026-10-01", "0.10", null)]);
        await PremiumsAsync(w, t, "P-Q", "13", [("2026-10-01", "0.10", null)]);
        await PremiumsAsync(w, t, "P-R", "4", [("2026-10-01", "0.25", null)]);
        var r = await w.MonthAsync("2026-10");
        Assert.Equal(new BigInteger[] { 1, 1, 1 }, r.Claims.Select(c => c.IbnrGel));
        Assert.Equal(new BigInteger(3), r.ClaimsTotals.IbnrGel);
        await using var w2 = await CreateAsync();
        var t2 = await RiTreatyAsync(w2);
        await PremiumsAsync(w2, t2, "P-S", "5.1", [("2026-10-01", "365.00", null)]);
        await PremiumsAsync(w2, t2, "P-T", "4", [("2026-10-01", "365.00", null)]);
        Assert.Equal(new[] { "7.30", "18.25" }, (await w2.MonthAsync("2026-10")).Claims.Select(c => Shown(c.IbnrGel)));
    }

    [Fact]
    public async Task IBNR_takes_the_year_to_date_cancellations_and_cession_corrections_included()
    {
        await using var w = await CreateAsync();
        var t = await RiTreatyAsync(w);
        await PremiumsAsync(w, t, "P-Y", "5.1", [
            ("2025-12-15", "1000.00", null),
            ("2026-03-01", X, [("2026-03-01", "1000.00"), ("2026-06-01", "-100.00")]),
            ("2026-04-01", L, null),
            ("2026-09-01", "-20.00", null),
        ]);
        var gross = T(X) + T(L) - 2000;
        BigInteger ceded = 90000;
        var oct = Ibnr(gross, ceded, 5);
        Assert.Equal(BigInteger.Parse("50450359962732440"), oct);
        var october = await w.MonthAsync("2026-10");
        Assert.Equal(oct, october.ClaimsTotals.IbnrGel);
        Assert.Equal(new BigInteger(5000), (await w.MonthAsync("2025-12")).ClaimsTotals.IbnrGel);
        await PremiumsAsync(w, t, "P-Z", "5.1", [("2026-11-05", "1000.00", [("2026-11-05", "600.00")])]);
        Assert.Equal(october.ClaimsTotals, (await w.MonthAsync("2026-10")).ClaimsTotals);
        Assert.Equal(Ibnr(gross + 100000, ceded + 60000, 5), (await w.MonthAsync("2026-11")).ClaimsTotals.IbnrGel);
    }

    // ---------------------------------------------------------------- the reinsurer's shares

    private sealed record TreatySpec(string Kind, string Currency, decimal Share, decimal? Priority = null, decimal? Limit = null);

    private static async Task<(Guid Claim, List<Guid> Treaties)> SharedClaimAsync(ReportWorld w, string k, string classCode, string currency, TreatySpec[] treaties,
        (string, string, string)[] payments, (string, string)[] reserves)
    {
        var l = await LineAsync(w, $"P-RI-{k}", classCode, currency);
        var claim = await w.ClaimAsync(l.Policy, l.Line, currency, "2026-09-05", "2026-09-06");
        var ids = new List<Guid>();
        for (var i = 0; i < treaties.Length; i++)
        {
            var t = treaties[i];
            var id = await w.TreatyAsync($"RI-{k}-{i}", t.Kind, "2026-01-01", "2027-12-31", [classCode], t.Currency,
                cededPercent: t.Kind == "excess_of_loss" ? null : t.Share, retention: t.Kind == "surplus" ? 1m : null, lines: t.Kind == "surplus" ? 1 : null,
                priority: t.Priority, limit: t.Limit);
            ids.Add(id);
            await w.ClaimCessionAsync(claim.Id, id, t.Kind == "excess_of_loss" ? 0m : t.Share, t.Priority, t.Limit);
        }
        Guid last = default;
        foreach (var (on, amount, rate) in payments)
        {
            if (amount.StartsWith('-')) await w.ClaimPaymentReversalAsync(last, on);
            else last = await w.ClaimPaymentAsync(claim.Id, on, M(amount), M(rate));
        }
        foreach (var (on, change) in reserves) await w.ReserveAsync(claim.Id, on, M(change));
        return (claim.Id, ids);
    }

    private static readonly TreatySpec Quota100 = new("quota_share", "GEL", 100m);

    [Theory]
    [InlineData("365.00")]
    [InlineData(L)]
    [InlineData(X)]
    public async Task A_100_percent_quota_share_takes_the_paid_and_RBNS_to_the_tetri(string amount)
    {
        await using var w = await CreateAsync();
        await SharedClaimAsync(w, "S", "5.1", "GEL", [Quota100], [("2026-10-10", amount, "1.000000")], [("2026-10-05", amount)]);
        var r = await w.MonthAsync("2026-10");
        Assert.Equal((T(amount), T(amount)), (r.ClaimsTotals.ReinsurerPaidGel, r.ClaimsTotals.ReinsurerRbnsGel));
    }

    [Fact]
    public async Task Shares_of_two_classes_sum_beyond_one_rows_largest()
    {
        await using var w = await CreateAsync();
        await SharedClaimAsync(w, "A", "5.1", "GEL", [Quota100], [("2026-10-10", L, "1.000000")], [("2026-10-05", L)]);
        await SharedClaimAsync(w, "B", "5.1", "GEL", [Quota100], [("2026-10-11", X, "1.000000")], [("2026-10-06", X)]);
        await SharedClaimAsync(w, "C", "13", "GEL", [Quota100], [("2026-10-12", L, "1.000000")], [("2026-10-07", L)]);
        var r = await w.MonthAsync("2026-10");
        BigInteger c51 = T(L) + T(X), c13 = T(L);
        Assert.Equal((c51, c51), (r.Claims.Single(c => c.ClassCode == "5.1").ReinsurerPaidGel, r.Claims.Single(c => c.ClassCode == "5.1").ReinsurerRbnsGel));
        Assert.Equal((c51 + c13, c51 + c13), (r.ClaimsTotals.ReinsurerPaidGel, r.ClaimsTotals.ReinsurerRbnsGel));
    }

    [Fact]
    public async Task Proportional_shares_are_sliced_from_their_running_total()
    {
        await using var w = await CreateAsync();
        await SharedClaimAsync(w, "H", "5.1", "GEL", [new("quota_share", "GEL", 50m), new("facultative", "GEL", 50m)],
            [("2026-10-10", "0.01", "1.000000"), ("2026-11-10", "0.01", "1.000000")], [("2026-10-05", "0.01")]);
        var oct = (await w.MonthAsync("2026-10")).ClaimsTotals;
        Assert.Equal((BigInteger.One, BigInteger.One), (oct.ReinsurerPaidGel, oct.ReinsurerRbnsGel));
        var nov = (await w.MonthAsync("2026-11")).ClaimsTotals;
        Assert.Equal((BigInteger.One, BigInteger.One), (nov.ReinsurerPaidGel, nov.ReinsurerRbnsGel));
        await using var w2 = await CreateAsync();
        await SharedClaimAsync(w2, "T", "5.1", "GEL", [new("surplus", "GEL", 33.3333m), new("surplus", "GEL", 33.3333m), new("surplus", "GEL", 33.3333m)],
            [("2026-10-10", "1.00", "1.000000")], [("2026-10-05", "1.00")]);
        var t = (await w2.MonthAsync("2026-10")).ClaimsTotals;
        Assert.Equal((new BigInteger(100), new BigInteger(100)), (t.ReinsurerPaidGel, t.ReinsurerRbnsGel));
    }

    [Fact]
    public async Task USD_shares_follow_the_payments_own_rate_and_RBNS_the_month_end()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-10-31", 2.8m);
        await w.RateAsync("USD", "2026-11-30", 2.9m);
        await SharedClaimAsync(w, "U", "5.1", "USD", [new("quota_share", "USD", 30m)], [("2026-10-10", "100.00", "2.700000"), ("2026-11-10", "-100.00", "2.700000")], [("2026-10-05", "50.00")]);
        var oct = (await w.MonthAsync("2026-10")).ClaimsTotals;
        Assert.Equal(("81.00", "42.00"), (Shown(oct.ReinsurerPaidGel), Shown(oct.ReinsurerRbnsGel)));
        var nov = (await w.MonthAsync("2026-11")).ClaimsTotals;
        Assert.Equal((new BigInteger(-8100), new BigInteger(4350)), (nov.ReinsurerPaidGel, nov.ReinsurerRbnsGel));
        await using var e = await CreateAsync();
        await e.RateAsync("EUR", "2026-10-31", 2.5m);
        await SharedClaimAsync(e, "V", "5.1", "EUR", [new("quota_share", "EUR", 50m)], [("2026-10-10", "0.05", "2.500000")], []);
        var v = (await e.MonthAsync("2026-10")).ClaimsTotals;
        Assert.Equal((new BigInteger(8), BigInteger.Zero), (v.ReinsurerPaidGel, v.ReinsurerRbnsGel));
    }

    [Fact]
    public async Task Excess_of_loss_takes_above_the_priority_up_to_the_limit_at_the_month_end_rate()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("EUR", "2026-10-31", 3.123456m);
        await SharedClaimAsync(w, "X", "5.1", "EUR", [new("excess_of_loss", "EUR", 0m, 100m, 1000m)], [("2026-10-10", "600.00", "3.000000")], [("2026-10-05", "900.00")]);
        var r = (await w.MonthAsync("2026-10")).ClaimsTotals;
        Assert.Equal((new BigInteger(156173), new BigInteger(156173)), (r.ReinsurerPaidGel, r.ReinsurerRbnsGel));
        await using var y = await CreateAsync();
        await SharedClaimAsync(y, "Y", "5.1", "GEL", [new("excess_of_loss", "GEL", 0m, 0m)], [("2026-10-10", L, "1.000000")], []);
        var g = (await y.MonthAsync("2026-10")).ClaimsTotals;
        Assert.Equal((T(L), BigInteger.Zero), (g.ReinsurerPaidGel, g.ReinsurerRbnsGel));
    }

    [Fact]
    public async Task The_claims_own_terms_count_not_the_treatys_later_ones_and_ordinary_amounts_match_the_number_rule()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-10-31", 2.8m);
        var (claim, treaties) = await SharedClaimAsync(w, "P", "5.1", "USD", [new("quota_share", "USD", 30m), new("facultative", "USD", 20m)], [("2026-10-10", "123.45", "2.700000")], [("2026-10-05", "77.77")]);
        var october = await w.MonthAsync("2026-10");
        // The number rule: Math.round(paidGel × share / paid) and Math.round(share × rate), each share from the claim's slices.
        var shares = await w.App.ReadTxAsync(tx => ReinsuranceStore.ClaimSharesAsync(tx, claim, D("2026-10-31")));
        var oldPaid = shares.Sum(s => (long)Math.Floor(33332m * s.InPaid / 12345m + 0.5m));
        var oldRbns = shares.Sum(s => (long)Math.Floor(s.InReserve * 2.8m + 0.5m));
        Assert.Equal((new BigInteger(oldPaid), new BigInteger(oldRbns)), (october.ClaimsTotals.ReinsurerPaidGel, october.ClaimsTotals.ReinsurerRbnsGel));
        await using (var c = await w.Database.OpenAdminAsync())
            await c.ExecuteAsync("update core.ri_treaty set ceded_percent = 90 where id in @treaties", new { treaties });
        await w.ClaimPaymentAsync(claim, "2026-11-03", 1000m, 2.7m);
        Assert.Equal(october.ClaimsTotals, (await w.MonthAsync("2026-10")).ClaimsTotals);
    }
}
