using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/integration/policy-approve-permission.test.ts and scripts/approval-answers-regression.ts: who may
/// approve (policy.approve — the head of operations and the admin by role, anyone else by a group or a personal grant, a
/// personal removal takes it away), no one but the admin approves their own policy (docs/05 ვ4, ვ8), the money once, the
/// answer of every press; and what an approval writes (premium rows, reinsurance cessions, the closed-month rule).
/// </summary>
public sealed class PolicyApprovalTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 2, 1);

    private Task<PolicyActions.ApproveAnswer> Approve(CurrentUser user, Guid id) => PolicyActions.ApprovePolicyAsync(w.App, user, id.ToString());

    [Theory]
    [InlineData("operations_head", "done")]
    [InlineData("admin", "done")]
    [InlineData("sales", "forbidden")]
    [InlineData("underwriter", "forbidden")]
    [InlineData("accountant", "forbidden")]
    [InlineData("claims", "forbidden")]
    [InlineData("viewer", "forbidden")]
    public async Task The_roles_starting_set(string role, string outcome)
    {
        var user = await w.UserAsync(role);
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), Start);
        var answer = await Approve(user, p.Id);
        Assert.Equal(outcome, answer.Outcome);
        Assert.Equal(outcome == "forbidden" ? "" : p.Number, answer.Number);
        Assert.Equal(outcome == "done" ? 1 : 0, (await w.PremiumRowsAsync(p.Id)).Count);
        Assert.Equal(outcome == "done" ? "active" : "pending_approval", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = p.Id }));
        if (outcome == "done")
            Assert.Equal(user.Id, await w.ScalarAsync<Guid>("select approved_by from core.policy where id = @id", new { id = p.Id }));
    }

    [Fact]
    public async Task Groups_personal_grants_and_removals()
    {
        var admin = await w.UserAsync();
        var viewer = await w.UserAsync("viewer");
        Assert.True((await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, viewer.Id, "policy.approve", "allow"))).Ok);
        Assert.Equal("done", (await Approve(viewer, (await w.PendingPolicyAsync(await w.ClientAsync(), Start)).Id)).Outcome);

        var sales = await w.UserAsync("sales");
        var group = (await w.App.AsUserAsync(admin.Id, tx => PermissionStore.CreateGroupAsync(tx, "დამმტკიცებლები " + Guid.NewGuid().ToString("N")[..6]))).Id!.Value;
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetGroupGrantAsync(tx, group, "policy.approve", true));
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetGroupMemberAsync(tx, group, sales.Id, true));
        Assert.Equal("done", (await Approve(sales, (await w.PendingPolicyAsync(await w.ClientAsync(), Start)).Id)).Outcome);
        // A personal removal beats the group.
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, sales.Id, "policy.approve", "deny"));
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), Start);
        Assert.Equal("forbidden", (await Approve(sales, p.Id)).Outcome);
        // The head of operations' own removal takes the role's; inherit gives it back.
        var head = await w.UserAsync("operations_head");
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, head.Id, "policy.approve", "deny"));
        Assert.Equal("forbidden", (await Approve(head, p.Id)).Outcome);
        Assert.Empty(await w.PremiumRowsAsync(p.Id));
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, head.Id, "policy.approve", "inherit"));
        Assert.Equal("done", (await Approve(head, p.Id)).Outcome);
    }

    [Fact]
    public async Task No_one_but_the_admin_approves_a_policy_they_created()
    {
        var head = await w.UserAsync("operations_head");
        var own = await w.PendingPolicyAsync(await w.ClientAsync(), Start, createdBy: head.Id);
        var answer = await Approve(head, own.Id);
        Assert.Equal(("own", own.Number), (answer.Outcome, answer.Number));
        Assert.Equal("საკუთარი გაფორმებული პოლისის დამტკიცება არ შეიძლება; დაამტკიცებს სხვა თანამშრომელი — არაფერი შეცვლილა", PolicyActions.AnswerText("own"));
        Assert.Empty(await w.PremiumRowsAsync(own.Id));
        // Another user given the permission personally approves it.
        var admin = await w.UserAsync();
        var underwriter = await w.UserAsync("underwriter");
        await w.App.AsUserAsync(admin.Id, tx => PermissionStore.SetUserDecisionAsync(tx, underwriter.Id, "policy.approve", "allow"));
        Assert.Equal("done", (await Approve(underwriter, own.Id)).Outcome);
        // The underwriter's own policy, given the permission: refused; the head approves it.
        var mine = await w.PendingPolicyAsync(await w.ClientAsync(), Start, createdBy: underwriter.Id);
        Assert.Equal("own", (await Approve(underwriter, mine.Id)).Outcome);
        Assert.Equal("done", (await Approve(head, mine.Id)).Outcome);
        // The admin's own: the admin may approve it (owner, 16.10.2026; docs/05 ვ8).
        var adminOwn = await w.PendingPolicyAsync(await w.ClientAsync(), Start, createdBy: admin.Id);
        Assert.Equal("done", (await Approve(admin, adminOwn.Id)).Outcome);
    }

    [Fact]
    public async Task Every_press_gets_an_answer_and_the_money_is_written_once()
    {
        var head = await w.UserAsync("operations_head");
        // Two lines, 300.00 and 100.00, four instalments of 100.00.
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), Start, premium: 300m, schedule: [(0, 100m), (91, 100m), (182, 100m), (273, 100m)]);
        await w.QueryAsync<int>("insert core.policy_line (policy_id, class_code, premium, premium_gel) values (@id, N'5.1', 100, 100); select 1", new { id = p.Id });
        Assert.Equal(("done", p.Number), (await Approve(head, p.Id)) is var a ? (a.Outcome, a.Number) : default);
        Assert.Equal(PolicyTexts.ApproveOutcomes["done"], PolicyActions.AnswerText("done"));
        var rows = await w.PremiumRowsAsync(p.Id);
        Assert.Equal([100m, 300m], rows.Select(r => r.Amount).Order());
        Assert.All(rows, r => Assert.Equal((Start, Start, Start.AddDays(365), "issue"), (r.BookedOn, r.CoverFrom, r.CoverTo, r.Kind)));
        Assert.Equal(400m, await w.ScheduledAsync(p.Id));
        var again = await Approve(head, p.Id);
        Assert.Equal(("already", p.Number), (again.Outcome, again.Number));
        Assert.Equal(2, (await w.PremiumRowsAsync(p.Id)).Count);
        Assert.Equal(("invalid", ""), (await Approve(head, Guid.NewGuid())) is var b ? (b.Outcome, b.Number) : default);
        Assert.Equal("invalid", (await PolicyActions.ApprovePolicyAsync(w.App, head, "")).Outcome);
        Assert.Equal("invalid", (await PolicyActions.ApprovePolicyAsync(w.App, head, "1 or 1=1")).Outcome);
        Assert.Null(PolicyActions.AnswerText("__proto__"));
    }

    [Fact]
    public async Task A_suspended_client_is_not_approved()
    {
        var head = await w.UserAsync("operations_head");
        var holder = await w.ClientAsync();
        var p = await w.PendingPolicyAsync(holder, Start);
        var s = await w.SuspendAsync(holder, head.Id);
        Assert.Equal(("suspended", p.Number), (await Approve(head, p.Id)) is var a ? (a.Outcome, a.Number) : default);
        Assert.Equal(AmlTexts.Suspension.Blocked, PolicyActions.AnswerText("suspended"));
        Assert.Empty(await w.PremiumRowsAsync(p.Id));
        // A suspension of another policy of the client (a transaction suspension) does not stop this one.
        await w.LiftAsync(s);
        var other = await w.PendingPolicyAsync(holder, Start);
        await w.SuspendAsync(holder, head.Id, policyId: other.Id);
        Assert.Equal("done", (await Approve(head, p.Id)).Outcome);
        Assert.Equal("suspended", (await Approve(head, other.Id)).Outcome);
    }

    [Fact]
    public async Task Approval_cedes_to_the_obligatory_treaties_and_an_over_ceded_plan_rolls_the_whole_approval_back()
    {
        var head = await w.UserAsync("operations_head");
        // Class 9 (an unused class) so this test's treaties touch no other test's policies.
        var quota = await w.TreatyAsync("quota_share", 60m, 10m, classCode: "9");
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), Start);
        await w.QueryAsync<int>("update core.policy_line set class_code = N'9' where policy_id = @id; select 1", new { id = p.Id });
        Assert.Equal("done", (await Approve(head, p.Id)).Outcome);
        var cession = Assert.Single(await w.QueryAsync<(Guid Treaty, decimal Share, decimal Amount, decimal AmountGel, decimal CommissionGel)>("""
            select c.treaty_id, c.share_percent, c.amount, c.amount_gel, c.commission_gel from core.premium_cession c
            join core.premium_transaction t on t.id = c.premium_transaction_id join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id
            """, new { id = p.Id }));
        Assert.Equal((quota, 60m, 219m, 219m, 21.9m), cession);

        // A second quota of 60 % on the same class would cede 120 %: refused, nothing of the approval stays (H08).
        await w.TreatyAsync("quota_share", 60m, classCode: "9");
        var q = await w.PendingPolicyAsync(await w.ClientAsync(), Start);
        await w.QueryAsync<int>("update core.policy_line set class_code = N'9' where policy_id = @id; select 1", new { id = q.Id });
        var answer = await Approve(head, q.Id);
        Assert.Equal("ri-over-ceded", answer.Outcome);
        Assert.Equal(ReinsuranceTexts.Problems["over-ceded"], PolicyActions.AnswerText(answer.Outcome));
        Assert.Empty(await w.PremiumRowsAsync(q.Id));
        Assert.Equal("pending_approval", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = q.Id }));
    }
}

