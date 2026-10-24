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

    private static string[][] Ri(Table t) => [.. t.Rows.Select(r => new[] { r[0] }.Concat(r[^3..]).ToArray())];

    [Fact]
    public async Task A_USD_quota_share_shows_the_commission_stored_in_GEL_and_the_reinsurers_UPR_on_the_gross_cession()
    {
        // legacy usd-ri-commission-page.test.ts: 1.08 USD at 3.0000 = 3.24 GEL from 01.07.2026, quota 30 % with 20 %; ceded by the writer.
        await using var site = await ReportSite.CreateAsync();
        var w = site.World;
        await w.RateAsync("USD", "2026-07-01", 4m);
        var p = await w.PolicyAsync("T-USD-1", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 1.08m, 3.24m, 20000m, "USD")], currency: "USD", rate: 3m);
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], "USD", cededPercent: 30m, commissionPercent: 20m);
        await w.CedeAsync(p.IssueRows[0]);
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await w.FingerprintAsync();
        var html = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-07");
        var table = Tables(html)[0];
        Assert.Equal(new Table(PremiumHeader, [["5.1", Casco, "1", "3.24", "3.24", "0.28", "2.96", "0.97", "0.89", "0.19"], ["სულ", "1", "3.24", "3.24", "0.28", "2.96", "0.97", "0.89", "0.19"]]), table, new TableComparer());
        Assert.Contains("01.07.2026 – 31.07.2026", html);
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task A_late_booked_commission_falls_in_the_month_it_is_booked_with_its_sign_and_earlier_months_stay()
    {
        // legacy late-commission-page.test.ts: 1.02 GEL booked 01.04.2026 for a cover from 01.03.2026; the cancellation from 01.07 booked 01.08.
        await using var site = await ReportSite.CreateAsync();
        var w = site.World;
        var p = await w.PolicyAsync("T-LATE-1", "2026-03-01", "2027-03-01", [new LineSpec("5.1", 1.02m, 1.02m)], bookedOn: "2026-04-01");
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 30m, commissionPercent: 20m);
        await w.CedeAsync(p.IssueRows[0]);
        var (client, _) = await site.SignedInAsync("accountant");
        async Task<Table> PageAsync(string month) => Tables(await ReportSite.PageAsync(client, $"/accounting/premium-report?month={month}"))[0];
        var march = await PageAsync("2026-03");
        var april = await PageAsync("2026-04");
        var july = await PageAsync("2026-07");
        Assert.Equal([[Ka.Common.NoData]], march.Rows);
        Assert.Equal([["5.1", "0.31", "0.26", "0.06"], ["სულ", "0.31", "0.26", "0.06"]], Ri(april));
        Assert.Equal([["5.1", "0.00", "0.18", "0.00"], ["სულ", "0.00", "0.18", "0.00"]], Ri(july));
        var cancel = await w.TxAsync(p.Lines[0], "cancellation", -0.68m, -0.68m, "2026-08-01", "2026-07-01", "2027-03-01", p.IssueRows[0]);
        await w.WriteAsync(async tx => { await ReinsuranceStore.CedeCancellationAsync(tx, cancel, w.Admin); return 0; });
        var august = await PageAsync("2026-08");
        Assert.Equal([["5.1", "-0.21", "0.00", "-0.04"], ["სულ", "-0.21", "0.00", "-0.04"]], Ri(august));
        Assert.Equal(["-0.68", "-0.09", "0.00"], august.Rows[0][4..7]);
        Assert.Equal(["0.00", "0.09", "0.59"], july.Rows[0][4..7]);
        Assert.Equal(april, await PageAsync("2026-04"), new TableComparer());
        Assert.Equal(july, await PageAsync("2026-07"), new TableComparer());
    }

    [Fact]
    public async Task The_page_over_a_thousand_policies_for_a_signed_in_user_of_any_role()
    {
        // legacy report-1000-page-session.test.ts: the page requires a signed-in user and grants nothing more; the figures are the report's.
        await using var site = await ReportSite.CreateAsync(() => ThousandWorld.CreateAsync());
        var (client, _) = await site.SignedInAsync("sales");
        var tables = Tables(await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-11"));
        Assert.Equal([["5.1", Casco, "1000", "400,000.00", "400,000.00", "32,889.86", "367,219.95", "0.00", "0.00", "0.00"], ["სულ", "1000", "400,000.00", "400,000.00", "32,889.86", "367,219.95", "0.00", "0.00", "0.00"]], tables[0].Rows);
        Assert.Equal([["GEL", "300,000.00", "0.00", "", "300,000.00"]], tables[2].Rows);
        var october = Tables(await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-10"));
        Assert.Equal(["5.1", Casco, "1", "120.00", "120.00", "10.19", "109.81", "0.00", "0.00", "0.00"], october[0].Rows[0]);
        var forged = site.Client();
        forged.DefaultRequestHeaders.Add("Cookie", $"{Insurance.Application.Services.Auth.Cookie}={new string('x', 43)}");
        var refused = await forged.GetAsync("/accounting/premium-report?month=2026-11");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.StartsWith("/login", refused.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_recovery_written_by_the_real_action_is_on_the_page_in_the_month_received_once()
    {
        // legacy claim-recovery-monthly-report.test.ts and claim-recovery-report-page.test.ts: one GEL CASCO claim, reserve 1000.00 (02.10.2026),
        // payment 900.00 (06.10.2026), a regress of 7.50 received 30.09.2026; the admin records a salvage of 50.00 received 31.10.2026.
        await using var site = await ReportSite.CreateAsync();
        var w = site.World;
        var p = await w.PolicyAsync("P-REC", "2026-09-01", "2027-09-01", [new LineSpec("5.1", 0m, 0m)], issueRows: false);
        var claim = await w.ClaimAsync(p.Id, p.Lines[0], "GEL", "2026-09-20", "2026-09-25");
        await w.ReserveAsync(claim.Id, "2026-10-02", 1000m);
        await w.ClaimPaymentAsync(claim.Id, "2026-10-06", 900m);
        await w.RecoveryAsync(claim.Id, "subrogation", "2026-09-30", 7.50m);
        var admin = new CurrentUser(w.Admin, "test.admin", "სატესტო ადმინი", "admin", "ადმინისტრატორი");
        Assert.Null(await ClaimActions.AddRecoveryAsync(w.App, admin, new PostedForm([new("claim_id", claim.Id.ToString()), new("kind", "salvage"), new("received_on", "2026-10-31"),
            new("amount_gel", "50.00"), new("note", "ოქტომბრის გადარჩენილი ქონება")])));
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await w.FingerprintAsync();
        string[] Row(string paid, string rbns, string recovered) => ["5.1", Casco, paid, rbns, "0.00", recovered, "0.00", "0.00"];
        string[] Total(string paid, string rbns, string recovered) => ["სულ", paid, rbns, "0.00", recovered, "0.00", "0.00"];
        foreach (var (month, paid, rbns, recovered) in new[] { ("2026-09", "0.00", "0.00", "7.50"), ("2026-10", "900.00", "1,000.00", "50.00"), ("2026-11", "0.00", "1,000.00", "0.00") })
        {
            var tables = Tables(await ReportSite.PageAsync(client, $"/accounting/premium-report?month={month}"));
            Assert.Equal([[Ka.Common.NoData]], tables[0].Rows);
            Assert.Equal([Row(paid, rbns, recovered), Total(paid, rbns, recovered)], tables[1].Rows);
            Assert.Equal(2, tables.Count);
        }
        Assert.Equal(before, await w.FingerprintAsync());
    }

    private sealed class TableComparer : IEqualityComparer<Table>
    {
        public bool Equals(Table? x, Table? y) => x is not null && y is not null && x.Header.SequenceEqual(y.Header)
            && x.Rows.Count == y.Rows.Count && x.Rows.Zip(y.Rows).All(p => p.First.SequenceEqual(p.Second));
        public int GetHashCode(Table obj) => 0;
    }
}
