using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Insurance.Integration.Tests;
using Insurance.Integration.Tests.FailureInjection;

namespace Insurance.Web.Tests.FailureInjection;

/// <summary>
/// The financial director's panel's uncovered cases over HTTP on a real database of its own per test (legacy
/// scripts/integration/fd-panel.test.ts): who sees it (a group, a personal grant, each one's removal, the address naming a role),
/// its permission read failing, seeing not authorising, what each list shows (two requests of one claim, a kind never under
/// another), a list that cannot be read, the menu's link, and the month's figures (a pending request changes none, a carried-out
/// one counts gross; the report failing gives no figure; refused, no report read). The world: one approved claim of 30 000.00 with
/// two payment requests above the threshold (15 000.00 and 12 000.00 gross, the class's deductible 100.00 once), a refund request
/// and a receipt's reversal request on a policy; all dated from the real day in Tbilisi.
/// </summary>
public sealed partial class FdPanelFaultTests
{
    private const string Key = "finance.panel.view";
    private static DateOnly Today => Rates.TodayInTbilisi();

    private sealed record Panel(FaultSite Site, Guid Claim, Guid Cq1, Guid Cq2, Guid Refund, Guid Reversal, Guid Receipt, Guid PolicyId, CurrentUser Accountant) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await ((IAsyncDisposable)Site).DisposeAsync();
    }

    private static async Task<Panel> PanelAsync()
    {
        var s = await FaultSite.CreateAsync();
        var w = new ClaimWorld(s.Db);
        var admin = await w.UserAsync("admin");
        var accountant = await w.UserAsync("accountant");
        var policy = await w.PolicyAsync(admin.Id, Today.AddDays(-40), [new("damage", 50000m, 100m, 400m)], [(Today.AddDays(-40), 400m)]);
        var claims = await w.UserAsync("claims");
        var claim = (await w.RegisterAsync(claims, policy, Today.AddDays(-10), Today.AddDays(-9), ("damage", "30000")))[0];
        await w.ApproveAsync(claims, claim, "30000", Today);
        Assert.Equal($"/claims/{claim}?payment=requested", (await w.PayAsync(accountant, claim, "15000.00", Today, Today)).RedirectTo);
        Assert.Equal($"/claims/{claim}?payment=requested", (await w.PayAsync(accountant, claim, "12000.00", Today, Today)).RedirectTo);
        var requests = await w.RowsAsync<Guid>("select id from core.claim_payment_request where claim_id = @claim order by created_at", new { claim });
        var receipt = await w.PaidAsync(admin.Id, policy, Today.AddDays(-5), 400m);
        var refund = Guid.NewGuid();
        var reversal = Guid.NewGuid();
        await w.App.AsUserAsync(accountant.Id, async tx =>
        {
            await tx.ExecuteAsync("""
                insert core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, payer_party_id, created_at)
                values (@refund, @policy, @today, 10000.01, N'GEL', 1, N'bank', @payer, dateadd(minute, 1, sysdatetimeoffset()))
                """, new { refund, policy = policy.Id, today = Today, payer = policy.PartyId });
            return await tx.ExecuteAsync("""
                insert core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason)
                values (@reversal, @receipt, @policy, @today, 400, N'bank', @payer, N'მიზეზი')
                """, new { reversal, receipt, policy = policy.Id, today = Today, payer = policy.PartyId });
        });
        return new Panel(s, claim, requests[0], requests[1], refund, reversal, receipt, policy.Id, accountant);
    }

    [GeneratedRegex("<div class=\"card\" data-queue=\"(\\w+)\">([\\s\\S]*?)(?=<div class=\"card\" data-(?:queue|section))")]
    private static partial Regex Queue();

    private static string Section(string html, string queue) => Queue().Matches(html).FirstOrDefault(m => m.Groups[1].Value == queue)?.Groups[2].Value ?? "";

    [GeneratedRegex("<tr>\\s*<td>[^<]*</td>\\s*<td>[^<]*</td>\\s*<td><code>([0-9a-f-]{36})</code></td>")]
    private static partial Regex RequestId();

    private static List<Guid> Ids(string html, string queue) => RequestId().Matches(Section(html, queue)).Select(m => Guid.Parse(m.Groups[1].Value)).ToList();

    private static bool ListRead(CommandEvent c) =>
        Faults.Has(c, "from core.claim_payment_request q") || Faults.Has(c, "from core.premium_refund_request q") || Faults.Has(c, "from core.receipt_reversal_request q");

    private static bool ReportRead(CommandEvent c) => Faults.Has(c, "from core.premium_transaction") || Faults.Has(c, "from core.claim_reserve");

    private static async Task<string> GetAsync(FaultSite s, HttpClient client, string url = "/finance/panel")
    {
        s.Faults.ClearLog();
        return await Html.GetAsync(client, url);
    }

    private static Task DecideAsync(FaultSite s, Guid user, string decision, string key = Key) =>
        s.ExecAsync("delete from core.user_permission where user_id = @user and permission = @key; insert core.user_permission (user_id, permission, decision) values (@user, @key, @decision)",
            new { user, key, decision });

    private static async Task<Guid> GroupAsync(FaultSite s, Guid member, string key = Key)
    {
        var group = Guid.NewGuid();
        await s.ExecAsync("""
            insert core.user_group (id, name) values (@group, @name);
            insert core.user_group_permission (group_id, permission) values (@group, @key);
            insert core.user_group_member (group_id, user_id) values (@group, @member);
            """, new { group, name = "ფინანსები " + group.ToString("N")[..6], key, member });
        return group;
    }

    [Fact]
    public async Task The_director_a_viewer_given_it_by_a_group_sees_the_lists_its_own_removal_takes_it_away_the_admins_own_removal_too()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (director, directorId) = await s.SignedInAsync("viewer");
        await GroupAsync(s, directorId);
        Assert.Equal([p.Cq1, p.Cq2], Ids(await GetAsync(s, director), "claim"));
        await DecideAsync(s, directorId, "deny");
        Assert.Contains(FdPanelTexts.NotAllowed, await GetAsync(s, director));
        Assert.DoesNotContain(s.Faults.Log, ListRead);
        var (admin, adminId) = await s.SignedInAsync("admin");
        Assert.Equal([p.Refund], Ids(await GetAsync(s, admin), "refund"));
        await DecideAsync(s, adminId, "deny");
        var refused = await GetAsync(s, admin);
        Assert.Contains(FdPanelTexts.NotAllowed, refused);
        Assert.DoesNotContain("data-queue", refused);
        Assert.DoesNotContain(s.Faults.Log, ListRead);
    }

    [Fact]
    public async Task A_personal_grant_gives_it_the_groups_grant_taken_away_takes_it_back_a_role_group_or_permission_named_in_the_address_changes_nothing()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (viewer, viewerId) = await s.SignedInAsync("viewer");
        var forged = await GetAsync(s, viewer, $"/finance/panel?role=admin&roleCode=admin&group={Guid.NewGuid()}&permission={Key}&decision=allow&claim={p.Cq1}");
        Assert.Contains(FdPanelTexts.NotAllowed, forged);
        Assert.DoesNotContain(s.Faults.Log, ListRead);
        await DecideAsync(s, viewerId, "allow");
        Assert.Equal([p.Refund], Ids(await GetAsync(s, viewer), "refund"));
        var (other, otherId) = await s.SignedInAsync("viewer");
        var group = await GroupAsync(s, otherId);
        Assert.Equal([p.Refund], Ids(await GetAsync(s, other), "refund"));
        await s.ExecAsync("delete from core.user_group_permission where group_id = @group", new { group });
        Assert.Contains(FdPanelTexts.NotAllowed, await GetAsync(s, other));
        Assert.DoesNotContain(s.Faults.Log, ListRead);
    }

    [Theory]
    [InlineData("own decisions")]
    [InlineData("memberships")]
    [InlineData("group grants")]
    public async Task The_read_of_the_permission_fails_the_same_error_no_list_read_never_no_overrides_allowed(string name)
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (director, directorId) = await s.SignedInAsync("viewer");
        await GroupAsync(s, directorId);
        s.Faults.ClearLog();
        var sentinel = new InjectedFault($"FD_PANEL_{name.Replace(' ', '_')}");
        s.Faults.FailOnce(Faults.Any(Faults.PermissionReads.Single(r => r.Name == name).Fragment), sentinel);
        await FaultSite.FailedAsync(await director.GetAsync("/finance/panel"), sentinel);
        Assert.DoesNotContain(s.Faults.Log, ListRead);
        Assert.DoesNotContain(s.Faults.Log, ReportRead);
    }

    [Fact]
    public async Task Seeing_is_not_authorising_finance_panel_view_does_not_give_payment_large_authorize_nor_does_that_give_the_panel()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (viewer, viewerId) = await s.SignedInAsync("viewer");
        await DecideAsync(s, viewerId, "allow");
        var app = new Insurance.Application.Data.Db(new() { ConnectionString = s.Db.AppConnection });
        var inputs = await PermissionStore.ReadAsync(app, viewerId);
        var subject = new PermissionSubject(viewerId.ToString(), "viewer");
        Assert.Equal((true, false), (Permissions.EvaluatePermission(subject, Key, inputs.Overrides, inputs.Groups).Allowed,
            Permissions.EvaluatePermission(subject, "payment.large.authorize", inputs.Overrides, inputs.Groups).Allowed));
        // The panel offers no authorisation of its own: its only form is the month's GET; the card's action decides.
        var panel = Html.Between(await GetAsync(s, viewer), "<h1>", "</main>");
        Assert.DoesNotContain("method=\"post\"", panel);
        Assert.DoesNotContain("handler=", panel);
        var (director, directorId) = await s.SignedInAsync("viewer");
        await GroupAsync(s, directorId, "payment.large.authorize");
        Assert.Contains(FdPanelTexts.NotAllowed, await GetAsync(s, director));
        Assert.DoesNotContain(s.Faults.Log, ListRead);
    }

    [Fact]
    public async Task A_kind_never_shows_under_another_two_pending_requests_of_one_claim_are_both_listed_and_the_page_says_the_card_decides()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (admin, _) = await s.SignedInAsync("admin");
        var h = await GetAsync(s, admin);
        var lists = new Dictionary<string, List<Guid>> { ["claim"] = Ids(h, "claim"), ["refund"] = Ids(h, "refund"), ["reversal"] = Ids(h, "reversal") };
        Assert.Equal([p.Cq1, p.Cq2], lists["claim"]);
        Assert.Equal([p.Refund], lists["refund"]);
        Assert.Equal([p.Reversal], lists["reversal"]);
        foreach (var (list, ids) in lists)
            foreach (var other in lists.Keys.Where(k => k != list))
                Assert.All(ids, id => Assert.DoesNotContain(id.ToString(), Section(h, other)));
        Assert.Contains($"href=\"/claims/{p.Claim}\"", Section(h, "claim"));
        Assert.DoesNotContain("href=\"/policies/", Section(h, "claim"));
        Assert.Contains($"href=\"/policies/{p.PolicyId}\"", Section(h, "refund"));
        Assert.Contains($"href=\"/policies/{p.PolicyId}\"", Section(h, "reversal"));
        Assert.DoesNotContain(FdPanelTexts.Columns.Receipt, Section(h, "claim"));
        Assert.DoesNotContain(FdPanelTexts.Columns.Receipt, Section(h, "refund"));
        Assert.Contains(FdPanelTexts.Columns.Receipt, Section(h, "reversal"));
        Assert.Contains($"<code>{p.Receipt}</code>", Section(h, "reversal"));
        // Both requests of the one claim are "not carried out"; the note says the card decides and the panel may be stale.
        Assert.Equal(2, Regex.Matches(Section(h, "claim"), Regex.Escape(FdPanelTexts.Pending)).Count);
        Assert.Contains(WebUtility.HtmlEncode(FdPanelTexts.Note), h);
        Assert.Contains("მოძველებული", FdPanelTexts.Note);
        Assert.Contains("ორი მოთხოვნა", FdPanelTexts.Note);
        // Each list read once, none per row.
        Assert.Equal(3, s.Faults.Log.Count(ListRead));
    }

    [Theory]
    [InlineData("208", "its table is not there")]
    [InlineData("207", "its column is not there")]
    public async Task The_claim_lists_read_answers_not_ready_never_nothing_pending_the_other_lists_as_they_are(string code, string label)
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (admin, _) = await s.SignedInAsync("admin");
        // The migration of the list not applied, as the legacy test's 42P01 / 42703: the table (or a column it reads) renamed
        // just before the list's read, and back right after the page. A test database of its own; nothing else reads it meanwhile.
        var (rename, back) = code == "208"
            ? ("exec sp_rename N'core.claim_payment_request', N'claim_payment_request_x'", "exec sp_rename N'core.claim_payment_request_x', N'claim_payment_request'")
            : ("exec sp_rename N'core.claim_payment_request.created_by', N'created_by_x', N'COLUMN'", "exec sp_rename N'core.claim_payment_request.created_by_x', N'created_by', N'COLUMN'");
        s.Faults.BeforeOnce(c => Faults.Has(c, "from core.claim_payment_request q"), () => s.Now(rename));
        string h;
        try
        {
            h = await GetAsync(s, admin);
        }
        finally
        {
            s.Now(back);
        }
        Assert.True(s.Faults.AllFired, label);
        Assert.Contains(FdPanelTexts.NotReady, Section(h, "claim"));
        Assert.DoesNotContain(FdPanelTexts.Empty, Section(h, "claim"));
        Assert.Equal([p.Refund], Ids(h, "refund"));
        Assert.Equal([p.Reversal], Ids(h, "reversal"));
        Assert.Equal([p.Cq1, p.Cq2], Ids(await GetAsync(s, admin), "claim"));
    }

    [Fact]
    public async Task A_list_read_fails_otherwise_could_not_be_read_never_nothing_pending()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (admin, _) = await s.SignedInAsync("admin");
        s.Faults.FailOnce(c => Faults.Has(c, "from core.receipt_reversal_request q"), new InvalidOperationException("connection lost"));
        var h = await GetAsync(s, admin);
        Assert.Contains(FdPanelTexts.Failed, Section(h, "reversal"));
        Assert.DoesNotContain(FdPanelTexts.Empty, Section(h, "reversal"));
        Assert.Equal([p.Cq1, p.Cq2], Ids(h, "claim"));
        Assert.Equal([p.Refund], Ids(h, "refund"));
    }

    [Fact]
    public async Task The_menu_the_admin_and_the_director_given_it_see_the_link_the_viewer_does_not_a_removal_hides_it_the_menu_reads_no_list()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (admin, _) = await s.SignedInAsync("admin");
        var (viewer, _) = await s.SignedInAsync("viewer");
        var (director, directorId) = await s.SignedInAsync("viewer");
        Assert.Contains("href=\"/finance/panel\"", await GetAsync(s, admin, "/policies"));
        Assert.DoesNotContain("href=\"/finance/panel\"", await GetAsync(s, viewer, "/policies"));
        await GroupAsync(s, directorId);
        Assert.Contains("href=\"/finance/panel\"", await GetAsync(s, director, "/policies"));
        await DecideAsync(s, directorId, "deny");
        Assert.DoesNotContain("href=\"/finance/panel\"", await GetAsync(s, director, "/policies"));
        Assert.DoesNotContain(s.Faults.Log, ListRead);
    }

    [Fact]
    public async Task The_menu_the_permission_read_fails_the_links_are_hidden_the_rest_of_the_menu_is_there()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (admin, _) = await s.SignedInAsync("admin");
        Assert.Contains("href=\"/audit\"", await GetAsync(s, admin, "/policies"));
        s.Faults.FailOnce(Faults.Quick(Faults.Overrides), new InjectedFault("down"));
        var h = await GetAsync(s, admin, "/policies");
        Assert.True(s.Faults.AllFired);
        Assert.DoesNotContain("href=\"/finance/panel\"", h);
        Assert.DoesNotContain("href=\"/audit\"", h);
        Assert.Contains("href=\"/policies\"", h);
        Assert.Contains("href=\"/claims\"", h);
    }

    [GeneratedRegex("<tr data-figure=\"(\\w+)\"><td>[^<]*</td><td class=\"num\">([^<]*)</td></tr>")]
    private static partial Regex Figure();

    private static Dictionary<string, string> Figures(string html) => Figure().Matches(html).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    [Fact]
    public async Task A_pending_request_changes_no_figure_once_carried_out_its_payment_counts_in_paid_gross_as_the_report_counts_it_never_the_request_count_or_the_net()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (admin, adminId) = await s.SignedInAsync("admin");
        var w = new ClaimWorld(s.Db);
        var month = Today.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var before = Figures(await GetAsync(s, admin, $"/finance/panel?month={month}"));
        Assert.Equal("0.00", before["paid"]);
        // A third request: the figures stay; the lists have three.
        Assert.Equal($"/claims/{p.Claim}?payment=requested", (await w.PayAsync(p.Accountant, p.Claim, "11000.00", Today, Today)).RedirectTo);
        var pending = await GetAsync(s, admin, $"/finance/panel?month={month}");
        Assert.Equal(before, Figures(pending));
        Assert.Equal(3, Ids(pending, "claim").Count);
        // Carried out by the financial director: gross 15 000.00, the deductible 100.00 withheld — net 14 900.00; paid counts the gross.
        var director = new CurrentUser(adminId, "", "", "admin", "admin");
        Assert.Equal($"/claims/{p.Claim}?payment=approved", await ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, director,
            ClaimWorld.Form(("claim_id", p.Claim.ToString()), ("request_id", p.Cq1.ToString()))));
        Assert.Equal([(15000m, 100m, 14900m)], await w.RowsAsync<(decimal, decimal, decimal)>("select amount, deductible_withheld, net_paid from core.claim_payment where claim_id = @c", new { c = p.Claim }));
        var after = await GetAsync(s, admin, $"/finance/panel?month={month}");
        Assert.Equal("15,000.00", Figures(after)["paid"]);
        Assert.Equal(2, Ids(after, "claim").Count);
        Assert.DoesNotContain(p.Cq1, Ids(after, "claim"));
    }

    [Fact]
    public async Task The_report_cannot_be_built_could_not_be_built_no_figure_at_all_never_zeros_the_three_lists_as_they_are()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (admin, _) = await s.SignedInAsync("admin");
        s.Faults.FailOnce(c => Faults.Has(c, "from core.premium_transaction"), new InjectedFault("connection lost"));
        var h = await GetAsync(s, admin);
        Assert.True(s.Faults.AllFired);
        var month = Html.Between(h, "data-section=\"month\"");
        Assert.Contains(FdPanelTexts.Summary.Failed, month);
        Assert.DoesNotContain("data-figure", month);
        Assert.DoesNotContain("data-receivable", month);
        Assert.DoesNotContain("0.00", month);
        Assert.Equal([p.Cq1, p.Cq2], Ids(h, "claim"));
        Assert.Equal([p.Refund], Ids(h, "refund"));
        Assert.Equal([p.Reversal], Ids(h, "reversal"));
    }

    [Fact]
    public async Task Refused_or_the_permission_read_fails_no_report_read_at_all()
    {
        await using var p = await PanelAsync();
        var s = p.Site;
        var (viewer, _) = await s.SignedInAsync("viewer");
        Assert.Contains(FdPanelTexts.NotAllowed, await GetAsync(s, viewer));
        Assert.DoesNotContain(s.Faults.Log, ReportRead);
        var (admin, _) = await s.SignedInAsync("admin");
        s.Faults.ClearLog();
        var sentinel = new InjectedFault("FD_PANEL_REPORT_DOWN");
        s.Faults.FailOnce(Faults.Quick(Faults.Overrides), sentinel);
        await FaultSite.FailedAsync(await admin.GetAsync("/finance/panel"), sentinel);
        Assert.DoesNotContain(s.Faults.Log, ReportRead);
    }
}