/// <summary>An approval after the policy's month was closed books the premium on the first open day (docs/04 r14 as the code has it).</summary>
public sealed class ApprovalClosedMonthTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    [Fact]
    public async Task A_late_approval_is_booked_on_the_first_open_day_and_the_closed_month_is_not_changed()
    {
        var head = await w.UserAsync("operations_head");
        var october = new DateOnly(2025, 10, 1);
        var holder = await w.ClientAsync();
        // Q: 120.00 from 01.10, approved and paid before October is closed.
        var q = await w.ActivePolicyAsync(head, holder, october, premium: 120m);
        await w.PayAsync(head, q.Id, "120.00", october.AddDays(5));
        var p = await w.PendingPolicyAsync(holder, october.AddDays(10), premium: 400m, schedule: [(0, 100m), (91, 100m), (182, 100m), (273, 100m)]);
        await w.CloseMonthAsync(october, head.Id);
        var reportBefore = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, "2025-10"));
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, head, p.Id.ToString())).Outcome);
        var row = Assert.Single(await w.PremiumRowsAsync(p.Id));
        Assert.Equal((400m, new DateOnly(2025, 11, 1), october.AddDays(10)), (row.Amount, row.BookedOn, row.CoverFrom));
        var reportAfter = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, "2025-10"));
        Assert.Equal(reportBefore.Totals, reportAfter.Totals);
        Assert.Equal(reportBefore.Receivables, reportAfter.Receivables);
        // November: P's 400.00 written there (financial), statistically October's (its cover starts there).
        var november = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, "2025-11"));
        Assert.Equal(40000, (long)november.Totals.FinancialWrittenGel);
        Assert.Equal(0, (long)november.Totals.StatisticalWrittenGel);
        // Earned in November: nothing of P is earned before it is booked, so November takes its 51 days from 11.10 at
        // once — 400 × 51/365 = 55.89 — and UPR at its end 400 − 55.89 = 344.11; Q 120 × 30/365 = 9.86, UPR 120 − 120 × 61/365 = 99.95.
        Assert.Equal(5589 + 986, (long)november.Totals.EarnedGel);
        Assert.Equal(34411 + 9995, (long)november.Totals.UnearnedGel);
    }
}
