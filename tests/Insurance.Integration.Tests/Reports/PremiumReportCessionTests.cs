using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Finance;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.Reports.PremiumReportPrecisionTests;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The monthly report's ceded premium, ceded UPR and reinsurance commission (legacy precision-month-report-cession.test.ts) and its treaty
/// premium (…-treaty.test.ts), exactly, on a real database. The cession guard (no premium row ceded beyond itself) holds, so cessions the
/// old offline store put on one row are spread over rows of the same line: the report reads them per class, as before.
/// </summary>
public sealed class PremiumReportCessionTests
{
    private const string L = "90071992547408.01", X = "9999999999999999.99";
    private const string CoverTo = "2027-12-01";
    private static decimal M(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Ces(Guid Id, BigInteger Amount, string Booked, string From, Guid? Cancels);

    /// <summary>The oracle: each cession's UPR at a day by the ledger rule (a correction mirrors the cession it cancels).</summary>
    private static BigInteger Upr(IReadOnlyDictionary<Guid, Ces> all, IEnumerable<Ces> of, string asOf)
    {
        ExactPremiumTx Tx(Ces c) => new(c.Amount, D(c.Booked), D(c.From), D(CoverTo), c.Cancels is { } k ? Tx(all[k]) : null);
        BigInteger sum = 0;
        foreach (var c in of)
        {
            if (D(asOf) < D(c.Booked)) continue;
            sum += c.Amount - EarnedPremium.EarnedAsOfExact(Tx(c), D(asOf));
        }
        return sum;
    }

    /// <summary>A policy line of a class whose premium rows (one per cession, each of <paramref name="premium"/>) carry the cessions.</summary>
    private static async Task<List<Ces>> CededAsync(ReportWorld w, string k, string classCode, string booked, string premium,
        (string Key, string Booked, string From, string Amount, string Commission, string? Cancels)[] cessions, string[]? order = null)
    {
        var p = await w.PolicyAsync($"P-CES-{k}", booked, CoverTo, [new LineSpec(classCode, M(premium), M(premium))], issueRows: false);
        var treaty = await w.TreatyAsync($"RI-CES-{k}", "facultative", "2026-01-01", "2028-12-31", [], cededPercent: 50m);
        var rows = new Dictionary<string, Guid>();
        foreach (var c in cessions) rows[c.Key] = await w.TxAsync(p.Lines[0], "issue", M(premium), M(premium), booked, booked, CoverTo);
        var ids = new Dictionary<string, Guid>();
        var result = new List<Ces>();
        // created_at in the order asked (a correction may be read before the cession it cancels); written originals first.
        var position = (order ?? cessions.Select(c => c.Key).ToArray()).ToList();
        var first = w.At();
        foreach (var _ in cessions) w.At();
        foreach (var c in cessions)
        {
            ids[c.Key] = Guid.NewGuid();
            await InsertCessionAsync(w, ids[c.Key], rows[c.Key], treaty, c, c.Cancels is null ? null : ids[c.Cancels], first.AddSeconds(position.IndexOf(c.Key)));
        }
        foreach (var c in cessions) result.Add(new Ces(ids[c.Key], T(c.Amount), c.Booked, c.From, c.Cancels is null ? null : ids[c.Cancels]));
        return result;
    }

    private static Task InsertCessionAsync(ReportWorld w, Guid id, Guid row, Guid treaty, (string Key, string Booked, string From, string Amount, string Commission, string? Cancels) c, Guid? cancels, DateTimeOffset? at = null) =>
        w.ExecAsync("""
            insert core.premium_cession (id, premium_transaction_id, treaty_id, share_percent, amount, amount_gel, commission, commission_gel, booked_on, cover_from, cover_to,
                                         cancels_cession_id, created_at, created_by)
            values (@id, @row, @treaty, 50, @amount, @amount, @commission, @commission, @booked, @from, @to, @cancels, @at, @user)
            """, new { id, row, treaty, amount = M(c.Amount), commission = M(c.Commission), booked = D(c.Booked), from = D(c.From), to = D(CoverTo), cancels, at = w.At(at), user = w.Admin });

    private static (BigInteger Written, BigInteger Upr, BigInteger Commission) Ceded(PremiumReport.Report r, string code)
    {
        var c = r.Classes.Single(x => x.ClassCode == code);
        return (c.CededWrittenGel, c.CededUnearnedGel, c.RiCommissionGel);
    }

    [Fact]
    public async Task October_cessions_of_two_classes_to_the_tetri_and_the_UPR_per_row()
    {
        await using var w = await CreateAsync();
        var a = await CededAsync(w, "A", "5.1", "2026-10-01", X, [("l", "2026-10-01", "2026-10-01", L, L, null), ("x", "2026-10-05", "2026-10-01", X, X, null)]);
        var b = await CededAsync(w, "B", "13", "2026-10-01", "365.00", [("s", "2026-10-01", "2026-10-01", "365.00", "365.00", null)]);
        var all = a.Concat(b).ToDictionary(c => c.Id);
        BigInteger w51 = T(L) + T(X), u51 = Upr(all, a, "2026-10-31"), u13 = Upr(all, b, "2026-10-31");
        Assert.Equal((BigInteger.Parse("8351745787846517"), new BigInteger(33844)), (Upr(all, [a[0]], "2026-10-31"), u13));
        var r = await w.MonthAsync("2026-10");
        Assert.Equal((w51, u51, w51), Ceded(r, "5.1"));
        Assert.Equal((new BigInteger(36500), u13, new BigInteger(36500)), Ceded(r, "13"));
        Assert.Equal((w51 + 36500, u51 + u13, w51 + 36500), (r.Totals.CededWrittenGel, r.Totals.CededUnearnedGel, r.Totals.RiCommissionGel));
        Assert.Equal("338.44", Shown(u13));
    }

    [Theory]
    [InlineData(L)]
    [InlineData(X)]
    [InlineData("0.01")]
    public async Task One_cession_alone_over_its_cover(string amount)
    {
        await using var w = await CreateAsync();
        var c = await CededAsync(w, "S", "5.1", "2026-10-01", X, [("s", "2026-10-01", "2026-10-01", amount, amount, null)]);
        var all = c.ToDictionary(x => x.Id);
        foreach (var (month, end) in new[] { ("2026-10", "2026-10-31"), ("2027-03", "2027-03-31"), ("2027-11", "2027-11-30"), ("2027-12", "2027-12-31") })
        {
            var written = month == "2026-10" ? T(amount) : 0;
            Assert.Equal((written, Upr(all, c, end), written), Ceded(await w.MonthAsync(month), "5.1"));
        }
        Assert.Equal(BigInteger.Zero, Upr(all, c, "2027-12-31"));
    }

    [Fact]
    public async Task A_partial_correction_read_before_its_cession_is_linked_by_its_id_and_its_undoing_restores_November()
    {
        await using var w = await CreateAsync();
        var c = await CededAsync(w, "C", "5.1", "2026-10-01", "200.00", [
            ("c1", "2026-10-01", "2026-10-01", "100.00", "10.00", null),
            ("c2", "2026-10-15", "2026-10-15", "100.00", "10.00", null),
            ("k", "2026-11-15", "2026-11-15", "-60.00", "-6.00", "c2"),
        ], ["k", "c1", "c2"]);
        var all = c.ToDictionary(x => x.Id);
        var october = await w.MonthAsync("2026-10");
        Assert.Equal((new BigInteger(20000), Upr(all, c.Take(2), "2026-10-31"), new BigInteger(2000)), Ceded(october, "5.1"));
        var corrected = Upr(all, c, "2026-11-30");
        Assert.Equal((new BigInteger(-6000), corrected, new BigInteger(-600)), Ceded(await w.MonthAsync("2026-11"), "5.1"));
        // Linked to c1 instead, the UPR would differ: the link decides.
        var wrong = new Dictionary<Guid, Ces>(all) { [c[2].Id] = c[2] with { Cancels = c[0].Id } };
        Assert.NotEqual(corrected, Upr(wrong, [c[0], c[1], wrong[c[2].Id]], "2026-11-30"));
        // The correction undone on 20.11.
        var p = await w.App.ReadAsync(x => x.QuerySingleAsync<(Guid Row, Guid Treaty)>("select premium_transaction_id, treaty_id from core.premium_cession where id = @id", new { id = c[2].Id }));
        var otherTreaty = await w.TreatyAsync("RI-CES-UNDO", "facultative", "2026-01-01", "2028-12-31", [], cededPercent: 50m);
        var undo = new Ces(Guid.NewGuid(), 6000, "2026-11-20", "2026-11-15", c[2].Id);
        await InsertCessionAsync(w, undo.Id, p.Row, otherTreaty, ("u", "2026-11-20", "2026-11-15", "60.00", "6.00", null), c[2].Id);
        all[undo.Id] = undo;
        var nov = Ceded(await w.MonthAsync("2026-11"), "5.1");
        Assert.Equal((BigInteger.Zero, Upr(all, [.. c, undo], "2026-11-30"), BigInteger.Zero), nov);
        Assert.Equal(Upr(all, c.Take(2), "2026-11-30"), nov.Upr);
        Assert.Equal(Ceded(october, "5.1"), Ceded(await w.MonthAsync("2026-10"), "5.1"));
    }

    [Fact]
    public async Task The_commission_is_its_own_stored_text_booked_in_its_month()
    {
        await using var w = await CreateAsync();
        var c = await CededAsync(w, "M", "5.1", "2026-10-01", "1000.00", [
            ("m1", "2026-11-03", "2026-10-01", "500.00", "75.55", null),
            ("m2", "2026-11-20", "2026-11-20", "-0.01", "-0.03", "m1"),
        ]);
        var all = c.ToDictionary(x => x.Id);
        var nov = await w.MonthAsync("2026-11");
        Assert.Equal((new BigInteger(49999), Upr(all, c, "2026-11-30"), new BigInteger(7552)), Ceded(nov, "5.1"));
        var extra = await CededAsync(w, "N", "5.1", "2026-12-01", X, [("d1", "2026-12-02", "2026-12-01", X, X, null)]);
        Assert.Equal(Ceded(nov, "5.1"), Ceded(await w.MonthAsync("2026-11"), "5.1"));
        var all2 = c.Concat(extra).ToDictionary(x => x.Id);
        Assert.Equal((T(X), Upr(all2, [.. c, .. extra], "2026-12-31"), T(X)), Ceded(await w.MonthAsync("2026-12"), "5.1"));
    }

    // ---------------------------------------------------------------- treaty premium

    /// <summary>The oracle: the treaty premium earned by days over its period (both ends counted), the month's part the cumulative difference.</summary>
    private static BigInteger PartOf(BigInteger premium, string from, string to, string month)
    {
        var days = D(to).DayNumber - D(from).DayNumber + 1;
        BigInteger Cum(DateOnly day) => day < D(from) ? 0 : ReportMath.DivRound(premium * Math.Min(day.DayNumber - D(from).DayNumber + 1, days), days);
        var start = D(month + "-01");
        return Cum(Dates.AddMonths(start, 1).AddDays(-1)) - Cum(start.AddDays(-1));
    }

    private static BigInteger AtRate(BigInteger minor, string rate) => ReportMath.AtRateHalfUp(minor, M(rate));

    private static async Task TreatiesAsync(ReportWorld w, params (string Premium, string Currency, string From, string To)[] list)
    {
        foreach (var t in list)
            await w.TreatyAsync(w.Next("RX"), "excess_of_loss", t.From, t.To, ["13"], t.Currency, priority: 100m, limit: 1000m, treatyPremium: M(t.Premium));
    }

    [Theory]
    [InlineData("365.00", "GEL", "36500")]
    [InlineData(L, "GEL", "9007199254740801")]
    [InlineData(X, "GEL", "999999999999999999")]
    [InlineData(L, "USD", "25220157913274243")]
    public async Task A_treaty_of_one_month_gives_its_premium_to_the_tetri(string premium, string currency, string want)
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-10-31", 2.8m);
        await TreatiesAsync(w, (premium, currency, "2026-10-01", "2026-10-31"));
        var part = PartOf(T(premium), "2026-10-01", "2026-10-31", "2026-10");
        Assert.Equal(BigInteger.Parse(want), currency == "GEL" ? part : AtRate(part, "2.800000"));
        Assert.Equal(BigInteger.Parse(want), (await w.MonthAsync("2026-10")).TreatyPremiumGel);
    }

