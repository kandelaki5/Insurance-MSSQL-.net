using System.Net;
using System.Text.RegularExpressions;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The monthly premium report page over HTTP (legacy accounting/premium-report/page.tsx; premium-report-page.test.ts and
/// premium-report-page-joint.test.ts). The old tests' November: a USD debt of 1,400.00 with 200.00 overdue valued at 30.11.2026's
/// 3.5000 = 4,900.00 GEL, each figure in its own table and column; here from real rows — a USD policy of 1,600.00 at 2.7000 (4,320.00
/// GEL) from 01.10.2026, its schedule 200 / 200 / 600 / 600, 200.00 USD (600.00 GEL at 3.0000) received on 31.10.2026 and allocated on
/// 07.11.2026. The old joint test wrote its rows with the old import, approval and receipt actions on a stand-in "today"; the new
/// actions read the real clock (Rates.TodayInTbilisi), so the same rows are written here directly (NOT the actions — see the report).
/// </summary>
public sealed class PremiumReportPageTests
{
    private static readonly string[] PremiumHeader = ["კოდი", "სახეობა", "ძალაში შესული პოლისები", "სტატისტიკური მოზიდული", "ფინანსური მოზიდული", "გამომუშავებული", "UPR თვის ბოლოს", "გადაზღვევის პრემია", "გადამზღვევლის წილი UPR-ში", "გადაზღვევის საკომისიო"];
    private static readonly string[] ClaimsHeader = ["კოდი", "სახეობა", "ანაზღაურებული", "RBNS თვის ბოლოს", "IBNR", "რეგრესი და ნარჩენები", "გადამზღვევლის წილი ანაზღაურებულში", "გადამზღვევლის წილი RBNS-ში"];
    private static readonly string[] ReceivablesHeader = ["ვალუტა", "დავალიანება", "მათ შორის ვადაგადაცილებული", "კურსი", "ლარში"];
    private const string Casco = "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)";

    private sealed record Table(string[] Header, List<string[]> Rows);

    private static string Text(string cell) => WebUtility.HtmlDecode(Regex.Replace(cell, "<[^>]*>", "")).Trim();

    private static List<Table> Tables(string html) => Regex.Matches(html, @"<table\b[^>]*>([\s\S]*?)</table>").Select(t => new Table(
        Regex.Matches(Regex.Match(t.Groups[1].Value, @"<thead>([\s\S]*?)</thead>").Groups[1].Value, @"<th\b[^>]*>([\s\S]*?)</th>").Select(c => Text(c.Groups[1].Value)).ToArray(),
        Regex.Matches(Regex.Match(t.Groups[1].Value, @"<tbody>([\s\S]*?)</tbody>").Groups[1].Value, @"<tr\b[^>]*>([\s\S]*?)</tr>")
            .Select(r => Regex.Matches(r.Groups[1].Value, @"<td\b[^>]*>([\s\S]*?)</td>").Select(c => Text(c.Groups[1].Value)).ToArray()).ToList())).ToList();

    private static string[] Headings(string html) => Regex.Matches(html, "<h2 class=\"section\">([\\s\\S]*?)</h2>").Select(m => Text(m.Groups[1].Value)).ToArray();

    private static async Task<ReportSite> SiteAsync()
    {
        var site = await ReportSite.CreateAsync();
        var w = site.World;
        await w.RateAsync("USD", "2026-10-01", 2.7m);
        await w.RateAsync("USD", "2026-10-31", 3m);
        await w.RateAsync("USD", "2026-11-30", 3.5m);
        var p = await w.PolicyAsync("P-USD-1600", "2026-10-01", "2027-10-01", [new LineSpec("5.1", 1600m, 4320m, 30000m, "USD")],
            [("2026-10-01", 200m, "client"), ("2026-11-01", 200m, "client"), ("2027-01-01", 600m, "client"), ("2027-04-01", 600m, "client")], "USD", 2.7m);
        var receipt = await w.ReceiptAsync("2026-10-31", 600m, p.PartyId);
        await w.AllocateAsync(receipt, p.Installments[0], 200m, 600m, 3m, "2026-11-07");
        return site;
    }

    [Fact]
    public async Task November_owed_1400_USD_200_overdue_at_3_5_is_4900_GEL_each_figure_in_its_own_table_and_column()
    {
        await using var site = await SiteAsync();
        var (client, _) = await site.SignedInAsync("viewer");
        var before = await site.World.FingerprintAsync();
        var html = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-11");
        var tables = Tables(html);
        Assert.Equal(3, tables.Count);
        var report = await site.World.MonthAsync("2026-11");
        var ibnr = PremiumReport.ReportMoney(report.ClaimsTotals.IbnrGel);
        Assert.NotEqual("0.00", ibnr);
        // November by days (365): earned 4,320.00 × 61/365 − 4,320.00 × 31/365 = 721.97 − 366.90 = 355.07; UPR 4,320.00 − 721.97 = 3,598.03.
        Assert.Equal(new Table(PremiumHeader, [["5.1", Casco, "0", "0.00", "0.00", "355.07", "3,598.03", "0.00", "0.00", "0.00"], ["სულ", "0", "0.00", "0.00", "355.07", "3,598.03", "0.00", "0.00", "0.00"]]), tables[0], new TableComparer());
        Assert.Equal(new Table(ClaimsHeader, [["5.1", Casco, "0.00", "0.00", ibnr, "0.00", "0.00", "0.00"], ["სულ", "0.00", "0.00", ibnr, "0.00", "0.00", "0.00"]]), tables[1], new TableComparer());
        Assert.Equal(new Table(ReceivablesHeader, [["USD", "1,400.00", "200.00", "3.5000 (30.11.2026)", "4,900.00"]]), tables[2], new TableComparer());
        Assert.Contains("id=\"month\" name=\"month\" type=\"month\" value=\"2026-11\"", html);
        Assert.Contains("01.11.2026 – 30.11.2026", html);
        Assert.Equal(["ზარალები", "პრემიის დავალიანება თვის ბოლოს (30.11.2026)"], Headings(ReportSite.Decode(html)));
        Assert.Equal(before, await site.World.FingerprintAsync());

        // October: written 4,320.00 (one policy issued); 1,600.00 owed, nothing allocated by 31.10, 200.00 overdue (due 01.10) at 3.0000.
        var october = Tables(await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-10"));
        Assert.Equal(["5.1", Casco, "1", "4,320.00", "4,320.00", "366.90", "3,953.10", "0.00", "0.00", "0.00"], october[0].Rows[0]);
        Assert.Equal(["USD", "1,600.00", "200.00", "3.0000 (31.10.2026)", "4,800.00"], october[2].Rows[0]);
    }

    [Fact]
    public async Task An_empty_month_shows_the_pages_own_empty_rows_and_nothing_of_November_anonymous_goes_to_the_login()
    {
        await using var site = await SiteAsync();
        var (client, _) = await site.SignedInAsync("accountant");
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-09"));
        var tables = Tables(html);
        Assert.Single(tables);
        Assert.Equal(PremiumHeader, tables[0].Header);
        Assert.Equal([[Ka.Common.NoData]], tables[0].Rows);
        Assert.Equal([Ka.Common.NoData, Ka.Common.NoData], Regex.Matches(html, "<div class=\"card empty\">([\\s\\S]*?)</div>").Select(m => Text(m.Groups[1].Value)));
        Assert.Contains("01.09.2026 – 30.09.2026", html);
        Assert.Equal(["ზარალები", "პრემიის დავალიანება თვის ბოლოს (30.09.2026)"], Headings(html));
        foreach (var november in new[] { "USD", "3,598.03", "355.07", "4,900.00", "30.11.2026" }) Assert.DoesNotContain(november, html);
        // A month that is not one: this month in Tbilisi.
        var today = Rates.TodayInTbilisi();
        Assert.Contains($"value=\"{today:yyyy-MM}\"", await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-13"));
        var anonymous = await site.Client().GetAsync("/accounting/premium-report?month=2026-11");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/login", anonymous.Headers.Location!.ToString());
    }

    [Fact]
    public async Task When_the_ledger_cannot_be_read_the_page_fails_writes_nothing_and_the_next_request_shows_November()
    {
        await using var site = await SiteAsync();
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await site.World.FingerprintAsync();
        await using (var c = await site.World.Database.OpenAdminAsync())
            await Dapper.SqlMapper.ExecuteAsync(c, "deny select on core.premium_transaction to core_app");
        var failed = await client.GetAsync("/accounting/premium-report?month=2026-11");
        await using (var c = await site.World.Database.OpenAdminAsync())
            await Dapper.SqlMapper.ExecuteAsync(c, "grant select on core.premium_transaction to core_app");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("4,900.00", await failed.Content.ReadAsStringAsync());
        Assert.Equal(before, await site.World.FingerprintAsync());
        var again = Tables(await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-11"));
        Assert.Equal(["USD", "1,400.00", "200.00", "3.5000 (30.11.2026)", "4,900.00"], again[2].Rows[0]);
        Assert.Equal("3,598.03", again[0].Rows[0][6]);
    }

    private sealed class TableComparer : IEqualityComparer<Table>
    {
        public bool Equals(Table? x, Table? y) => x is not null && y is not null && x.Header.SequenceEqual(y.Header)
            && x.Rows.Count == y.Rows.Count && x.Rows.Zip(y.Rows).All(p => p.First.SequenceEqual(p.Second));
        public int GetHashCode(Table obj) => 0;
    }
}
