using System.Net;
using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Regulatory;
using Insurance.Integration.Tests;
using RegulatoryTexts = Insurance.Application.Texts.RegulatoryTexts;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The reinsurer's commission page (/accounting/ri-commission) and form 1's 00140 over HTTP, on the world of Insurance-Software PR #362's
/// scripts/integration/ri-commission.test.ts (RiCommissionWorld, "today" 31.12.2026). Legacy accounting/ri-commission/page.tsx: the admin
/// and the accountant only (else home); the record form only with the permission reinsurance.commission.settle and when there is
/// something to settle; each press answered by a redirect to the page with ?date=…&amp;result=…. Legacy regulatory page and form1/xlsx
/// route (regulatory-page.test.ts, form1-broker-page.test.ts as changed by #362; ri-commission.test.ts L160–170): N1-4 00140 (E26).
/// </summary>
public sealed class RiCommissionPageTests
{
    private const string Page = "/accounting/ri-commission";

    private static async Task<(ReportSite Site, RiCommissionWorld Ri)> SiteAsync()
    {
        var site = await ReportSite.CreateAsync();
        return (site, await RiCommissionWorld.BuildAsync(site.World));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields) =>
        ReportSite.PostFormAsync(client, $"{Page}?handler={handler}", fields, $"{Page}?date=2026-12-31");

    private static async Task<int> RowsAsync(ReportSite site)
    {
        await using var c = await site.World.Database.OpenAdminAsync();
        return await c.ExecuteScalarAsync<int>("select count(*) from core.ri_commission_settlement");
    }

    [Fact]
    public async Task Only_the_admin_and_the_accountant_see_it_others_are_sent_home_nobody_to_the_login()
    {
        var (site, _) = await SiteAsync();
        await using var _s = site;
        foreach (var role in new[] { "viewer", "sales", "underwriter", "operations_head" })
        {
            var (client, _) = await site.SignedInAsync(role);
            var r = await client.GetAsync(Page);
            Assert.Equal((HttpStatusCode.Redirect, "/"), (r.StatusCode, r.Headers.Location!.ToString()));
            Assert.DoesNotContain("href=\"/accounting/ri-commission\"", await ReportSite.PageAsync(client, "/"));
        }
        Assert.StartsWith("/login", (await site.Client().GetAsync(Page)).Headers.Location!.ToString());
        foreach (var role in new[] { "admin", "accountant" })
        {
            var (client, _) = await site.SignedInAsync(role);
            var html = await ReportSite.PageAsync(client, Page);
            // The nav link after the regulatory forms' (legacy layout.tsx).
            Assert.Matches("href=\"/accounting/regulatory\"[^<]*</a>\\s*<a href=\"/accounting/ri-commission\">", html);
            Assert.Contains(RiCommissionTexts.Title, ReportSite.Decode(html));
        }
    }

