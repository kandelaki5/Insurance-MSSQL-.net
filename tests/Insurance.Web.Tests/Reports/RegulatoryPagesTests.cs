using System.Net;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The supervisory forms page and its three Excel downloads over HTTP (legacy accounting/regulatory/page.tsx, reserves/xlsx, form1/xlsx and
/// form6/xlsx routes; regulatory-page.test.ts, form1-broker-page.test.ts, form6-fixes.test.ts). The old tests stood in for the reports; here
/// the figures come from real fictional rows: a class-13 policy booked 31.10.2026 for a cover from 01.11.2026 (its whole premium the UPR of
/// October) and an older class-13 policy fully earned and unpaid (October's N1-4 gross receivable). A figure Excel would change (2⁵³ tetri
/// and a tenth) withholds the download and names the cell; the files are read back as cells, never compared as bytes.
/// </summary>
public sealed class RegulatoryPagesTests
{
    private const string Sheet6 = "გადამზღვევლები და ბროკერები";

    private static async Task<ReportSite> SiteAsync(decimal upr = 1234.10m, decimal gross = 365.10m)
    {
        var site = await ReportSite.CreateAsync();
        var w = site.World;
        await w.PolicyAsync("P-UPR", "2026-11-01", "2027-11-01", [new LineSpec("13", upr, upr)], bookedOn: "2026-10-31");
        await w.PolicyAsync("P-OWED", "2025-10-01", "2026-10-01", [new LineSpec("13", gross, gross)], [("2025-10-01", gross, "client")]);
        await using var c = await w.Database.OpenAdminAsync();
        await c.ExecuteAsync("update core.setting set value = N'სატესტო კომპანია (გამოგონილი)' where [key] = N'company_name'");
        return site;
    }

    private static async Task<Guid> TreatyOn13Async(ReportWorld w, string reinsurer, string number = "RI-0001", Guid? broker = null)
    {
        var party = await w.PartyAsync(reinsurer, foreignId: "DE-" + number, country: "DE");
        return await w.TreatyAsync(number, "quota_share", "2026-01-01", "2026-12-31", ["13"], cededPercent: 20m, reinsurer: party, rating: "A", agency: "AM Best", broker: broker);
    }

    [Fact]
    public async Task The_admin_and_the_accountant_see_the_exact_figures_and_both_downloads_for_ordinary_amounts()
    {
        await using var site = await SiteAsync();
        foreach (var role in new[] { "admin", "accountant" })
        {
            var (client, _) = await site.SignedInAsync(role);
            var html = await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-10");
            Assert.Contains("1,234.10", html);
            Assert.Contains("365.10", html);
            Assert.Contains("href=\"/accounting/regulatory/reserves/xlsx?month=2026-10\"", html);
            Assert.Contains("href=\"/accounting/regulatory/form1/xlsx?month=2026-10\"", html);
            Assert.DoesNotContain(RegulatoryTexts.PrecisionPage, ReportSite.Decode(html));
            Assert.Contains(RegulatoryTexts.Date("31.10.2026"), ReportSite.Decode(html));
            Assert.Contains("href=\"/accounting/regulatory/form6/xlsx?quarter=2026-Q4\"", html);
        }
    }

    [Theory]
    [InlineData("UPR 90071992547408.10", "90071992547408.10", "365.10", false, false, "90,071,992,547,408.10", "რეზერვები!C35: 90071992547408.10 → 90071992547408.09")]
    [InlineData("N1-4 90071992547408.10", "1234.10", "90071992547408.10", true, false, "90,071,992,547,408.10", "BS-IR & RR!E10: 90071992547408.10 → 90071992547408.09")]
    [InlineData("both 90071992547408.01", "90071992547408.01", "90071992547408.01", false, false, "90,071,992,547,408.01", null)]
    public async Task A_figure_Excel_would_change_is_shown_exactly_and_withholds_the_download_it_is_in(string label, string upr, string gross, bool reservesLink, bool form1Link, string exact, string? line)
    {
        await using var site = await SiteAsync(decimal.Parse(upr, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(gross, System.Globalization.CultureInfo.InvariantCulture));
        var (client, _) = await site.SignedInAsync("accountant");
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-10"));
        Assert.True(reservesLink == html.Contains("href=\"/accounting/regulatory/reserves/xlsx?month=2026-10\""), label);
        Assert.True(form1Link == html.Contains("href=\"/accounting/regulatory/form1/xlsx?month=2026-10\""), label);
        Assert.Contains(RegulatoryTexts.PrecisionPage, html);
        Assert.Contains(exact, html);
        if (line is not null) Assert.Contains(line, html);
        var reserves = await client.GetAsync("/accounting/regulatory/reserves/xlsx?month=2026-10");
        var form1 = await client.GetAsync("/accounting/regulatory/form1/xlsx?month=2026-10");
        Assert.Equal(reservesLink ? HttpStatusCode.OK : (HttpStatusCode)422, reserves.StatusCode);
        Assert.Equal(form1Link ? HttpStatusCode.OK : (HttpStatusCode)422, form1.StatusCode);
        foreach (var refused in new[] { reserves, form1 }.Where(r => (int)r.StatusCode == 422))
        {
            Assert.False(refused.Content.Headers.Contains("Content-Disposition"));
            Assert.Equal("text/plain", refused.Content.Headers.ContentType!.MediaType);
            var body = await refused.Content.ReadAsStringAsync();
            Assert.StartsWith(RegulatoryTexts.PrecisionRefused, body);
            // Each refusal names its own workbook's cells: the reserves placement's or form 1's.
            if (line is not null && (refused == reserves) == line.StartsWith("რეზერვები!", StringComparison.Ordinal)) Assert.Contains(line, body);
        }
    }

    [Fact]
    public async Task The_downloads_are_files_named_for_the_month_with_the_months_figures_in_their_cells()
    {
        await using var site = await SiteAsync();
        var (client, _) = await site.SignedInAsync("accountant");
        var reserves = await client.GetAsync("/accounting/regulatory/reserves/xlsx?month=2026-10");
        Assert.Equal(HttpStatusCode.OK, reserves.StatusCode);
        Assert.Equal(ReportWorkbooks.XlsxType, reserves.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment; filename=\"reserves-placement-2026-10.xlsx\"", reserves.Content.Headers.GetValues("Content-Disposition").Single());
        var form1 = await client.GetAsync("/accounting/regulatory/form1/xlsx?month=2026-10");
        Assert.Equal("attachment; filename=\"financial-annex-n1-2026-10.xlsx\"", form1.Content.Headers.GetValues("Content-Disposition").Single());
        var n1 = XlsxSheet.Read(await form1.Content.ReadAsByteArrayAsync());
        Assert.Equal("365.1", XlsxSheet.Text(n1.Sheets["BS-IR & RR"]["E10"]));
        var placement = XlsxSheet.Read(await reserves.Content.ReadAsByteArrayAsync());
        Assert.Contains(placement.Sheets.Values.SelectMany(s => s.Cells.Values), cell => XlsxSheet.Text(cell) == "1234.1");
    }

    [Fact]
    public async Task Another_role_is_sent_home_or_refused_nobody_to_the_login_and_a_month_or_quarter_that_is_not_one_is_400()
    {
        await using var site = await SiteAsync();
        foreach (var role in new[] { "sales", "viewer", "operations_head" })
        {
            var (client, _) = await site.SignedInAsync(role);
            var page = await client.GetAsync("/accounting/regulatory?month=2026-10");
            Assert.Equal((HttpStatusCode.Redirect, "/"), (page.StatusCode, page.Headers.Location!.ToString()));
            foreach (var url in new[] { "/accounting/regulatory/reserves/xlsx?month=2026-10", "/accounting/regulatory/form1/xlsx?month=2026-10", "/accounting/regulatory/form6/xlsx?quarter=2026-Q4" })
            {
                var r = await client.GetAsync(url);
                Assert.Equal((HttpStatusCode.Forbidden, "Forbidden"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
            }
        }
        var anonymous = site.Client();
        foreach (var url in new[] { "/accounting/regulatory?month=2026-10", "/accounting/regulatory/form1/xlsx?month=2026-10", "/accounting/regulatory/form6/xlsx?quarter=2026-Q4" })
            Assert.StartsWith("/login", (await anonymous.GetAsync(url)).Headers.Location!.ToString());
        var (accountant, _) = await site.SignedInAsync("accountant");
        foreach (var route in new[] { "reserves", "form1" })
            foreach (var q in new[] { "", "?month=", "?month=2026-13", "?month=2026-1", "?month=abc", "?month=2026-10-01", "?month=%202026-10", "?month=2026-00" })
            {
                var r = await accountant.GetAsync($"/accounting/regulatory/{route}/xlsx{q}");
                Assert.Equal((HttpStatusCode.BadRequest, "Bad month"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
            }
        foreach (var q in new[] { "", "?quarter=", "?quarter=2026-Q0", "?quarter=2026-Q5", "?quarter=2026q4", "?quarter=%202026-Q4", "?month=2026-10", "?quarter=0099-Q4" })
        {
            var r = await accountant.GetAsync($"/accounting/regulatory/form6/xlsx{q}");
            Assert.Equal((HttpStatusCode.BadRequest, "Bad quarter"), (r.StatusCode, await r.Content.ReadAsStringAsync()));
        }
        Assert.Equal("attachment; filename=\"form6-1900-Q1.xlsx\"", (await accountant.GetAsync("/accounting/regulatory/form6/xlsx?quarter=1900-Q1")).Content.Headers.GetValues("Content-Disposition").Single());
    }

    [Fact]
    public async Task Form_6_on_the_page_and_in_its_file_the_quarter_asked_or_the_months_own_the_rows_escaped_and_below_the_grid()
    {
        await using var site = await SiteAsync();
        var w = site.World;
        await TreatyOn13Async(w, "Re \"A&B\" <ტესტი> (გამოგონილი)");
        await using (var c = await w.Database.OpenAdminAsync())
        {
            var name13 = await c.ExecuteScalarAsync<string>("select name_ka from core.insurance_class where code = N'13'");
            var (client, _) = await site.SignedInAsync("accountant");
            foreach (var (query, quarter, day) in new[] { ("month=2026-10", "2026-Q4", "31.12.2026"), ("month=2026-10&quarter=2026-Q2", "2026-Q2", "30.06.2026"), ("month=2026-02&quarter=2026-Q5", "2026-Q1", "31.03.2026") })
            {
                var page = await ReportSite.PageAsync(client, "/accounting/regulatory?" + query);
                Assert.Contains($"href=\"/accounting/regulatory/form6/xlsx?quarter={quarter}\"", page);
                Assert.Contains($"<option value=\"{quarter}\" selected=\"selected\">", page);
                Assert.Contains($"საანგარიშო თარიღი: {day}", ReportSite.Decode(page));
            }
            var html = await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-10");
            Assert.Contains("Re &quot;A&amp;B&quot; &lt;", html);
            Assert.Equal([$"13 {name13}", "Re \"A&B\" <ტესტი> (გამოგონილი)", "კვოტური", "01.01.2026 – 31.12.2026", "A", "AM Best", "DE", "—", "—"], ReportSite.Cells(html, $"13 {name13}"));

            var file = await client.GetAsync("/accounting/regulatory/form6/xlsx?quarter=2026-Q4");
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("attachment; filename=\"form6-2026-Q4.xlsx\"", file.Content.Headers.GetValues("Content-Disposition").Single());
            var sheet = XlsxSheet.Read(await file.Content.ReadAsByteArrayAsync()).Sheets[Sheet6];
            Assert.Equal([$"13 {name13}", "Re \"A&B\" <ტესტი> (გამოგონილი)", "კვოტური", "01.01.2026 – 31.12.2026", "DE"], new[] { "A9", "B9", "C9", "D9", "G9" }.Select(r => XlsxSheet.Text(sheet[r])));
            Assert.Equal("კომპანიის დასახელება: სატესტო კომპანია (გამოგონილი)", XlsxSheet.Text(sheet["A4"]));
            Assert.Equal("საანგარიშო თარიღი: 31.12.2026", XlsxSheet.Text(sheet["A5"]));

            // Five more reinsurers on 13: five in its group (rows 9–13), the sixth by name (Re 4) in the row added below the grid.
            for (var i = 0; i < 5; i++) await TreatyOn13Async(w, $"Re {i} (გამოგონილი)", $"RI-1{i}");
            var more = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-10"));
            Assert.Contains("1 სტრიქონი ბადის ქვემოთ", more);
            for (var i = 0; i < 5; i++) Assert.Contains($"Re {i} (გამოგონილი)", more);
            var below = XlsxSheet.Read(await (await client.GetAsync("/accounting/regulatory/form6/xlsx?quarter=2026-Q4")).Content.ReadAsByteArrayAsync()).Sheets[Sheet6];
            Assert.Equal([$"13 {name13}", $"13 {name13}", "Re 4 (გამოგონილი)"], new[] { "A9", "A39", "B39" }.Select(r => XlsxSheet.Text(below[r])));

            // None in force on the quarter's day: said so, the empty form given.
            var empty = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-10&quarter=2027-Q1"));
            Assert.Contains(RegulatoryTexts.Form6None, empty);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/accounting/regulatory/form6/xlsx?quarter=2027-Q1")).StatusCode);
        }
    }

    /// <summary>
    /// The old party name had no length limit and form 6 split names over 500 characters across rows (PR358); the new core.party.name holds
    /// at most 400 characters (database/migrations/0001_foundation.sql), so the longest name a party can have here fits one row.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(400)]
    public async Task A_brokers_name_of_any_length_a_party_may_have_is_in_the_file_whole(int length)
    {
        await using var site = await SiteAsync();
        var name = string.Concat(Enumerable.Repeat("Fictional Broker Company ", length / 25 + 1))[..length].TrimEnd();
        var broker = await site.World.PartyAsync(name, foreignId: "BR-LONG", country: "DE");
        await TreatyOn13Async(site.World, "Re Long (გამოგონილი)", broker: broker);
        var (client, _) = await site.SignedInAsync("accountant");
        var file = await client.GetAsync("/accounting/regulatory/form6/xlsx?quarter=2026-Q4");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        var sheet = XlsxSheet.Read(await file.Content.ReadAsByteArrayAsync()).Sheets[Sheet6];
        Assert.Equal("Re Long (გამოგონილი)", XlsxSheet.Text(sheet["B9"]));
        var pieces = new List<string> { XlsxSheet.Text(sheet["H9"]) };
        for (var row = 10; sheet[$"H{row}"] is not null && sheet[$"C{row}"] is null && sheet[$"A{row}"] is null; row++) pieces.Add(XlsxSheet.Text(sheet[$"H{row}"]));
        Assert.Equal(name, string.Join(" ", pieces));
        Assert.Single(pieces);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(name), await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-10"));
    }

    [Theory]
    [InlineData("broker", "E13", "E14", "0")]
    [InlineData("client", "E10", "E11", "33.5")]
    public async Task Form_1s_N1_4_from_the_real_calculator_a_brokers_receivable_aged_not_impaired(string debtor, string gross, string impaired, string reserve)
    {
        // legacy form1-broker-page.test.ts: 365.00 over 2026, 100.00 paid on 15.01.2026; on 31.12.2026 265.00 is owed.
        await using var site = await ReportSite.CreateAsync();
        var p = await site.World.PolicyAsync("P-N14", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 365m)], [("2026-01-01", 365m, debtor)]);
        await site.World.PayAsync(p.Installments[0], "2026-01-15", 100m);
        var (client, _) = await site.SignedInAsync("accountant");
        var file = await client.GetAsync("/accounting/regulatory/form1/xlsx?month=2026-12");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        var sheet = XlsxSheet.Read(await file.Content.ReadAsByteArrayAsync()).Sheets["BS-IR & RR"];
        Assert.Equal(("265", reserve), (XlsxSheet.Text(sheet[gross]), XlsxSheet.Text(sheet[impaired])));
        var html = await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12");
        Assert.Contains("265.00", html);
        Assert.Contains("href=\"/accounting/regulatory/form1/xlsx?month=2026-12\"", html);
    }
}
