using System.Net;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;
using Insurance.Integration.Tests.FailureInjection;
using Insurance.Web.Tests.Reports;
using Microsoft.Extensions.DependencyInjection;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Web.Tests.FailureInjection;

/// <summary>
/// The permissions panel, the premium receipt's page, the monthly report page and the impairment download when one of their reads
/// fails, over HTTP on a real database with the command interceptor (legacy scripts/integration/permission-panel.test.ts,
/// permission-actions.test.ts, receipt-permission.test.ts, claim-recovery-report-page.test.ts, report-bulk-reads.test.ts and
/// impairment-xlsx-get.test.ts). The error leaves the page as it is (500 with its message, nothing of the page); nothing is written;
/// the next request is whole.
/// </summary>
public sealed class PageFaultTests(FaultSite site) : IClassFixture<FaultSite>
{
    private const string Key = "premium.receipt.create";

    // ================================================================ the seam's production wiring

    [Fact]
    public void The_application_as_configured_has_no_interceptor()
    {
        var program = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Insurance", "Server=localhost;Database=not_used;User ID=x;Password=x");
        });
        using (program)
            Assert.Null(program.Services.GetRequiredService<DbOptions>().Interceptor);
        Assert.NotNull(site.Factory.Services.GetRequiredService<DbOptions>().Interceptor);
    }

    // ================================================================ permission-panel.test.ts, permission-actions.test.ts

    private async Task<(Guid Anna, Guid Cashiers, string Name)> PanelWorldAsync()
    {
        var anna = Guid.NewGuid();
        var cashiers = Guid.NewGuid();
        var tag = anna.ToString("N")[..6];
        await site.ExecAsync("""
            insert core.app_user (id, username, full_name, password_hash, role_code) values (@anna, @username, @full, N'x', N'underwriter');
            insert core.user_group (id, name) values (@cashiers, @name);
            insert core.user_group_member (group_id, user_id) values (@cashiers, @anna);
            insert core.user_group_permission (group_id, permission) values (@cashiers, @key);
            """, new { anna, username = $"anna.{tag}", full = $"ანა {tag}", cashiers, name = $"სალაროები {tag}", key = Key });
        return (anna, cashiers, $"სალაროები {tag}");
    }

    [Theory]
    [InlineData("roles", "select code, name_ka from core.role order by code")]
    [InlineData("own decisions", Faults.Overrides)]
    [InlineData("group grants", Faults.Grants)]
    [InlineData("its members", "from core.app_user where id in")]
    public async Task Panel_a_failed_read_stops_the_page_the_same_error_comes_out_no_page_the_next_render_is_whole(string name, string fragment)
    {
        var (anna, cashiers, groupName) = await PanelWorldAsync();
        var (admin, _) = await site.SignedInAsync("admin");
        var url = $"/users/permissions?user={anna}&group={cashiers}";
        var sentinel = new InjectedFault($"PANEL_READ_SENTINEL_{name.Replace(' ', '_')}");
        // The panel's snapshot is the page's first snapshot read (the layout's own permission read comes after, when the page renders).
        site.Faults.FailOnce(c => Faults.Has(c, fragment) && c.Isolation == System.Data.IsolationLevel.Snapshot, sentinel);
        await FaultSite.FailedAsync(await admin.GetAsync(url), sentinel);
        Assert.True(site.Faults.AllFired);
        var html = WebUtility.HtmlDecode(await Html.GetAsync(admin, url));
        Assert.Contains($"data-member=\"{anna}\"", html);
        Assert.Contains(groupName, html);
    }

    [Fact]
    public async Task Actions_the_admins_own_permission_denied_and_back_the_admin_can_still_act_afterwards()
    {
        var (_, cashiers, _) = await PanelWorldAsync();
        await site.ExecAsync("delete from core.user_group_permission where group_id = @cashiers", new { cashiers });
        var (admin, adminId) = await site.SignedInAsync("admin");
        async Task<string> Post(string handler, Dictionary<string, string> fields) =>
            WebUtility.HtmlDecode(await Html.BodyAsync(await WebApp.PostFormAsync(admin, $"/users/permissions?handler={handler}&user={adminId}&group={cashiers}", fields,
                $"/users/permissions?user={adminId}&group={cashiers}")));
        Assert.Contains(PermissionTexts.Changes.Saved, await Post("SetUserDecision", new() { ["answer_for"] = $"decision:{Key}", ["user_id"] = adminId.ToString(), ["permission"] = Key, ["decision"] = "deny" }));
        Assert.Contains(PermissionTexts.Changes.Saved, await Post("SetGroupGrant", new() { ["answer_for"] = $"grant:{Key}", ["group_id"] = cashiers.ToString(), ["permission"] = Key, ["granted"] = "true" }));
        Assert.Contains(PermissionTexts.Changes.Saved, await Post("SetUserDecision", new() { ["answer_for"] = $"decision:{Key}", ["user_id"] = adminId.ToString(), ["permission"] = Key, ["decision"] = "inherit" }));
        Assert.Equal(0, await site.ScalarAsync<int>("select count(*) from core.user_permission where user_id = @adminId", new { adminId }));
        Assert.Equal(1, await site.ScalarAsync<int>("select count(*) from core.user_group_permission where group_id = @cashiers and permission = @key", new { cashiers, key = Key }));
        Assert.Equal(3, await site.ScalarAsync<int>("select count(*) from core.audit_log where table_name in (N'user_permission', N'user_group_permission') and user_id = @adminId", new { adminId }));
    }

    [Fact]
    public async Task Actions_a_database_error_in_the_middle_comes_out_as_it_is_nothing_of_the_change_stays_the_retry_saves_it_once()
    {
        var (_, cashiers, _) = await PanelWorldAsync();
        var (admin, adminId) = await site.SignedInAsync("admin");
        var beka = Guid.NewGuid();
        await site.ExecAsync("insert core.app_user (id, username, full_name, password_hash, role_code) values (@beka, @u, N'ბექა', N'x', N'underwriter')", new { beka, u = "beka." + beka.ToString("N")[..6] });
        var fields = new Dictionary<string, string> { ["answer_for"] = $"member:user:{cashiers}", ["group_id"] = cashiers.ToString(), ["user_id"] = beka.ToString(), ["member"] = "true" };
        var sentinel = new InjectedFault("PERMISSION_WRITE_SENTINEL");
        site.Faults.FailOnce(Faults.Any("insert into core.user_group_member"), sentinel);
        await FaultSite.FailedAsync(await WebApp.PostFormAsync(admin, $"/users/permissions?handler=SetGroupMember&user={beka}", fields, $"/users/permissions?user={beka}"), sentinel);
        Task<int> Members() => site.ScalarAsync<int>("select count(*) from core.user_group_member where group_id = @cashiers and user_id = @beka", new { cashiers, beka });
        Task<int> Audited() => site.ScalarAsync<int>("select count(*) from core.audit_log where table_name = N'user_group_member' and user_id = @adminId", new { adminId });
        Assert.Equal((0, 0), (await Members(), await Audited()));
        var saved = await Html.BodyAsync(await WebApp.PostFormAsync(admin, $"/users/permissions?handler=SetGroupMember&user={beka}", fields, $"/users/permissions?user={beka}"));
        Assert.Contains(PermissionTexts.Changes.Saved, WebUtility.HtmlDecode(saved));
        Assert.Equal((1, 1), (await Members(), await Audited()));
    }

    // ================================================================ receipt-permission.test.ts

    [Fact]
    public async Task Receipt_a_failed_permission_read_on_the_page_stops_it_no_page_no_form_shown_by_default()
    {
        var w = new Integration.Tests.Policies.World(site.Db);
        var admin = await w.UserAsync();
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Integration.Tests.Policies.World.Today.AddDays(-20));
        var (accountant, _) = await site.SignedInAsync("accountant");
        Assert.Contains("name=\"amount_gel\"", await Html.GetAsync(accountant, $"/policies/{p.Id}/payment"));
        var sentinel = new InjectedFault("RECEIPT_PAGE_PERMISSION_SENTINEL");
        site.Faults.FailOnce(Faults.Quick(Faults.Overrides), sentinel);
        var body = await FaultSite.FailedAsync(await accountant.GetAsync($"/policies/{p.Id}/payment"), sentinel);
        Assert.DoesNotContain("name=\"amount_gel\"", body);
        Assert.Contains("name=\"amount_gel\"", await Html.GetAsync(accountant, $"/policies/{p.Id}/payment"));
    }
}

