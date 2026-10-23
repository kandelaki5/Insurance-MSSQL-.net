using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;
using static Insurance.Integration.Tests.PolicyPremiumWorld;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The premium report by policy over HTTP: the page and its Excel file (legacy accounting/policy-premium/page.tsx and xlsx/route.ts;
/// policy-premium-page.test.ts and policy-premium-xlsx.test.ts) over the accepted fictional ledger (PolicyPremiumWorld). Cells are read
/// under their own headings; the figures are the old tests', worked by hand. A read that fails is never a premium of nothing: the app
/// role is denied the table for one request (a real refusal by SQL Server), the request fails, and the next one is the healthy page.
/// </summary>
public sealed partial class PolicyPremiumPagesTests
{
    private static readonly string[] PageHeader =
    [
        "ხელშეკრულების ნომერი", "პოლისის ნომერი", "დამზღვევი", "ცვლილება", "სახეობის კოდი", "ვალუტა", "პრემია (ვალუტით)", "სტატისტიკური პრემია (ლარი)",
        "Correction (ლარი)", "მოზიდული პრემია / Corrected (ლარი)", "დაწყება", "ცვლილების თარიღი", "დასასრული", "გაუქმების თარიღი", "გამომუშავებული (ლარი)", "UPR პერიოდის ბოლოს (ლარი)",
        "გადაზღვევის პრემია (ლარი)", "გადაზღვევის გამომუშავებული პრემია (ლარი)", "გადაზღვევის გამოუმუშავებელი პრემია (ლარი)", "საკომისიო გამომუშავებული (ლარი)", "საკომისიო გამოუმუშავებელი (ლარი)",
    ];

    private static readonly string[] FileHeader = [.. PageHeader[..5], "სახეობა", .. PageHeader[5..]];

    [GeneratedRegex(@"<th\b[^>]*>([\s\S]*?)</th>")]
    private static partial Regex Th();

    private static string[] Header(string html) => Th().Matches(html).Select(m => WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]*>", "")).Trim()).ToArray();

    private static string Hint(string html) => WebUtility.HtmlDecode(Regex.Match(html, "<span class=\"hint\"[^>]*>([\\s\\S]*?)</span>").Groups[1].Value).Trim();

    private static Dictionary<string, string> ByHeading(string[] row) => PageHeader.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => row[x.i]);

    private static async Task<(ReportSite Site, HttpClient Client)> SiteAsync(Variant variant)
    {
        var site = await ReportSite.CreateAsync();
        await PolicyPremiumWorld.WriteAsync(site.World, variant);
        var (client, _) = await site.SignedInAsync("accountant");
        return (site, client);
    }

    private static string Url(string from, string to) => $"/accounting/policy-premium?from={from}&to={to}";
    private static string FileUrl(string from, string to) => $"/accounting/policy-premium/xlsx?from={from}&to={to}";

    [Fact]
    public async Task October_after_Novembers_bring_forward_is_the_same_page_and_November_shows_the_new_date()
    {
        var (site, client) = await SiteAsync(Variant.History);
        await using var owner = site;
        var october = await ReportSite.PageAsync(client, Url("2026-10-01", "2026-10-31"));
        Assert.Equal(PageHeader, Header(october));
        Assert.Equal(["C-T-002", "T-0002", "ტესტი შპს", "არა", "5.1", "GEL", "730.00", "730.00", "640.00", "-640.00", "01.09.2026", "01.09.2026", "01.09.2027", "16.10.2026", "30.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00"],
            ReportSite.Cells(october, "C-T-002"));
        Assert.Equal([["სულ", "2,555.00", "985.00", "495.00", "", "122.00", "1,378.00", "0.00", "0.00", "0.00", "0.00", "0.00"]], ReportSite.TotalRows(october));
        Assert.Contains("value=\"01/10/2026\"", october);
        Assert.Contains("<input type=\"hidden\" name=\"from\" value=\"2026-10-01\" />", october);
        Assert.Contains("<input type=\"hidden\" name=\"to\" value=\"2026-10-31\" />", october);
        Assert.Equal("01.10.2026 – 31.10.2026 · სტრიქონები: 6", Hint(october));
        Assert.Contains("href=\"/accounting/policy-premium/xlsx?from=2026-10-01&amp;to=2026-10-31\"", october);

        // The same October over the ledger before the bring-forward (the base world), cell for cell.
        var (baseSite, baseClient) = await SiteAsync(Variant.Base);
        await using (baseSite)
        {
            var before = await ReportSite.PageAsync(baseClient, Url("2026-10-01", "2026-10-31"));
            string Body(string html) => Regex.Match(html, "<tbody>[\\s\\S]*?</tbody>").Value;
            Assert.Equal(Body(before), Body(october));
        }

        var november = await ReportSite.PageAsync(client, Url("2026-11-01", "2026-11-30"));
        Assert.Equal(["C-T-002", "T-0002", "ტესტი შპს", "არა", "5.1", "GEL", "730.00", "0.00", "50.00", "-50.00", "01.09.2026", "01.09.2026", "01.09.2027", "21.09.2026", "-50.00", "0.00", "0.00", "0.00", "0.00", "0.00", "0.00"],
            ReportSite.Cells(november, "C-T-002"));
        Assert.Equal([["სულ", "1,825.00", "50.00", "315.00", "", "112.00", "1,581.00", "0.00", "0.00", "0.00", "0.00", "0.00"]], ReportSite.TotalRows(november));
        Assert.Equal("01.11.2026 – 30.11.2026 · სტრიქონები: 5", Hint(november));
    }

    private static string[] Row(string html, string policy) => Regex.Matches(html, "<tr[^>]*>([\\s\\S]*?)</tr>")
        .Select(r => Regex.Matches(r.Groups[1].Value, "<td[^>]*>([\\s\\S]*?)</td>").Select(c => WebUtility.HtmlDecode(Regex.Replace(c.Groups[1].Value, "<[^>]*>", "")).Trim()).ToArray())
        .Where(c => c.Length > 1 && c[1] == policy).ToList() is [var one] ? one : throw new InvalidOperationException(policy + ": " + string.Join(" / ", Regex.Matches(html, "<tr[^>]*>([\\s\\S]*?)</tr>").Select(r => r.Value).Where(v => v.Contains(policy))));

    private static IEnumerable<string[]> BodyRows(string html) => Regex.Matches(html, "<tr>([\\s\\S]*?)</tr>")
        .Select(r => Regex.Matches(r.Groups[1].Value, "<td[^>]*>([\\s\\S]*?)</td>").Select(c => WebUtility.HtmlDecode(Regex.Replace(c.Groups[1].Value, "<[^>]*>", "")).Trim()).ToArray())
        .Where(c => c.Length == PageHeader.Length);

    private static readonly string[] RiHeadings = PageHeader[16..];

    [Fact]
    public async Task The_change_and_reinsurance_columns_each_under_its_own_heading()
    {
        var (site, client) = await SiteAsync(Variant.ChangeAndReinsurance);
        await using var owner = site;
        var october = await ReportSite.PageAsync(client, Url("2026-10-01", "2026-10-31"));
        var a = ByHeading(Row(october, "T-0001"));
        var h = ByHeading(Row(october, "T-0008"));
        Assert.Equal(["არა", "01.10.2026", "01.10.2026", "109.50", "9.30", "100.20", "1.86", "20.04"],
            new[] { a["ცვლილება"], a["დაწყება"], a["ცვლილების თარიღი"] }.Concat(RiHeadings.Select(k => a[k])));
        Assert.Equal(["კი", "01.10.2026", "16.10.2026", "-35.00", "-35.00", "-1.60", "-33.40", "0.00", "0.00", "0.00", "0.00", "0.00"],
            new[] { h["ცვლილება"], h["დაწყება"], h["ცვლილების თარიღი"], h["სტატისტიკური პრემია (ლარი)"], h["მოზიდული პრემია / Corrected (ლარი)"], h["გამომუშავებული (ლარი)"], h["UPR პერიოდის ბოლოს (ლარი)"] }
                .Concat(RiHeadings.Select(k => h[k])));
        Assert.Equal([["სულ", "2,520.00", "985.00", "460.00", "", "120.40", "1,344.60", "109.50", "9.30", "100.20", "1.86", "20.04"]], ReportSite.TotalRows(october));
        var others = BodyRows(october).Where(r => r[1] is not ("T-0001" or "T-0008")).Select(ByHeading)
            .Select(x => string.Join("|", new[] { x["ცვლილება"], (x["ცვლილების თარიღი"] == x["დაწყება"]).ToString() }.Concat(RiHeadings.Select(k => x[k]))));
        Assert.Equal(Enumerable.Repeat("არა|True|0.00|0.00|0.00|0.00|0.00", 5), others);
        var november = await ReportSite.PageAsync(client, Url("2026-11-01", "2026-11-30"));
        var an = ByHeading(Row(november, "T-0001"));
        var hn = ByHeading(Row(november, "T-0008"));
        Assert.Equal(["0.00", "9.00", "91.20", "1.80", "18.24"], RiHeadings.Select(k => an[k]));
        Assert.Equal(["კი", "16.10.2026", "-3.00", "-30.40", "0.00", "0.00", "0.00", "0.00", "0.00"],
            new[] { hn["ცვლილება"], hn["ცვლილების თარიღი"], hn["გამომუშავებული (ლარი)"], hn["UPR პერიოდის ბოლოს (ლარი)"] }.Concat(RiHeadings.Select(k => hn[k])));
        var total = ReportSite.TotalRows(november).Single();
        Assert.Equal(["365.00", "159.00", "1,550.60", "0.00", "9.00", "91.20", "1.80", "18.24"], new[] { total[3] }.Concat(total[5..]));
    }

    private static async Task DenyAsync(ReportSite site, string table, bool deny)
    {
        await using var c = await site.World.Database.OpenAdminAsync();
        // The migrations grant select on these ledger tables to core_app (0007_grants.sql): a grant puts it back.
        await c.ExecuteAsync(deny ? $"deny select on core.{table} to core_app" : $"grant select on core.{table} to core_app");
    }

    [Fact]
    public async Task A_read_that_fails_fails_the_page_and_the_file_never_a_share_of_nothing_and_the_next_request_is_right()
    {
        var (site, client) = await SiteAsync(Variant.ChangeAndReinsurance);
        await using var owner = site;
        var healthyPage = await ReportSite.PageAsync(client, Url("2026-10-01", "2026-10-31"));
        var healthyFile = XlsxSheet.Rows(await (await client.GetAsync(FileUrl("2026-10-01", "2026-10-31"))).Content.ReadAsByteArrayAsync(), "NBG list");
        var before = await site.World.FingerprintAsync();
        foreach (var table in new[] { "premium_cession", "premium_transaction" })
        {
            await DenyAsync(site, table, true);
            var page = await client.GetAsync(Url("2026-10-01", "2026-10-31"));
            var file = await client.GetAsync(FileUrl("2026-10-01", "2026-10-31"));
            await DenyAsync(site, table, false);
            Assert.Equal(HttpStatusCode.InternalServerError, page.StatusCode);
            Assert.DoesNotContain("total-row", await page.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.InternalServerError, file.StatusCode);
            Assert.False(file.Content.Headers.Contains("Content-Disposition"));
            Assert.Equal(before, await site.World.FingerprintAsync());
            var again = await ReportSite.PageAsync(client, Url("2026-10-01", "2026-10-31"));
            Assert.Equal(ReportSite.TotalRows(healthyPage), ReportSite.TotalRows(again));
            Assert.Equal(["109.50", "9.30", "100.20", "1.86", "20.04"], RiHeadings.Select(k => ByHeading(Row(again, "T-0001"))[k]));
            var fileAgain = XlsxSheet.Rows(await (await client.GetAsync(FileUrl("2026-10-01", "2026-10-31"))).Content.ReadAsByteArrayAsync(), "NBG list");
            Assert.Equal(healthyFile.Select(r => string.Join("|", r)), fileAgain.Select(r => string.Join("|", r)));
        }
    }

    [Fact]
    public async Task The_file_October_after_the_bring_forward_and_November_with_its_name_type_and_cells()
    {
        var (site, client) = await SiteAsync(Variant.History);
        await using var owner = site;
        var response = await client.GetAsync(FileUrl("2026-10-01", "2026-10-31"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReportWorkbooks.XlsxType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment; filename=\"premium-by-policy-2026-10-01-2026-10-31.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        var sheet = XlsxSheet.Rows(await response.Content.ReadAsByteArrayAsync(), "NBG list");
        Assert.Equal("01.10.2026 – 31.10.2026", sheet[0][0]);
        Assert.Equal(FileHeader, sheet[2]);
        Assert.Equal(["C-T-002", "T-0002", "ტესტი შპს", "არა", "5.1", "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", "GEL", "730", "730", "640", "-640", "01.09.2026", "01.09.2026", "01.09.2027", "16.10.2026", "30", "0", "0", "0", "0", "0", "0"],
            sheet.Single(r => r[1] == "T-0002"));
        Assert.Equal([PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "2555", "985", "495", "", "", "", "", "122", "1378", "0", "0", "0", "0", "0"], sheet[^1]);
        var november = XlsxSheet.Rows(await (await client.GetAsync(FileUrl("2026-11-01", "2026-11-30"))).Content.ReadAsByteArrayAsync(), "NBG list");
        Assert.Equal("01.11.2026 – 30.11.2026", november[0][0]);
        Assert.Equal(["C-T-002", "T-0002", "ტესტი შპს", "არა", "5.1", "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", "GEL", "730", "0", "50", "-50", "01.09.2026", "01.09.2026", "01.09.2027", "21.09.2026", "-50", "0", "0", "0", "0", "0", "0"],
            november.Single(r => r[1] == "T-0002"));
        Assert.Equal([PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "1825", "50", "315", "", "", "", "", "112", "1581", "0", "0", "0", "0", "0"], november[^1]);
        Assert.Equal(5, november.Count - 4);
    }

    [Fact]
    public async Task Only_the_admin_and_the_accountant_a_wrong_or_reversed_period_is_refused_and_nothing_is_read_or_written()
    {
        var (site, client) = await SiteAsync(Variant.History);
        await using var owner = site;
        var (viewer, _) = await site.SignedInAsync("viewer");
        var (admin, _) = await site.SignedInAsync("admin");
        var before = await site.World.FingerprintAsync();
        var forbidden = await viewer.GetAsync(FileUrl("2026-10-01", "2026-10-31"));
        Assert.Equal((HttpStatusCode.Forbidden, "Forbidden"), (forbidden.StatusCode, await forbidden.Content.ReadAsStringAsync()));
        var page = await viewer.GetAsync(Url("2026-10-01", "2026-10-31"));
        Assert.Equal((HttpStatusCode.Redirect, "/"), (page.StatusCode, page.Headers.Location!.ToString()));
        foreach (var query in new[] { "from=2026-10-31&to=2026-10-01", "from=2026-13-01&to=2026-10-31", "from=2026-10-01" })
        {
            var r = await client.GetAsync($"/accounting/policy-premium/xlsx?{query}");
            Assert.Equal((HttpStatusCode.BadRequest, "Bad period"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
            Assert.False(r.Content.Headers.Contains("Content-Disposition"));
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(FileUrl("2026-10-01", "2026-10-31"))).StatusCode);
        var anonymous = await site.Client().GetAsync(FileUrl("2026-10-01", "2026-10-31"));
        Assert.StartsWith("/login", anonymous.Headers.Location!.ToString());
        Assert.Equal(before, await site.World.FingerprintAsync());
    }

    [Fact]
    public async Task A_USD_quota_share_with_a_20_percent_commission_keeps_the_stored_GEL_commission_by_days()
    {
        await using var site = await ReportSite.CreateAsync();
        var w = site.World;
        await w.RateAsync("USD", "2026-07-01", 4m);
        var holder = await w.PartyAsync("ტესტი USD");
        var p = await w.PolicyAsync("T-USD-1", "2026-07-01", "2027-07-01", [new LineSpec("5.1", 1.08m, 3.24m, 20000m, "USD")], currency: "USD", rate: 3m, holder: holder, contractNumber: "C-USD-1");
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], "USD", cededPercent: 30m, commissionPercent: 20m);
        await w.CedeAsync(p.IssueRows[0]);
        await using (var c = await w.Database.OpenAdminAsync())
            Assert.Equal((0.32m, 0.97m, 0.06m, 0.19m), await c.QuerySingleAsync<(decimal, decimal, decimal, decimal)>("select amount, amount_gel, commission, commission_gel from core.premium_cession"));
        var (client, _) = await site.SignedInAsync("accountant");
        var before = await w.FingerprintAsync();
        var response = await client.GetAsync(FileUrl("2026-07-01", "2026-07-31"));
        Assert.Equal("attachment; filename=\"premium-by-policy-2026-07-01-2026-07-31.xlsx\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        var sheet = XlsxSheet.Rows(await response.Content.ReadAsByteArrayAsync(), "NBG list");
        Assert.Equal(["01.07.2026 – 31.07.2026"], sheet[0][..1]);
        Assert.Equal(FileHeader, sheet[2]);
        Assert.Equal(["C-USD-1", "T-USD-1", "ტესტი USD", "არა", "5.1", "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", "USD", "1.08", "3.24", "0", "3.24", "01.07.2026", "01.07.2026", "01.07.2027", "", "0.28", "2.96", "0.97", "0.08", "0.89", "0.02", "0.17"],
            sheet.Single(r => r[1] == "T-USD-1"));
        Assert.Equal([PolicyPremiumTexts.Total, "", "", "", "", "", "", "", "3.24", "0", "3.24", "", "", "", "", "0.28", "2.96", "0.97", "0.08", "0.89", "0.02", "0.17"], sheet[^1]);
        // Read only: no write, no rate asked.
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Empty(site.Nbg.Requests);
    }

    [Fact]
    public async Task A_late_booked_commission_and_its_cancellation_April_and_July_unchanged_August_by_days()
    {
        await using var site = await ReportSite.CreateAsync();
        var w = site.World;
        var holder = await w.PartyAsync("ტესტი გვიანი");
        var p = await w.PolicyAsync("T-LATE-1", "2026-03-01", "2027-03-01", [new LineSpec("5.1", 1.02m, 1.02m)], holder: holder, contractNumber: "C-LATE-1", bookedOn: "2026-04-01");
        await w.TreatyAsync("RI030", "quota_share", "2026-01-01", "2027-12-31", ["5.1"], cededPercent: 30m, commissionPercent: 20m);
        await w.CedeAsync(p.IssueRows[0]);
        var (client, _) = await site.SignedInAsync("accountant");
        async Task<List<string[]>> FileAsync(string from, string to)
        {
            var before = await w.FingerprintAsync();
            var r = await client.GetAsync(FileUrl(from, to));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal($"attachment; filename=\"premium-by-policy-{from}-{to}.xlsx\"", r.Content.Headers.GetValues("Content-Disposition").Single());
            var sheet = XlsxSheet.Rows(await r.Content.ReadAsByteArrayAsync(), "NBG list");
            Assert.Equal(FileHeader, sheet[2]);
            Assert.Equal(before, await w.FingerprintAsync());
            return sheet;
        }
        static string[] Money(string[] r) => [r[10], r[15], r[16], .. r[17..]];
        var march = await FileAsync("2026-03-01", "2026-03-31");
        var april = await FileAsync("2026-04-01", "2026-04-30");
        var july = await FileAsync("2026-07-01", "2026-07-31");
        Assert.DoesNotContain(march, r => r[1] == "T-LATE-1");
        Assert.Equal(["C-LATE-1", "T-LATE-1", "ტესტი გვიანი", "არა", "5.1", "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", "GEL", "1.02", "1.02", "0", "1.02", "01.03.2026", "01.03.2026", "01.03.2027", "", "0.17", "0.85", "0.31", "0.05", "0.26", "0.01", "0.05"],
            april.Single(r => r[1] == "T-LATE-1"));
        Assert.Equal(["1.02", "0.17", "0.85", "0.31", "0.05", "0.26", "0.01", "0.05"], Money(april[^1]));
        Assert.Equal(["0", "0.09", "0.59", "0", "0.03", "0.18", "0.01", "0.03"], Money(july.Single(r => r[1] == "T-LATE-1")));
        Assert.Equal(["0", "0.09", "0.59", "0", "0.03", "0.18", "0.01", "0.03"], Money(july[^1]));
        // The gross cancellation (−0.68 booked 01.08 from 01.07), mirrored by the real writer.
        var cancel = await w.TxAsync(p.Lines[0], "cancellation", -0.68m, -0.68m, "2026-08-01", "2026-07-01", "2027-03-01", p.IssueRows[0]);
        await w.WriteAsync(async tx => { await ReinsuranceStore.CedeCancellationAsync(tx, cancel, w.Admin); return 0; });
        await using (var c = await w.Database.OpenAdminAsync())
            Assert.Equal([(0.31m, 0.06m, D("2026-04-01"), D("2026-03-01"), false), (-0.21m, -0.04m, D("2026-08-01"), D("2026-07-01"), true)],
                await c.QueryAsync<(decimal, decimal, DateOnly, DateOnly, bool)>("select amount_gel, commission_gel, booked_on, cover_from, cast(case when cancels_cession_id is null then 0 else 1 end as bit) from core.premium_cession order by created_at"));
        string Flat(List<string[]> s) => string.Join("\n", s.Select(r => string.Join("|", r)));
        Assert.Equal(Flat(april), Flat(await FileAsync("2026-04-01", "2026-04-30")));
        Assert.Equal(Flat(july), Flat(await FileAsync("2026-07-01", "2026-07-31")));
        var august = await FileAsync("2026-08-01", "2026-08-31");
        Assert.Equal(["-0.68", "-0.09", "0", "-0.21", "-0.03", "0", "-0.01", "0"], Money(august.Single(r => r[1] == "T-LATE-1")));
        Assert.Equal(["-0.68", "-0.09", "0", "-0.21", "-0.03", "0", "-0.01", "0"], Money(august[^1]));
    }
}