    [Fact]
    public async Task The_accountant_sees_the_receivable_per_treaty_and_00140_at_the_days_rate_and_records_and_reverses_a_settlement()
    {
        var (site, ri) = await SiteAsync();
        await using var _s = site;
        var (client, accountant) = await site.SignedInAsync("accountant");
        // No date: today (31.12.2026); a future one: today too.
        foreach (var url in new[] { Page, $"{Page}?date=2027-01-01", $"{Page}?date=2026-12-31" })
        {
            var html = ReportSite.Decode(await ReportSite.PageAsync(client, url));
            Assert.Equal(["T-FRONT ხელშეკრულება T-FRONT", "Fictional Re", "USD", "300.00", "0.00", "300.00"], ReportSite.Cells(html, "T-FRONT ხელშეკრულება T-FRONT"));
            Assert.Equal([[RiCommissionTexts.Total, "810.45 ₾"]], ReportSite.TotalRows(html));
            Assert.Contains($"{RiCommissionTexts.RatesUsed}: USD 2.7015 (31.12.2026)", html);
            Assert.Contains("value=\"2026-12-31\"", html);
            Assert.Contains(RiCommissionTexts.Record.Title, html);
        }
        // October: the commission before the cancellation, 945.53.
        Assert.Equal([[RiCommissionTexts.Total, "945.53 ₾"]], ReportSite.TotalRows(ReportSite.Decode(await ReportSite.PageAsync(client, $"{Page}?date=2026-10-31"))));
        // Before anything is booked: said so, no form.
        var empty = ReportSite.Decode(await ReportSite.PageAsync(client, $"{Page}?date=2026-09-30"));
        Assert.Contains(RiCommissionTexts.None, empty);
        Assert.DoesNotContain(RiCommissionTexts.Record.Title, empty);

        // Recorded through the form: back to the page of its day with the answer; the row written by the accountant.
        var ok = await PostAsync(client, "Record", new() { ["treaty_id"] = Uuid.Text(ri.Front), ["settled_on"] = "2026-12-01", ["kind"] = "offset", ["currency"] = "USD", ["amount"] = " 100.00 ", ["reference"] = "RI statement Q4", ["note"] = " " });
        Assert.Equal((HttpStatusCode.Redirect, $"{Page}?date=2026-12-01&result=ok"), (ok.StatusCode, ok.Headers.Location!.ToString()));
        var after = ReportSite.Decode(await ReportSite.PageAsync(client, $"{Page}?date=2026-12-31&result=ok"));
        Assert.Contains($"<p class=\"note\"><strong>{RiCommissionTexts.Outcomes["ok"]}</strong></p>", after);
        Assert.Equal([[RiCommissionTexts.Total, "540.30 ₾"]], ReportSite.TotalRows(after));
        Assert.Equal(["01.12.2026", "T-FRONT ხელშეკრულება T-FRONT", "ჩათვლა", "USD", "100.00", "RI statement Q4", "", RiCommissionTexts.Record.Reverse], ReportSite.Cells(after, "01.12.2026"));
        await using (var c = await site.World.Database.OpenAdminAsync())
            Assert.Equal((accountant, (string?)null), await c.QuerySingleAsync<(Guid, string?)>("select created_by, note from core.ri_commission_settlement"));
        // Above what is left, a closed month, a bad amount: answered, nothing written.
        foreach (var (day, amount, outcome) in new[] { ("2026-12-10", "250.00", "exceeds"), ("2026-11-30", "1.00", "period-closed"), ("2026-12-10", "1,00", "invalid") })
        {
            var r = await PostAsync(client, "Record", new() { ["treaty_id"] = Uuid.Text(ri.Front), ["settled_on"] = day, ["kind"] = "offset", ["currency"] = "USD", ["amount"] = amount });
            Assert.Equal($"{Page}?date={day}&result={outcome}", r.Headers.Location!.ToString());
        }
        Assert.Equal(1, await RowsAsync(site));
        Assert.Contains(RiCommissionTexts.Outcomes["exceeds"], ReportSite.Decode(await ReportSite.PageAsync(client, $"{Page}?result=exceeds")));
        // An outcome the texts do not know shows nothing.
        Assert.DoesNotContain("<p class=\"note\"><strong>", await ReportSite.PageAsync(client, $"{Page}?result=constructor"));

        // Reversed through its form: the amount back; the row marked, the reversal marked.
        await using (var c = await site.World.Database.OpenAdminAsync())
        {
            var id = await c.ExecuteScalarAsync<Guid>("select id from core.ri_commission_settlement");
            var rev = await PostAsync(client, "Reverse", new() { ["settlement_id"] = Uuid.Text(id), ["reversed_on"] = "2026-12-05" });
            Assert.Equal($"{Page}?date=2026-12-05&result=reversed", rev.Headers.Location!.ToString());
            var again = await PostAsync(client, "Reverse", new() { ["settlement_id"] = Uuid.Text(id), ["reversed_on"] = "2026-12-06" });
            Assert.Equal($"{Page}?date=2026-12-06&result=already", again.Headers.Location!.ToString());
        }
        var reversed = ReportSite.Decode(await ReportSite.PageAsync(client, $"{Page}?date=2026-12-31"));
        Assert.Equal([[RiCommissionTexts.Total, "810.45 ₾"]], ReportSite.TotalRows(reversed));
        Assert.Equal(RiCommissionTexts.Record.ReversedMark, ReportSite.Cells(reversed, "01.12.2026")[^1]);
        Assert.Equal(["05.12.2026", "T-FRONT ხელშეკრულება T-FRONT", "ჩათვლა", "USD", "-100.00", "", "", RiCommissionTexts.Record.ReversalMark], ReportSite.Cells(reversed, "05.12.2026"));
        Assert.DoesNotContain("handler=Reverse", reversed);
    }

