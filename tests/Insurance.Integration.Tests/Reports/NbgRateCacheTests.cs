using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// A rate missing from the cache loaded from one successful National Bank answer (legacy nbg-rate-cache.test.ts, Codex
/// INS-AUTOGRAPH-NBG-SUCCESS-NORMALIZATION-CACHE-REPORT-OFFLINE-20261011): the real Rates keeps only USD and EUR with a positive rate and
/// quantity, stores the rate per unit to six places on the asked date, and reads it back; later reads and the real July report use the
/// cached rate without asking again. The bank is <see cref="AnsweringNbg"/> (offline, every request recorded); the cache is the real
/// core.exchange_rate on SQL Server. Worked by hand: USD 314.15 per 100 → 3.141500; EUR 3.5 per 1 → 3.500000; July's XL premium
/// 365.00 USD × 31 / 365 = 31.00 USD × 3.1415 = 97.39 GEL.
/// </summary>
public sealed class NbgRateCacheTests
{
    private const string Url = "https://nbg.gov.ge/gw/api/ct/monetarypolicy/currencies/ka/json/?currencies=USD&currencies=EUR&date=2026-07-31";

    private sealed record Cached(string Currency, DateOnly RateDate, decimal Rate);

    private static async Task<List<Cached>> CacheAsync(ReportWorld w)
    {
        await using var c = await w.Database.OpenAdminAsync();
        return (await c.QueryAsync<Cached>("select rtrim(currency) as Currency, rate_date as RateDate, rate as Rate from core.exchange_rate order by rate_date, currency")).ToList();
    }

    private static string Text(IEnumerable<Cached> rows) => string.Join("; ", rows.Select(r => $"{r.Currency} {r.RateDate:yyyy-MM-dd} {r.Rate:0.000000}"));

    /// <summary>The legacy world: a USD excess of loss over 2026 and two other USD dates cached; nothing for 31.07 in either currency.</summary>
    private static async Task<(ReportWorld W, Guid Treaty)> WorldAsync()
    {
        var w = await CreateAsync();
        var treaty = await w.TreatyAsync("RX401", "excess_of_loss", "2026-01-01", "2026-12-31", ["5"], "USD", priority: 100m, limit: 1000m, treatyPremium: 365m);
        await w.RateAsync("USD", "2026-07-01", 2m);
        await w.RateAsync("USD", "2026-08-01", 9m);
        return (w, treaty);
    }

    private const string Old = "USD 2026-07-01 2.000000; USD 2026-08-01 9.000000";
    private const string Stored = "USD 2026-07-01 2.000000; EUR 2026-07-31 3.500000; USD 2026-07-31 3.141500; USD 2026-08-01 9.000000";

    [Fact]
    public async Task The_first_read_of_USD_31_07_is_3_1415_after_one_request_with_the_endpoint_the_date_USD_and_EUR()
    {
        var (w, _) = await WorldAsync();
        await using var _w = w;
        var (rates, nbg) = AnsweringNbg.For(w.App);
        Assert.Equal(3.1415m, await rates.RateOnAsync("USD", D("2026-07-31")));
        Assert.Equal([new AnsweringNbg.Request("GET", Url)], nbg.Requests);
        var uri = new Uri(nbg.Requests[0].Url);
        Assert.Equal(("https://nbg.gov.ge/gw/api/ct/monetarypolicy/currencies/ka/json/", "?currencies=USD&currencies=EUR&date=2026-07-31"),
            (uri.GetLeftPart(UriPartial.Path), uri.Query));
    }

    [Fact]
    public async Task The_cache_write_is_exactly_USD_3_141500_and_EUR_3_500000_for_31_07_no_GBP_no_zero_rate_or_quantity()
    {
        var (w, _) = await WorldAsync();
        await using var _w = w;
        var (rates, _) = AnsweringNbg.For(w.App);
        await rates.RateOnAsync("USD", D("2026-07-31"));
        Assert.Equal(Stored, Text(await CacheAsync(w)));
        Assert.Equal("EUR 2026-07-31 3.500000; USD 2026-07-31 3.141500", Text((await CacheAsync(w)).Where(r => r.RateDate == D("2026-07-31"))));
    }

    [Fact]
    public async Task Read_again_USD_and_EUR_come_from_the_cache_alone_no_further_request_no_further_write()
    {
        var (w, _) = await WorldAsync();
        await using var _w = w;
        var (rates, nbg) = AnsweringNbg.For(w.App);
        await rates.RateOnAsync("USD", D("2026-07-31"));
        var fingerprint = await w.FingerprintAsync();
        // A later answer of the bank would be a different rate: none is asked for.
        nbg.Answer = _ => throw new InvalidOperationException("the cache must answer");
        Assert.Equal(3.1415m, await rates.RateOnAsync("USD", D("2026-07-31")));
        Assert.Equal(3.5m, await rates.RateOnAsync("EUR", D("2026-07-31")));
        Assert.Equal((3.1415m, D("2026-07-31")), await rates.LatestRateOnOrBeforeAsync("USD", D("2026-07-31")));
        Assert.Single(nbg.Requests);
        Assert.Equal(fingerprint, await w.FingerprintAsync());
        Assert.Equal(Stored, Text(await CacheAsync(w)));
    }