    [Fact]
    public async Task Two_treaties_sum_beyond_one_rows_largest()
    {
        await using var w = await CreateAsync();
        await TreatiesAsync(w, (L, "GEL", "2026-10-01", "2026-10-31"), (X, "GEL", "2026-10-01", "2026-10-31"));
        Assert.Equal(T(L) + T(X), (await w.MonthAsync("2026-10")).TreatyPremiumGel);
    }

    [Fact]
    public async Task Treaty_premium_is_cumulative_leap_years_and_years_across_and_outside_its_period_nothing()
    {
        await using var w = await CreateAsync();
        await TreatiesAsync(w, ("0.01", "GEL", "2026-10-31", "2026-11-01"));
        Assert.Equal((BigInteger.One, BigInteger.Zero), ((await w.MonthAsync("2026-10")).TreatyPremiumGel, (await w.MonthAsync("2026-11")).TreatyPremiumGel));
        await using var leap = await CreateAsync();
        await TreatiesAsync(leap, ("366.00", "GEL", "2028-01-01", "2028-12-31"));
        Assert.Equal(new BigInteger(2900), (await leap.MonthAsync("2028-02")).TreatyPremiumGel);
        BigInteger year = 0;
        for (var m = 1; m <= 12; m++) year += (await leap.MonthAsync($"2028-{m:D2}")).TreatyPremiumGel;
        Assert.Equal(new BigInteger(36600), year);
        await using var across = await CreateAsync();
        await TreatiesAsync(across, ("100.00", "GEL", "2026-12-15", "2027-01-14"), ("7.00", "GEL", "2027-01-01", "2027-02-01"));
        BigInteger dec = PartOf(10000, "2026-12-15", "2027-01-14", "2026-12"), jan = PartOf(10000, "2026-12-15", "2027-01-14", "2027-01");
        Assert.Equal(new BigInteger[] { 5484, 4516, 10000 }, new[] { dec, jan, dec + jan });
        Assert.Equal(dec, (await across.MonthAsync("2026-12")).TreatyPremiumGel);
        Assert.Equal(jan + PartOf(700, "2027-01-01", "2027-02-01", "2027-01"), (await across.MonthAsync("2027-01")).TreatyPremiumGel);
        Assert.Equal(PartOf(700, "2027-01-01", "2027-02-01", "2027-02"), (await across.MonthAsync("2027-02")).TreatyPremiumGel);
        Assert.Equal((BigInteger.Zero, BigInteger.Zero), ((await across.MonthAsync("2026-11")).TreatyPremiumGel, (await across.MonthAsync("2027-03")).TreatyPremiumGel));
    }

    [Fact]
    public async Task Ordinary_treaty_amounts_match_the_old_number_rule_at_the_stored_month_end_rate()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-10-31", 2.8m);
        await w.RateAsync("EUR", "2026-10-31", 3.123456m);
        await w.RateAsync("USD", "2026-11-30", 9.9m);
        (string Premium, string Currency, string From, string To)[] list =
            [("1200.00", "GEL", "2026-01-01", "2026-12-31"), ("3000.00", "USD", "2026-07-15", "2027-07-14"), ("77.77", "EUR", "2026-10-10", "2026-11-09")];
        await TreatiesAsync(w, list);
        var rate = new Dictionary<string, decimal> { ["GEL"] = 1m, ["USD"] = 2.8m, ["EUR"] = 3.123456m };
        // The old rule on numbers: treatyPremiumInPeriod, then Math.round(share × rate).
        var old = list.Sum(t => (long)Math.Floor(Reinsurance.TreatyPremiumInPeriod((long)(M(t.Premium) * 100), D(t.From), D(t.To), D("2026-10-01"), D("2026-10-31")) * rate[t.Currency] + 0.5m));
        Assert.Equal(new BigInteger(old), (await w.MonthAsync("2026-10")).TreatyPremiumGel);
    }
}