    [Fact]
    public async Task The_form_only_with_the_permission_and_without_it_a_press_is_refused_with_nothing_written()
    {
        var (site, ri) = await SiteAsync();
        await using var _s = site;
        var (client, accountant) = await site.SignedInAsync("accountant");
        Assert.Equal("ok", (await RiCommissionLedger.RecordAsync(site.World.App, new CurrentUser(accountant, "a", "a", "accountant", "accountant"),
            Uuid.Text(ri.Front), "2026-12-01", "offset", "USD", "100.00", null, null)).Outcome);
        var html = await ReportSite.PageAsync(client, Page);
        Assert.Contains("handler=Record", html);
        Assert.Contains("handler=Reverse", html);
        // The permission taken from this accountant personally: the figures stay, the forms go; a press (an old page) is refused.
        await using (var c = await site.World.Database.OpenAdminAsync())
            await c.ExecuteAsync("insert core.user_permission (user_id, permission, decision) values (@accountant, N'reinsurance.commission.settle', N'deny')", new { accountant });
        var denied = ReportSite.Decode(await ReportSite.PageAsync(client, Page));
        Assert.Equal([[RiCommissionTexts.Total, "540.30 ₾"]], ReportSite.TotalRows(denied));
        Assert.DoesNotContain("handler=Record", denied);
        Assert.DoesNotContain("handler=Reverse", denied);
        Assert.DoesNotContain(RiCommissionTexts.Record.Title, denied);
        var r = await PostAsync(client, "Record", new() { ["treaty_id"] = Uuid.Text(ri.Front), ["settled_on"] = "2026-12-10", ["kind"] = "offset", ["currency"] = "USD", ["amount"] = "1.00" });
        Assert.Equal($"{Page}?result=not-allowed", r.Headers.Location!.ToString());
        Assert.Contains(RiCommissionTexts.Outcomes["not-allowed"], ReportSite.Decode(await ReportSite.PageAsync(client, $"{Page}?result=not-allowed")));
        Assert.Equal(1, await RowsAsync(site));
        // The admin has it by role.
        var (admin, _) = await site.SignedInAsync("admin");
        Assert.Contains("handler=Record", await ReportSite.PageAsync(admin, Page));
        // A viewer pressing an old form: refused by the permission, then sent home by the page.
        var (viewer, _) = await site.SignedInAsync("viewer");
        var v = await ReportSite.PostFormAsync(viewer, $"{Page}?handler=Record", new() { ["treaty_id"] = Uuid.Text(ri.Front), ["settled_on"] = "2026-12-10", ["kind"] = "offset", ["currency"] = "USD", ["amount"] = "1.00" }, "/");
        Assert.Equal($"{Page}?result=not-allowed", v.Headers.Location!.ToString());
        Assert.Equal(1, await RowsAsync(site));
    }

