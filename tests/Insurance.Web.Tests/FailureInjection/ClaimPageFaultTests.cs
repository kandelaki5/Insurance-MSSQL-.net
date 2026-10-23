using System.Net;
using System.Text.RegularExpressions;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests;

namespace Insurance.Web.Tests.FailureInjection;

/// <summary>
/// The claim page's forms by permission, and a grant taken away after the form was shown, over HTTP on a real database (legacy
/// scripts/integration/claim-payment-permission.test.ts and recovery-permission.test.ts: "the form shown with a group grant; the
/// grant taken away (or a removal added) before the submit", "the accountant's own removal hides only that form; the admin's
/// panel action gives it back", "a viewer with only the recovery or the premium receipt"). An approved GEL claim of 1 000.00, the
/// class's deductible 100.00, dated from the real day in Tbilisi (the pages read it).
/// </summary>
public sealed partial class ClaimPageFaultTests(WebApp app) : IClassFixture<WebApp>
{
    private readonly ClaimWorld w = new(app.Db);
    private static DateOnly Today => Rates.TodayInTbilisi();

    private async Task<Guid> ApprovedClaimAsync()
    {
        var actor = await w.UserAsync("admin");
        var p = await w.PolicyAsync(actor.Id, Today.AddDays(-40), [new("damage", 20000m, 100m)]);
        var u = await w.UserAsync("claims");
        var claim = (await w.RegisterAsync(u, p, Today.AddDays(-10), Today.AddDays(-9), ("damage", "1000")))[0];
        await w.ApproveAsync(u, claim, "1000", Today);
        return claim;
    }

    [GeneratedRegex("<form\\b[^>]*\\baction=\"[^\"]*[?&](?:amp;)?handler=(\\w+)\"")]
    private static partial Regex FormHandler();