/// <summary>The report pages over a report world of their own, with the command interceptor.</summary>
public sealed class ReportPageFaultTests
{
    private static async Task<(ReportWorld W, FaultSite Site)> SiteAsync(Func<ReportWorld, Task> build)
    {
        var w = await CreateAsync();
        await build(w);
        return (w, FaultSite.Over(w.Database));
    }

    // ================================================================ claim-recovery-report-page.test.ts

    [Fact]
    public async Task Octobers_recovery_read_fails_once_the_page_throws_that_error_and_renders_nothing_the_next_attempt_shows_50_for_class_5_1_and_the_total()
    {
        var (w, site) = await SiteAsync(async w =>
        {
            var p = await w.PolicyAsync("P-REC", "2026-09-01", "2027-09-01", [new LineSpec("5.1", 0m, 0m)], issueRows: false);
            var claim = await w.ClaimAsync(p.Id, p.Lines[0], "GEL", "2026-09-20", "2026-09-25");
            await w.ReserveAsync(claim.Id, "2026-10-02", 1000m);
            await w.ClaimPaymentAsync(claim.Id, "2026-10-06", 900m);
            await w.RecoveryAsync(claim.Id, "subrogation", "2026-09-30", 7.50m);
            await w.RecoveryAsync(claim.Id, "salvage", "2026-10-31", 50m);
        });
        await using (w)
        await using (site)
        {
            var (client, _) = await site.SignedInAsync("accountant");
            var before = await w.FingerprintAsync();
            var sentinel = new InjectedFault("CLAIM_RECOVERY_READ_UNAVAILABLE");
            site.Faults.FailOnce(c => Faults.Has(c, "from core.claim_recovery cr") && Equals(c.Parameter("from"), D("2026-10-01")), sentinel);
            var body = await FaultSite.FailedAsync(await client.GetAsync("/accounting/premium-report?month=2026-10"), sentinel);
            Assert.DoesNotContain("<table", body);
            Assert.Equal(before, await w.FingerprintAsync());
            var html = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-10");
            Assert.Equal(["5.1", "სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", "900.00", "1,000.00", "0.00", "50.00", "0.00", "0.00"], ReportSite.Cells(html, "5.1"));
            Assert.Contains(["სულ", "900.00", "1,000.00", "0.00", "50.00", "0.00", "0.00"], ReportSite.TotalRows(html).Concat(ReportSite.Rows(html, "სულ")));
        }
    }