    [Fact]
    public async Task Form_1_carries_00140_on_the_page_and_in_its_file()
    {
        var (site, _) = await SiteAsync();
        await using var _s = site;
        var (client, _) = await site.SignedInAsync("accountant");
        var html = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12"));
        Assert.Equal([RegulatoryTexts.N14Lines["E26"], "810.45"], ReportSite.Cells(html, RegulatoryTexts.N14Lines["E26"]));
        var form1 = await client.GetAsync("/accounting/regulatory/form1/xlsx?month=2026-12");
        Assert.Equal(HttpStatusCode.OK, form1.StatusCode);
        var n1 = XlsxSheet.Read(await form1.Content.ReadAsByteArrayAsync());
        Assert.Equal("810.45", XlsxSheet.Text(n1.Sheets["BS-IR & RR"]["E26"]));
        // October: before the cancellation, 945.53; nothing accrued yet in September: 0.
        Assert.Equal([RegulatoryTexts.N14Lines["E26"], "945.53"], ReportSite.Cells(ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-10")), RegulatoryTexts.N14Lines["E26"]));
        Assert.Equal([RegulatoryTexts.N14Lines["E26"], "0.00"], ReportSite.Cells(ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-09")), RegulatoryTexts.N14Lines["E26"]));
    }
    /// <summary>
    /// Codex R004-01 (INS-DOTNET-MSSQL-004): the balance's 00070, reinsurance receivables, sums N1-4 00140 among its inputs, so the preview
    /// shows the figure the file carries, marked as holding only the reinsurer's commission — never "not kept" with the figure hidden.
    /// The subform, the balance on the page and the balance in the file agree at the start, after a partial settlement and after its reversal.
    /// </summary>
    [Fact]
    public async Task Balance_00070_shows_the_commission_receivable_as_the_file_does_partly_kept_before_and_after_a_partial_settlement_and_its_reversal()
    {
        var (site, ri) = await SiteAsync();
        await using var _s = site;
        var (client, accountant) = await site.SignedInAsync("accountant");
        static decimal Number(Value v) => decimal.Parse(ReservesPlacement.Shown(v)!, System.Globalization.CultureInfo.InvariantCulture);
        var balanceLabel = $"00070 {RegulatoryTexts.BalanceLines["00070"]} — {RegulatoryTexts.PartlyKept}";
        async Task<(string N14, string Balance, decimal FileN14, decimal FileBalance)> FiguresAsync()
        {
            var html = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12"));
            var file = await client.GetAsync("/accounting/regulatory/form1/xlsx?month=2026-12");
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            var wb = TemplateWorkbook.Open(await file.Content.ReadAsByteArrayAsync());
            return (ReportSite.Cells(html, RegulatoryTexts.N14Lines["E26"])[^1], ReportSite.Cells(html, balanceLabel)[^1],
                Number(wb.ValueOf(FinancialAnnex.ReceivablesSheet, FinancialAnnex.N14RiCommission)), Number(wb.ValueOf(FinancialAnnex.BalanceSheet, "E15")));
        }
        // At the start: 300.00 USD at 2.7015 = 810.45 everywhere.
        Assert.Equal(("810.45", "810.45", 810.45m, 810.45m), await FiguresAsync());
        // A partial settlement of 100.00 USD: 200.00 USD left, 540.30.
        var me = new CurrentUser(accountant, "a", "a", "accountant", "accountant");
        Assert.Equal("ok", (await RiCommissionLedger.RecordAsync(site.World.App, me, Uuid.Text(ri.Front), "2026-12-01", "offset", "USD", "100.00", null, null)).Outcome);
        Assert.Equal(("540.30", "540.30", 540.30m, 540.30m), await FiguresAsync());
        // Its reversal: back to 810.45.
        Guid settlement;
        await using (var c = await site.World.Database.OpenAdminAsync())
            settlement = await c.ExecuteScalarAsync<Guid>("select id from core.ri_commission_settlement");
        Assert.Equal("reversed", (await RiCommissionLedger.ReverseAsync(site.World.App, me, Uuid.Text(settlement), "2026-12-05")).Outcome);
        Assert.Equal(("810.45", "810.45", 810.45m, 810.45m), await FiguresAsync());
        // 00210 stays not kept, its figure not shown.
        var page = ReportSite.Decode(await ReportSite.PageAsync(client, "/accounting/regulatory?month=2026-12"));
        Assert.Equal(RegulatoryTexts.Empty, ReportSite.Cells(page, $"00210 {RegulatoryTexts.BalanceLines["00210"]} — {RegulatoryTexts.NotKept}")[^1]);
    }
}
