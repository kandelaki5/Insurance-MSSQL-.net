using System.Net;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Integration.Tests.Policies;
using static Insurance.Web.Tests.Policies.PolicyWebApp;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// Seeing and authorising are two rights (legacy precision-fd-panel-chain.test.ts, its last case): a new exact receipt's
/// reversal request on the financial director's panel over HTTP — finance.panel.view lets one see it and not approve it;
/// payment.large.authorize alone shows no panel (nothing financial read) yet approves on the card; a personal removal; a
/// group grant. The receipt, the second policy's advance and the request are written by the real actions today.
/// </summary>
public sealed class GapsFdPanelRightsTests(PolicyWebApp app) : IClassFixture<PolicyWebApp>
{
    private World W => app.W;

    [Fact]
    public async Task View_only_sees_and_cannot_approve_authorise_only_sees_no_panel_yet_approves_a_personal_removal_a_group_grant()
    {
        var admin = await W.UserAsync();
        var accountant = await W.UserAsync("accountant");
        var holder = await W.ClientAsync();
        var a = await W.PendingPolicyAsync(holder, Gaps.MonthStart, premium: 10000m, schedule: [(0, 10000m)]);
        var b = await W.PendingPolicyAsync(holder, Gaps.MonthStart, premium: 0.01m, schedule: [(Gaps.MonthStart.AddMonths(1).DayNumber - Gaps.MonthStart.DayNumber, 0.01m)]);
        foreach (var p in new[] { a, b }) Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(W.App, admin, p.Id.ToString())).Outcome);
        Assert.Equal($"/policies/{a.Id}?paid=1", (await W.PayAsync(accountant, a.Id, "10000.01", World.Today, reference: "R-P")).Redirect);
        var r = await W.ScalarAsync<Guid>("select id from core.payment where reference = N'R-P'");
        Assert.Equal($"/policies/{b.Id}?advance=allocated&gel=1&left=0", await PaymentActions.AllocateReceiptAsync(W.App, W.Rates, accountant, b.Id.ToString(), r.ToString()));
        Assert.Equal($"/policies/{a.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(W.App, accountant, r.ToString(), a.Id.ToString(), "შეცდომით ჩაირიცხა P"));
        var q = await W.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @r", new { r });
        int Mirrors() => W.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @r", new { r }).GetAwaiter().GetResult();
        var approve = new Dictionary<string, string> { ["policy_id"] = a.Id.ToString(), ["request_id"] = q.ToString() };

        // The viewer, given finance.panel.view personally: sees the request; its forged approval is refused, nothing written.
        var (viewer, viewerId) = await app.SignedInAsync("viewer");
        await Gaps.DecideAsync(W, viewerId, "finance.panel.view", "allow");
        var panel = await HtmlAsync(viewer, "/finance/panel");
        Assert.Contains(q.ToString(), panel);
        Assert.Contains("10,000.01", panel);
        Assert.Equal($"/policies/{a.Id}?reversal=not-allowed", Location(await WebApp.PostFormAsync(viewer, $"/policies/{a.Id}?handler=ApproveReversalRequest", approve, $"/policies/{a.Id}")));
        Assert.Equal(0, Mirrors());

        // The admin's own removal of the view: the panel says so and shows nothing financial.
        var (adminClient, adminId) = await app.SignedInAsync("admin");
        await Gaps.DecideAsync(W, adminId, "finance.panel.view", "deny");
        var refused = await HtmlAsync(adminClient, "/finance/panel");
        Assert.Contains(FdPanelTexts.NotAllowed, refused);
        Assert.DoesNotContain("data-queue", refused);
        Assert.DoesNotContain(q.ToString(), refused);

        // The underwriter, given payment.large.authorize by a group: no panel, yet it approves on the card.
        var (underwriter, underwriterId) = await app.SignedInAsync("underwriter");
        var group = await Gaps.GroupAsync(W, "payment.large.authorize", underwriterId);
        var none = await HtmlAsync(underwriter, "/finance/panel");
        Assert.Contains(FdPanelTexts.NotAllowed, none);
        Assert.DoesNotContain("data-queue", none);
        // A group grant of the view: the underwriter sees it, approves it on the card, and it is gone.
        await W.QueryAsync<int>("insert core.user_group_permission (group_id, permission) values (@group, N'finance.panel.view'); select 1", new { group });
        Assert.Contains(q.ToString(), await HtmlAsync(underwriter, "/finance/panel"));
        Assert.Equal($"/policies/{a.Id}?reversal=approved", Location(await WebApp.PostFormAsync(underwriter, $"/policies/{a.Id}?handler=ApproveReversalRequest", approve, $"/policies/{a.Id}")));
        Assert.Equal(1, Mirrors());
        Assert.Equal(underwriterId, await W.ScalarAsync<Guid>("select created_by from core.payment where reverses_payment_id = @r", new { r }));
        Assert.DoesNotContain(q.ToString(), await HtmlAsync(underwriter, "/finance/panel"));
        Assert.Equal((10000m, 0.01m), (await W.OwedAsync(a.Id), await W.OwedAsync(b.Id)));
    }
}