    /// <summary>The page's forms, by the handler each posts to, in order.</summary>
    private async Task<List<string>> FormsAsync(HttpClient client, Guid claim) =>
        FormHandler().Matches(await Html.GetAsync(client, $"/claims/{claim}")).Select(m => m.Groups[1].Value).ToList();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid claim, string handler, Dictionary<string, string> fields) =>
        WebApp.PostFormAsync(client, $"/claims/{claim}?handler={handler}", fields, pageUrl: $"/claims/{claim}");

    private static Dictionary<string, string> Pay(Guid claim) => new()
    {
        ["claim_id"] = claim.ToString(), ["amount"] = "250.00", ["paid_on"] = ClaimWorld.D(Today), ["payee_kind"] = "policyholder", ["total_loss_mode"] = "none",
        ["note"] = "ნაწილობრივი ანაზღაურება",
    };

    private static Dictionary<string, string> Recovery(Guid claim, string amount = "25.00") => new()
    {
        ["claim_id"] = claim.ToString(), ["kind"] = "subrogation", ["received_on"] = ClaimWorld.D(Today), ["amount_gel"] = amount, ["note"] = "რეგრესი",
    };

    private Task<int> CountAsync(string table, Guid claim) => w.ScalarAsync<int>($"select count(*) from core.{table} where claim_id = @claim", new { claim });

    private Task SetDecisionByPanelAsync(HttpClient admin, Guid user, string permission, string decision) =>
        WebApp.PostFormAsync(admin, $"/users/permissions?handler=SetUserDecision&user={user}",
            new() { ["answer_for"] = $"decision:{permission}", ["user_id"] = user.ToString(), ["permission"] = permission, ["decision"] = decision }, $"/users/permissions?user={user}")
            .ContinueWith(async t =>
            {
                var html = await Html.BodyAsync(t.Result);
                Assert.Contains(PermissionTexts.Changes.Saved, html);
            }).Unwrap();

    [Theory]
    [InlineData("ungrant")]
    [InlineData("deny")]
    public async Task Claim_payment_the_form_shown_with_a_group_grant_the_grant_taken_away_or_a_removal_added_before_the_submit_refused_nothing_written(string change)
    {
        var claim = await ApprovedClaimAsync();
        var (client, id) = await app.SignedInAsync("underwriter");
        var group = await w.GroupAsync("claim.payment.record", id);
        Assert.Contains("PayClaim", await FormsAsync(client, claim));
        // The page with its form (and its anti-forgery token) is read first; then the change; then the form is sent.
        var page = await client.GetAsync($"/claims/{claim}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        if (change == "ungrant") await w.ExecAsync("delete from core.user_group_permission where group_id = @group", new { group });
        else await w.GrantAsync(id, "claim.payment.record", "deny");
        var answer = await Html.BodyAsync(await PostAsync(client, claim, "PayClaim", Pay(claim)));
        Assert.Contains(WebUtility.HtmlEncode(ClaimTexts.PaymentNotAllowed), answer);
        Assert.Equal((0, 1), (await CountAsync("claim_payment", claim), await CountAsync("claim_reserve", claim)));
        Assert.DoesNotContain("PayClaim", await FormsAsync(client, claim));
    }

    [Theory]
    [InlineData("ungrant")]
    [InlineData("deny")]
    public async Task Recovery_the_form_was_shown_with_a_group_grant_the_grant_taken_away_or_a_removal_added_before_the_submit_refused_nothing_written(string change)
    {
        var claim = await ApprovedClaimAsync();
        var (client, id) = await app.SignedInAsync("underwriter");
        var group = await w.GroupAsync("claim.recovery.receive", id);
        Assert.Contains("AddRecovery", await FormsAsync(client, claim));
        if (change == "ungrant") await w.ExecAsync("delete from core.user_group_permission where group_id = @group", new { group });
        else await w.GrantAsync(id, "claim.recovery.receive", "deny");
        var refused = await PostAsync(client, claim, "AddRecovery", Recovery(claim));
        Assert.Equal($"/claims/{claim}?recovery=not-allowed", Html.Location(refused));
        Assert.Contains(WebUtility.HtmlEncode(ClaimTexts.RecoveryNotAllowed), await Html.GetAsync(client, $"/claims/{claim}?recovery=not-allowed"));
        Assert.Equal(0, await CountAsync("claim_recovery", claim));
    }

    [Fact]
    public async Task Claim_payment_the_accountants_payment_removed_only_the_pay_form_goes_the_recovery_still_works_the_admins_panel_action_gives_it_back()
    {
        var claim = await ApprovedClaimAsync();
        var (accountant, id) = await app.SignedInAsync("accountant");
        var (admin, _) = await app.SignedInAsync("admin");
        var all = await FormsAsync(accountant, claim);
        Assert.Contains("PayClaim", all);
        Assert.Contains("AddRecovery", all);
        await SetDecisionByPanelAsync(admin, id, "claim.payment.record", "deny");
        var denied = await FormsAsync(accountant, claim);
        Assert.Equal(all.Where(f => f != "PayClaim"), denied);
        Assert.Contains(WebUtility.HtmlEncode(ClaimTexts.PaymentNotAllowed), await Html.BodyAsync(await PostAsync(accountant, claim, "PayClaim", Pay(claim))));
        Assert.Equal(0, await CountAsync("claim_payment", claim));
        Assert.Equal($"/claims/{claim}", Html.Location(await PostAsync(accountant, claim, "AddRecovery", Recovery(claim, "10.00"))));
        Assert.Equal([(10.00m, id)], await w.RowsAsync<(decimal, Guid)>("select amount_gel, created_by from core.claim_recovery where claim_id = @claim", new { claim }));
        await SetDecisionByPanelAsync(admin, id, "claim.payment.record", "inherit");
        Assert.Equal(all, await FormsAsync(accountant, claim));
        Assert.Equal($"/claims/{claim}?paid=1", Html.Location(await PostAsync(accountant, claim, "PayClaim", Pay(claim))));
        Assert.Equal([(250m, 100m, 150m, id)], await w.RowsAsync<(decimal, decimal, decimal, Guid)>(
            "select amount, deductible_withheld, net_paid, created_by from core.claim_payment where claim_id = @claim", new { claim }));
    }

    [Fact]
    public async Task Recovery_the_accountants_own_removal_hides_only_the_recovery_form_the_admins_panel_action_gives_it_back()
    {
        var claim = await ApprovedClaimAsync();
        var (accountant, id) = await app.SignedInAsync("accountant");
        var (admin, _) = await app.SignedInAsync("admin");
        var all = await FormsAsync(accountant, claim);
        await SetDecisionByPanelAsync(admin, id, "claim.recovery.receive", "deny");
        var denied = await FormsAsync(accountant, claim);
        Assert.Equal(all.Where(f => f != "AddRecovery"), denied);
        Assert.NotEmpty(denied);
        Assert.Equal($"/claims/{claim}?recovery=not-allowed", Html.Location(await PostAsync(accountant, claim, "AddRecovery", Recovery(claim))));
        await SetDecisionByPanelAsync(admin, id, "claim.recovery.receive", "inherit");
        Assert.Equal(all, await FormsAsync(accountant, claim));
        Assert.Equal($"/claims/{claim}", Html.Location(await PostAsync(accountant, claim, "AddRecovery", Recovery(claim))));
        Assert.Equal([25.00m], await w.RowsAsync<decimal>("select amount_gel from core.claim_recovery where claim_id = @claim", new { claim }));
    }

    [Fact]
    public async Task Claim_payment_a_viewer_given_the_permission_sees_the_pay_form_and_no_other_with_only_the_recovery_or_the_premium_receipt_it_cannot_pay()
    {
        var claim = await ApprovedClaimAsync();
        var (viewer, id) = await app.SignedInAsync("viewer");
        await w.GrantAsync(id, "claim.payment.record", "allow");
        Assert.Equal(["PayClaim"], await FormsAsync(viewer, claim));
        Assert.Equal($"/claims/{claim}?paid=1", Html.Location(await PostAsync(viewer, claim, "PayClaim", Pay(claim))));
        var other = await ApprovedClaimAsync();
        var (only, onlyId) = await app.SignedInAsync("viewer");
        await w.GrantAsync(onlyId, "claim.recovery.receive", "allow");
        await w.GrantAsync(onlyId, "premium.receipt.create", "allow");
        Assert.Equal(["AddRecovery"], await FormsAsync(only, other));
        Assert.Contains(WebUtility.HtmlEncode(ClaimTexts.PaymentNotAllowed), await Html.BodyAsync(await PostAsync(only, other, "PayClaim", Pay(other))));
        Assert.Equal(0, await CountAsync("claim_payment", other));
    }
}