    // ================================================================ report-bulk-reads.test.ts (the page)

    [Theory]
    [InlineData("the instalments", "select i.policy_id, i.id, i.due_on, i.amount")]
    [InlineData("the adjustments", "select j.installment_id, coalesce(sum(j.change), 0)")]
    [InlineData("the allocations", "select i.policy_id, a.amount, a.booked_on, p.method")]
    public async Task Bulk_reads_the_read_fails_once_the_page_gives_that_error_never_a_report_with_zeros_the_retry_gives_the_figures(string name, string fragment)
    {
        var (w, site) = await SiteAsync(async w =>
        {
            var a = await w.PolicyAsync("P-SMALL-1", "2026-10-01", "2027-10-01", [new LineSpec("5.1", 600m, 600m)], [("2026-10-01", 200m, "client"), ("2026-11-01", 200m, "client"), ("2026-12-01", 200m, "client")]);
            await w.AdjustAsync(a.Installments[2], "2026-11-15", -100m);
            await w.AllocateAsync(await w.ReceiptAsync("2026-11-10", 280m, a.PartyId), a.Installments[0], 200m, 200m, 1m, "2026-10-05");
        });
        await using (w)
        await using (site)
        {
            var (client, _) = await site.SignedInAsync("accountant");
            var before = await w.FingerprintAsync();
            var sentinel = new InjectedFault($"BULK_PAGE_{name.Replace(' ', '_')}");
            site.Faults.FailOnce(Faults.Any(fragment), sentinel);
            var body = await FaultSite.FailedAsync(await client.GetAsync("/accounting/premium-report?month=2026-11"), sentinel);
            Assert.DoesNotContain("<table", body);
            Assert.Equal(before, await w.FingerprintAsync());
            // By hand at 30.11: A2 200.00 (due 01.11, overdue) + A3 200.00 − 100.00 = 100.00: 300.00 owed, 200.00 overdue.
            var html = await ReportSite.PageAsync(client, "/accounting/premium-report?month=2026-11");
            Assert.Equal(["GEL", "300.00", "200.00", "", "300.00"], ReportSite.Cells(html, "GEL"));
        }
    }

