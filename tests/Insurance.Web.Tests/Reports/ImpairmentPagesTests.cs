using System.Net;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The impairment page and its Excel file over HTTP (legacy accounting/impairment/page.tsx and xlsx/route.ts; impairment-xlsx-get.test.ts,
/// impairment-usd-reduction-surplus.test.ts and impairment-usd-eur-get-failure.test.ts through the route). The world is the USD broker's
/// of the old tests, paid in full on 30.06.2026 and then reduced by a change policy of −184.00 USD on 01.07.2026; the old tests' "today"
/// was a stand-in (31.01.2027), here it is the real day in Tbilisi, so the dates read are ones already past. Figures worked by hand there.
/// </summary>
public sealed class ImpairmentPagesTests
{
    private static async Task<ReportSite> WorldAsync()
    {
        var site = await ReportSite.CreateAsync();
        var w = site.World;
        await w.RateAsync("USD", "2026-06-30", 3m);
        await w.RateAsync("USD", "2026-07-01", 3.2m);
        var d = await ImpairmentWorld.DebtorsAsync(w, usdBroker: true);
        await w.PayAsync(d.Broker.Installments[0], "2026-06-30", 630m, 3.1m);
        await w.PolicyAsync("P-B-BROKER-1", "2026-07-01", "2027-01-01", [new LineSpec("5.1", -184m, -588.80m)], currency: "USD", rate: 3.2m, holder: d.Holder, replaces: d.Broker.Id);
        return site;
    }

    [Fact]
    public async Task The_page_and_the_file_of_01_07_2026_the_brokers_surplus_on_its_row_and_N1_4_untouched_by_it()
    {
        await using var site = await WorldAsync();
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await site.World.FingerprintAsync();
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/impairment?date=2026-07-01"));
        var k = ImpairmentTexts.Kinds;
        Assert.Equal(["P-B-BROKER", "ტესტაძე ნინო", k["broker"], "546.00 USD", "363.00", "730.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00"], ReportSite.Cells(html, "P-B-BROKER"));
        Assert.Equal(["P-A-CLIENT", "ტესტაძე ნინო", k["client"], "365.00 GEL", "182.00", "0.00", "182.00", "0.00", "0.00", "1.00", "90.00", "91.00", "9.30", "9.30"], ReportSite.Cells(html, "P-A-CLIENT"));
        Assert.Equal(["P-D-MIXED", "ტესტაძე ნინო", k["client"], "1,460.00 GEL", "728.00", "50.00", "678.00", "0.00", "0.00", "0.00", "314.00", "364.00", "31.40", "31.40"], ReportSite.Cells(html, "P-D-MIXED"));
        Assert.Equal([k["client"], "2", "1,775.00", "860.00", "40.70"], ReportSite.Cells(html, k["client"]));
        Assert.Equal([k["broker"], "1", "0.00", "0.00", "0.00"], ReportSite.Cells(html, k["broker"]));
        Assert.Equal([k["agent"], "1", "895.00", "346.00", "7.30"], ReportSite.Cells(html, k["agent"]));
        Assert.Equal([[k["all"], "4", "2,670.00", "1,206.00", "48.00"]], ReportSite.TotalRows(html));
        Assert.Contains($"{ImpairmentTexts.RatesUsed}: USD 3.2 (01.07.2026)", html);
        Assert.Contains("href=\"/accounting/impairment/xlsx?date=2026-07-01\"", html);
        Assert.DoesNotContain("P-B-BROKER-1", html);

        var file = await client.GetAsync("/accounting/impairment/xlsx?date=2026-07-01");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(ReportWorkbooks.XlsxType, file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment; filename=\"impairment-2026-07-01.xlsx\"", file.Content.Headers.GetValues("Content-Disposition").Single());
        var book = XlsxSheet.Read(await file.Content.ReadAsByteArrayAsync());
        Assert.Equal(["ფორმა", "N1-4"], book.SheetNames);
        Assert.Equal($"2|P-B-BROKER|2026-01-01|2027-01-01|{k["broker"]}||ტესტაძე ნინო|5.1|საცალო|||546|USD|0|0|2|180|181|363|730|-184|3.2|-588.8|0|0|0|0|0|0|0|0|0|0|0|0|0",
            string.Join("|", XlsxSheet.Rows(book, "ფორმა")[2]));
        Assert.Equal([$"{k["client"]}|2|1775|860|40.7", $"{k["broker"]}|1|0|0|0", $"{k["agent"]}|1|895|346|7.3", $"{k["all"]}|4|2670|1206|48"],
            XlsxSheet.Rows(book, "N1-4").Skip(1).Select(r => string.Join("|", r)));
        // 30.06.2026: the reduction not yet booked, the broker paid in full.
        var june = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/impairment?date=2026-06-30"));
        Assert.Equal(["P-B-BROKER", "ტესტაძე ნინო", k["broker"], "730.00 USD", "362.00", "730.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00"], ReportSite.Cells(june, "P-B-BROKER"));
        Assert.Equal([[k["all"], "4", "2,670.00", "1,198.00", "47.00"]], ReportSite.TotalRows(june));
        Assert.Equal(before, await site.World.FingerprintAsync());
    }

    [Fact]
    public async Task Only_the_admin_and_the_accountant_a_wrong_or_future_date_is_refused_and_the_page_falls_back_to_last_months_end()
    {
        await using var site = await WorldAsync();
        var (viewer, _) = await site.SignedInAsync("viewer");
        var (client, _) = await site.SignedInAsync("accountant");
        var (admin, _) = await site.SignedInAsync("admin");
        var before = await site.World.FingerprintAsync();
        var forbidden = await viewer.GetAsync("/accounting/impairment/xlsx?date=2026-07-01");
        Assert.Equal((HttpStatusCode.Forbidden, "Forbidden"), (forbidden.StatusCode, await forbidden.Content.ReadAsStringAsync()));
        var page = await viewer.GetAsync("/accounting/impairment");
        Assert.Equal((HttpStatusCode.Redirect, "/"), (page.StatusCode, page.Headers.Location!.ToString()));
        var today = Rates.TodayInTbilisi();
        foreach (var date in new[] { "2026-02-30", "", "01.07.2026", today.AddDays(1).ToString("yyyy-MM-dd") })
        {
            var r = await client.GetAsync($"/accounting/impairment/xlsx?date={date}");
            Assert.Equal((HttpStatusCode.BadRequest, "Bad date"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/accounting/impairment/xlsx?date={today:yyyy-MM-dd}")).StatusCode);
        var lastMonthEnd = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        foreach (var query in new[] { "", "?date=x", $"?date={today.AddDays(1):yyyy-MM-dd}" })
        {
            var html = await ReportSite.PageAsync(client, "/accounting/impairment" + query);
            Assert.Contains($"<input type=\"hidden\" name=\"date\" value=\"{lastMonthEnd:yyyy-MM-dd}\" />", html);
        }
        Assert.StartsWith("/login", (await site.Client().GetAsync("/accounting/impairment/xlsx?date=2026-07-01")).Headers.Location!.ToString());
        Assert.Equal(before, await site.World.FingerprintAsync());
    }

    [Fact]
    public async Task Without_a_rate_for_the_date_the_request_fails_and_gives_no_file_with_the_rate_stored_the_next_one_is_right()
    {
        await using var site = await ReportSite.CreateAsync();
        var w = site.World;
        await w.PolicyAsync("P-USD", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 50m, 135m)], [("2026-01-01", 50m, "client")], "USD", 2.7m);
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await w.FingerprintAsync();
        var failed = await client.GetAsync("/accounting/impairment/xlsx?date=2026-07-01");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(failed.Content.Headers.Contains("Content-Disposition"));
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/accounting/impairment?date=2026-07-01")).StatusCode);
        Assert.NotEmpty(site.Nbg.Requests);
        Assert.Equal(before, await w.FingerprintAsync());
        await w.RateAsync("USD", "2026-07-01", 3m);
        var file = await client.GetAsync("/accounting/impairment/xlsx?date=2026-07-01");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        var form = XlsxSheet.Rows(await file.Content.ReadAsByteArrayAsync(), "ფორმა");
        Assert.Equal(["P-USD", "50", "USD", "3", "150"], new[] { form[1][1], form[1][11], form[1][12], form[1][21], form[1][22] });
    }
}