    [Fact]
    public async Task The_July_report_from_the_cache_31_00_USD_at_3_1415_is_97_39_GEL_nothing_else_no_request_no_write()
    {
        var (w, _) = await WorldAsync();
        await using var _w = w;
        var (rates, nbg) = AnsweringNbg.For(w.App);
        await rates.RateOnAsync("USD", D("2026-07-31"));
        var fingerprint = await w.FingerprintAsync();
        var july = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-07"));
        Assert.Equal(new BigInteger(9739), july.TreatyPremiumGel);
        Assert.Empty(july.Classes);
        Assert.Empty(july.Receivables);
        Assert.Empty(july.Claims);
        Assert.Equal(0, july.Totals.PoliciesIssued);
        Assert.Single(nbg.Requests);
        Assert.Equal(fingerprint, await w.FingerprintAsync());
    }

    [Fact]
    public async Task The_July_report_on_an_empty_cache_loads_the_rate_once_itself_and_values_at_it()
    {
        // The same chain started by the report: its month-end valuation is the first read.
        var (w, _) = await WorldAsync();
        await using var _w = w;
        var (rates, nbg) = AnsweringNbg.For(w.App);
        var july = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-07"));
        Assert.Equal(new BigInteger(9739), july.TreatyPremiumGel);
        Assert.Equal([new AnsweringNbg.Request("GET", Url)], nbg.Requests);
        Assert.Equal(Stored, Text(await CacheAsync(w)));
    }

    [Fact]
    public async Task The_earlier_cache_rows_and_the_treaty_are_unchanged()
    {
        var (w, treaty) = await WorldAsync();
        await using var _w = w;
        await using var c = await w.Database.OpenAdminAsync();
        const string treatySql = "select concat(number, '|', kind, '|', rtrim(currency), '|', priority, '|', treaty_limit, '|', treaty_premium, '|', status, '|', period_from, '|', period_to) from core.ri_treaty where id = @treaty";
        var before = await c.ExecuteScalarAsync<string>(treatySql, new { treaty });
        var (rates, _) = AnsweringNbg.For(w.App);
        await rates.RateOnAsync("USD", D("2026-07-31"));
        await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, rates, "2026-07"));
        Assert.Equal(Old, Text((await CacheAsync(w)).Where(r => r.RateDate != D("2026-07-31"))));
        Assert.Equal(before, await c.ExecuteScalarAsync<string>(treatySql, new { treaty }));
        Assert.Equal("RX401|excess_of_loss|USD|100.00|1000.00|365.00|active|2026-01-01|2026-12-31", before);
    }

    [Fact]
    public async Task Loading_a_stored_currency_and_date_again_replaces_its_rate_and_adds_the_missing_one()
    {
        // The legacy model's upsert (on conflict do update), here on the real table: USD 01.07 was 2.000000.
        var (w, _) = await WorldAsync();
        await using var _w = w;
        var (rates, nbg) = AnsweringNbg.For(w.App);
        Assert.True(await rates.LoadNbgRatesAsync(D("2026-07-01")));
        Assert.EndsWith("date=2026-07-01", Assert.Single(nbg.Requests).Url);
        Assert.Equal("EUR 2026-07-01 3.500000; USD 2026-07-01 3.141500; USD 2026-08-01 9.000000", Text(await CacheAsync(w)));
        // The direct store replaces too (legacy model case: USD 01.07 2.500000).
        await rates.StoreAsync(D("2026-07-01"), [("USD", 2.5m)]);
        Assert.Equal("EUR 2026-07-01 3.500000; USD 2026-07-01 2.500000; USD 2026-08-01 9.000000", Text(await CacheAsync(w)));
    }

    [Fact]
    public async Task An_answer_with_only_decoys_or_no_currencies_stores_nothing_and_the_read_stays_empty()
    {
        var (w, _) = await WorldAsync();
        await using var _w = w;
        var (rates, nbg) = AnsweringNbg.For(w.App);
        foreach (var body in new[]
                 {
                     """[{"currencies":[{"code":"GBP","quantity":1,"rate":7},{"code":"USD","quantity":1,"rate":0},{"code":"EUR","quantity":0,"rate":100}]}]""",
                     AnsweringNbg.NoRates, "[]", "[{}]",
                 })
        {
            nbg.Answer = _ => AnsweringNbg.Json(body);
            Assert.False(await rates.LoadNbgRatesAsync(D("2026-07-31")), body);
            Assert.Null(await rates.RateOnAsync("USD", D("2026-07-31")));
        }
        Assert.Equal(Old, Text(await CacheAsync(w)));
        Assert.Equal(8, nbg.Requests.Count);
    }

    [Fact]
    public async Task A_failing_bank_an_error_status_a_broken_answer_or_no_connection_gives_false_and_stores_nothing()
    {
        var (w, _) = await WorldAsync();
        await using var _w = w;
        var (rates, nbg) = AnsweringNbg.For(w.App);
        var answers = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => AnsweringNbg.Json(AnsweringNbg.Payload, System.Net.HttpStatusCode.InternalServerError),
            _ => AnsweringNbg.Json(AnsweringNbg.Payload, System.Net.HttpStatusCode.NotFound),
            _ => AnsweringNbg.Json("<html>maintenance</html>"),
            _ => AnsweringNbg.Json("""{"currencies":[]}"""),
            _ => throw new HttpRequestException("no connection"),
            _ => throw new TaskCanceledException("timed out"),
        };
        foreach (var answer in answers)
        {
            nbg.Answer = answer;
            Assert.False(await rates.LoadNbgRatesAsync(D("2026-07-31")));
        }
        Assert.Equal(answers.Length, nbg.Requests.Count);
        Assert.Equal(Old, Text(await CacheAsync(w)));
    }
}
