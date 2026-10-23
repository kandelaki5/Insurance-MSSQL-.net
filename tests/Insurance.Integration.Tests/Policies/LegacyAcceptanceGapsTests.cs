using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/acceptance-gaps-regression.ts (Codex H39, the autograph acceptance rows that had only been read in the
/// code): the application's own actions on a real database as core_app. Same scenarios and numbers: a 365.00 policy over
/// 365 days in 4 × 91.25 from its first day, a receipt of 91.25 on day 5, a cancellation on day 182 (181 days kept).
/// AG-P5 closes a month, so it has its own database (<see cref="LegacyAcceptanceGapsClosedMonthTests"/>).
/// </summary>
public sealed class LegacyAcceptanceGapsTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly Paid = Start.AddDays(4);
    private static readonly DateOnly Cancel = Start.AddDays(181);

    private Task<decimal> LedgerAsync(Guid policyId) => w.ScalarAsync<decimal>("""
        select coalesce(sum(t.amount), 0) from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @policyId
        """, new { policyId });

    [Fact]
    public async Task AG_C2_payment_and_cancellation_in_either_order_keep_181_and_leave_89_75_owed()
    {
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var first = await w.ActivePolicyAsync(admin, holder, Start);
        var second = await w.ActivePolicyAsync(admin, holder, Start);
        // pay → cancel
        var f1 = await w.PayAsync(admin, first.Id, "91.25", Paid, reference: "C2 pay");
        var f2 = await w.CancelAsync(admin, first.Id, Cancel, "C2 cancel");
        // cancel → pay
        var s1 = await w.CancelAsync(admin, second.Id, Cancel, "C2 cancel");
        var s2 = await w.PayAsync(admin, second.Id, "91.25", Paid, reference: "C2 pay");
        Assert.All(new[] { f1, f2, s1, s2 }, a => Assert.NotNull(a.Redirect));
        foreach (var p in new[] { first, second })
            Assert.Equal((181m, 181m, 91.25m, 89.75m), (await LedgerAsync(p.Id), await w.ScheduledAsync(p.Id), await w.PaidAsync(p.Id), await w.OwedAsync(p.Id)));
    }

    [Fact]
    public async Task AG_C4_a_claim_on_the_cancelled_policy_only_for_a_loss_before_the_cancellation_day()
    {
        var admin = await w.UserAsync();
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        Assert.NotNull((await w.PayAsync(admin, p.Id, "91.25", Paid)).Redirect);
        Assert.NotNull((await w.CancelAsync(admin, p.Id, Cancel)).Redirect);
        var line = await w.ScalarAsync<Guid>("select id from core.policy_line where policy_id = @id", new { id = p.Id });
        Task<FormOutcome> Claim(DateOnly occurred) => ClaimActions.RegisterClaimAsync(w.App, w.Rates, admin, ClaimWorld.Form(("policy_number", p.Number),
            ("occurred_on", ClaimWorld.D(occurred)), ("reported_on", ClaimWorld.D(occurred)), ("description", "AG-C4"), ($"line:{line}", "on"), ($"reserve:{line}", "100")));
        var before = await Claim(Cancel.AddDays(-1));
        var onDay = await Claim(Cancel);
        var after = await Claim(Cancel.AddDays(1));
        Assert.NotNull(before.RedirectTo);
        Assert.Null(onDay.RedirectTo);
        Assert.Contains("occurred_on", onDay.Errors.Keys);
        Assert.Null(after.RedirectTo);
        Assert.Contains("occurred_on", after.Errors.Keys);
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.claim where policy_id = @id", new { id = p.Id }));
    }

    [Fact]
    public async Task AG_P6_an_approval_during_an_FMS_suspension_is_refused_and_books_nothing_and_after_the_lift_goes_through()
    {
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var blocked = await w.PendingPolicyAsync(holder, Start);
        var suspension = await w.SuspendAsync(holder, admin.Id);
        var refused = await PolicyActions.ApprovePolicyAsync(w.App, admin, blocked.Id.ToString());
        Assert.Equal(("suspended", "pending_approval", 0), (refused.Outcome, await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = blocked.Id }), (await w.PremiumRowsAsync(blocked.Id)).Count));
        await w.LiftAsync(suspension);
        var allowed = await PolicyActions.ApprovePolicyAsync(w.App, admin, blocked.Id.ToString());
        Assert.Equal(("done", "active", 1), (allowed.Outcome, await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = blocked.Id }), (await w.PremiumRowsAsync(blocked.Id)).Count));
    }
}

/// <summary>AG-P5 of scripts/acceptance-gaps-regression.ts: its own database, since a month is closed (never reopened).</summary>
public sealed class LegacyAcceptanceGapsClosedMonthTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    /// <summary>
    /// Acceptance P5 under the rule of the old program's PR #212 (owner, 13.10.2026): a policy starting in a closed month is not
    /// approved now — it used to be booked on the first open day with its cession — and nothing is written: no premium, no cession.
    /// </summary>
    [Fact]
    public async Task AG_P5_a_policy_starting_in_a_closed_month_is_not_approved_and_cedes_nothing()
    {
        var admin = await w.UserAsync();
        var lastClosed = new DateOnly(2025, 6, 1);
        var start = lastClosed.AddMonths(1);
        // An obligatory treaty on the class: an approval would cede.
        await w.TreatyAsync("quota_share", 60m);
        var late = await w.PendingPolicyAsync(await w.ClientAsync(), lastClosed, schedule: [(start.DayNumber - lastClosed.DayNumber, 365m)]);
        await w.CloseMonthAsync(lastClosed, admin.Id);
        Assert.Equal("policy-period-closed", (await PolicyActions.ApprovePolicyAsync(w.App, admin, late.Id.ToString())).Outcome);
        Assert.Empty(await w.PremiumRowsAsync(late.Id));
        Assert.Equal(0, await w.ScalarAsync<int>("""
            select count(*) from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
            join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id
            """, new { id = late.Id }));
        // Approved on its first open day it is in force, with its cession booked on its own date.
        var onTime = await w.PendingPolicyAsync(await w.ClientAsync(), start);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, onTime.Id.ToString())).Outcome);
        Assert.Equal(start, Assert.Single(await w.PremiumRowsAsync(onTime.Id)).BookedOn);
    }
}