    // ================================================================ impairment-xlsx-get.test.ts

    private static async Task<(ReportWorld W, FaultSite Site)> ImpairmentSiteAsync() => await SiteAsync(async w =>
    {
        await w.RateAsync("USD", "2026-06-30", 3m);
        var d = await ImpairmentWorld.DebtorsAsync(w, usdBroker: true);
        await w.PayAsync(d.Broker.Installments[0], "2026-06-30", 630m, 3.1m);
    });

    private const string PolicyRead = "from core.policy p";
    private const string PaidRead = "select i.policy_id, coalesce(sum(a.amount), 0)\nfrom core.payment_allocation a join core.installment i";

    private static bool Is(CommandEvent c, string fragment) => c.Sql.Replace("\r\n", "\n", StringComparison.Ordinal).Contains(fragment, StringComparison.Ordinal);

    [Fact]
    public async Task Impairment_the_signed_in_user_cannot_be_read_that_very_error_comes_out_of_GET_nothing_is_read_rated_or_exported_the_past_is_as_it_was()
    {
        var (w, site) = await ImpairmentSiteAsync();
        await using (w)
        await using (site)
        {
            var (client, _) = await site.SignedInAsync("accountant");
            var before = await w.FingerprintAsync();
            site.Faults.ClearLog();
            var sentinel = new InjectedFault("test fault: the signed-in user cannot be read");
            site.Faults.FailOnce(c => Faults.Has(c, "from core.session s"), sentinel);
            var failed = await client.GetAsync("/accounting/impairment/xlsx?date=2026-06-30");
            await FaultSite.FailedAsync(failed, sentinel);
            Assert.NotEqual(ReportWorkbooks.XlsxType, failed.Content.Headers.ContentType?.MediaType);
            Assert.Equal(["select u.id as Id"], site.Faults.Log.Select(c => c.Sql.TrimStart()[..17]));
            Assert.Empty(site.Nbg.Requests);
            Assert.Equal(before, await w.FingerprintAsync());
            var file = await client.GetAsync("/accounting/impairment/xlsx?date=2026-06-30");
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal(ReportWorkbooks.XlsxType, file.Content.Headers.ContentType!.MediaType);
            Assert.Equal(["ფორმა", "N1-4"], XlsxSheet.Read(await file.Content.ReadAsByteArrayAsync()).SheetNames);
        }
    }

    [Fact]
    public async Task Impairment_the_third_read_paid_premium_fails_after_the_first_two_were_read_that_very_error_comes_out_of_GET_no_rate_no_workbook_the_past_is_as_it_was()
    {
        var (w, site) = await ImpairmentSiteAsync();
        await using (w)
        await using (site)
        {
            var (client, _) = await site.SignedInAsync("accountant");
            var before = await w.FingerprintAsync();
            var good = await client.GetAsync("/accounting/impairment/xlsx?date=2026-06-30");
            var goodBook = await good.Content.ReadAsByteArrayAsync();
            site.Faults.ClearLog();
            var sentinel = new InjectedFault("test fault: the paid premium cannot be read");
            site.Faults.FailOnce(c => Is(c, PaidRead), sentinel);
            var failed = await client.GetAsync("/accounting/impairment/xlsx?date=2026-06-30");
            await FaultSite.FailedAsync(failed, sentinel);
            Assert.True(site.Faults.AllFired);
            var log = site.Faults.Log.ToList();
            var paid = log.FindIndex(c => Is(c, PaidRead));
            Assert.Contains(log.Take(paid), c => Is(c, PolicyRead) && Equals(c.Parameter("asOf"), D("2026-06-30")));
            Assert.Contains(log.Take(paid), c => Is(c, "from core.premium_transaction t") && Equals(c.Parameter("asOf"), D("2026-06-30")));
            Assert.DoesNotContain(log, c => Faults.Has(c, "core.exchange_rate"));
            Assert.Empty(site.Nbg.Requests);
            Assert.Equal(before, await w.FingerprintAsync());
            var again = await client.GetAsync("/accounting/impairment/xlsx?date=2026-06-30");
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(XlsxSheet.Rows(XlsxSheet.Read(goodBook), "N1-4").Select(r => string.Join("|", r)), XlsxSheet.Rows(XlsxSheet.Read(await again.Content.ReadAsByteArrayAsync()), "N1-4").Select(r => string.Join("|", r)));
        }
    }
}
