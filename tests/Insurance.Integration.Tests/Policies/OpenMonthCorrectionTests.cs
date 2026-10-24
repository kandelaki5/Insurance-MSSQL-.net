using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The old program's PRs #207/#218/#225 on a real database (they never ran on one: their tests were in-memory), with the
/// figures their tests worked out by hand (scripts/integration/demo-paid-reduction-approval.test.ts at #225's head, "L225:n").
/// The change world: an approved 400.00 GEL original from 01.10.2026 to 01.10.2027, 4 × 100.00 due 01.10, 01.11, 01.12.2026
/// and 01.01.2027, 100.00 paid on 05.10; a change dated 07.10 approved while October is open corrects the original (owner,
/// 13.10.2026): October counts one policy at the corrected premium.
/// </summary>
public sealed class CorrectionWorld(TestDatabase db)
{
    public readonly World w = new(db);
    public static readonly DateOnly October = new(2026, 10, 1);

    public async Task<(CurrentUser User, World.Policy Original)> ChangeWorldAsync()
    {
        var user = await w.UserAsync();
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), October, premium: 400m, schedule: [(0, 100m), (31, 100m), (61, 100m), (92, 100m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, p.Id.ToString())).Outcome);
        Assert.NotNull((await w.PayAsync(user, p.Id, "100.00", October.AddDays(4))).Redirect);
        return (user, p);
    }

    public async Task<(Guid Id, PolicyActions.ApproveAnswer Answer)> ChangeAsync(CurrentUser user, World.Policy original, string delta)
    {
        Assert.NotNull((await w.EndorseAsync(user, original.Id, October.AddDays(6), delta)).Redirect);
        var id = await w.ScalarAsync<Guid>("select id from core.policy where replaces_policy_id = @id and status = N'pending_approval'", new { id = original.Id });
        return (id, await PolicyActions.ApprovePolicyAsync(w.App, user, id.ToString()));
    }

    /// <summary>October's report: each test has its own database, so only its own policies count.</summary>
    public async Task<(long Policies, long Statistical, long Financial, long Earned, long Unearned, long Outstanding, long Overdue)> OctoberAsync()
    {
        var report = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, "2026-10"));
        var r = report.Receivables.Single(x => x.Currency == "GEL");
        return ((long)report.Totals.PoliciesIssued, (long)report.Totals.StatisticalWrittenGel, (long)report.Totals.FinancialWrittenGel, (long)report.Totals.EarnedGel,
            (long)report.Totals.UnearnedGel, (long)r.Outstanding, (long)r.Overdue);
    }
}

public sealed class OpenMonthCorrectionReductionTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly CorrectionWorld cw = new(db);
    private World w => cw.w;

    [Fact]
    public async Task L225_1775_2201_minus_80_corrects_the_original_to_4_x_80_and_October_counts_one_policy_at_320()
    {
        var (user, o) = await cw.ChangeWorldAsync();
        var (change, answer) = await cw.ChangeAsync(user, o, "-80");
        Assert.Equal(("done", $"{o.Number}/შესწორება-1"), (answer.Outcome, answer.Number));
        Assert.Equal("80.00/80.00/80.00/80.00", await w.ScheduleTextAsync(o.Id));
        var correction = Assert.Single(await w.PremiumRowsAsync(o.Id), r => r.Amount < 0);
        Assert.Equal((-80m, CorrectionWorld.October, CorrectionWorld.October, CorrectionWorld.October.AddYears(1)), (correction.Amount, correction.BookedOn, correction.CoverFrom, correction.CoverTo));
        Assert.Empty(await w.PremiumRowsAsync(change));
        // By hand (L225:2201): one policy, 320.00 both ways; earned 32000 × 31/365 = 2718, UPR 29282; owed 32000 − 10000 = 22000.
        Assert.Equal((1L, 32000L, 32000L, 2718L, 29282L, 22000L, 0L), await cw.OctoberAsync());
        // A second press writes nothing.
        Assert.Equal("already", (await PolicyActions.ApprovePolicyAsync(w.App, user, change.ToString())).Outcome);
        Assert.Equal("80.00/80.00/80.00/80.00", await w.ScheduleTextAsync(o.Id));
    }
}

public sealed class OpenMonthCorrectionIncreaseTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly CorrectionWorld cw = new(db);
    private World w => cw.w;

    [Fact]
    public async Task L225_3105_plus_100_corrects_the_original_to_4_x_125_the_first_25_short_and_overdue()
    {
        var (user, o) = await cw.ChangeWorldAsync();
        var (change, answer) = await cw.ChangeAsync(user, o, "100");
        Assert.Equal(("done", $"{o.Number}/შესწორება-1"), (answer.Outcome, answer.Number));
        Assert.Equal("125.00/125.00/125.00/125.00", await w.ScheduleTextAsync(o.Id));
        Assert.Equal(0m, await w.ScheduledAsync(change));
        // +25.00 on each of the original's instalments and −100.00 on the change's own, booked 01.10 with the correction's reason.
        Assert.Equal(["-100.00", "25.00", "25.00", "25.00", "25.00"], (await w.QueryAsync<(decimal Change, DateOnly BookedOn, string Reason)>("""
            select j.change, j.booked_on, j.reason from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id in (@o, @c)
            """, new { o = o.Id, c = change })).Select(a =>
            {
                Assert.Equal((CorrectionWorld.October, $"{PolicyTexts.Endorse.CorrectionReason} {o.Number}/შესწორება-1"), (a.BookedOn, a.Reason));
                return a.Change.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }).Order(StringComparer.Ordinal));
        // By hand (L225:3105): 500.00 both ways; earned 3397 + round(10000 × 31/365) = 4246, UPR 45754; owed 40000, the first
        // instalment 125.00 with 100.00 paid overdue 2500.
        Assert.Equal((1L, 50000L, 50000L, 4246L, 45754L, 40000L, 2500L), await cw.OctoberAsync());
    }
}

public sealed class OpenMonthCorrectionZeroThenUpTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly CorrectionWorld cw = new(db);
    private World w => cw.w;

    [Fact]
    public async Task L225_3174_corrected_to_nothing_then_up_again_is_4_x_25_by_the_first_shares()
    {
        var (user, o) = await cw.ChangeWorldAsync();
        Assert.Equal(("done", $"{o.Number}/შესწორება-1"), ((await cw.ChangeAsync(user, o, "-400")).Answer is var a ? (a.Outcome, a.Number) : default));
        Assert.Equal("0.00/0.00/0.00/0.00", await w.ScheduleTextAsync(o.Id));
        Assert.Equal(("done", $"{o.Number}/შესწორება-2"), ((await cw.ChangeAsync(user, o, "100")).Answer is var b ? (b.Outcome, b.Number) : default));
        Assert.Equal("25.00/25.00/25.00/25.00", await w.ScheduleTextAsync(o.Id));
        // Amount − paid: −75.00 (the client's credit), then 25.00 each. October: 100.00, earned 849, UPR 9151, owed nothing.
        Assert.Equal([-75m, 25m, 25m, 25m], await w.QueryAsync<decimal>("""
            select i.amount + coalesce((select sum(j.change) from core.installment_adjustment j where j.installment_id = i.id), 0)
                   - coalesce((select sum(a.amount) from core.payment_allocation a where a.installment_id = i.id), 0)
            from core.installment i where i.policy_id = @id order by i.due_on
            """, new { id = o.Id }));
        Assert.Equal((1L, 10000L, 10000L, 849L, 9151L, 0L, 0L), await cw.OctoberAsync());
    }
}

public sealed class OpenMonthCorrectionOwnPolicyTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly CorrectionWorld cw = new(db);
    private World w => cw.w;

    [Fact]
    public async Task A_change_with_a_coverage_the_original_lacks_or_in_another_currency_stands_as_its_own_policy()
    {
        // Every line of the change must have its one line of the same coverage on the original (legacy openMonthCorrection);
        // the correction is GEL-only (the owner's FX answer is not implemented in the old program either: an open question).
        var (user, o) = await cw.ChangeWorldAsync();
        Assert.NotNull((await w.EndorseAsync(user, o.Id, CorrectionWorld.October.AddDays(6), "50", coverage: "theft")).Redirect);
        var own = await w.ScalarAsync<Guid>("select id from core.policy where replaces_policy_id = @id and status = N'pending_approval'", new { id = o.Id });
        var answer = await PolicyActions.ApprovePolicyAsync(w.App, user, own.ToString());
        Assert.Equal(("done", $"{o.Number}/1"), (answer.Outcome, answer.Number));
        Assert.Equal(50m, Assert.Single(await w.PremiumRowsAsync(own)).Amount);
        Assert.Equal("100.00/100.00/100.00/100.00", await w.ScheduleTextAsync(o.Id));
    }
}

/// <summary>
/// L225:2419: the original's own month closed before the approval — the change stands as a policy of its own, ROOT/1, on its own
/// date (owner, 13.10.2026: "წინა პერიოდს არ ვცვლით"). The September world: an approved 400.00 original from 01.09.2026, 4 × 100.00
/// due 01.10 … 01.01, nothing paid; a change of −80.00 dated 02.10; September closed; approved in the open October.
/// </summary>
public sealed class OpenMonthCorrectionClosedOriginTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    [Fact]
    public async Task L225_2419_the_originals_month_closed_the_change_is_its_own_policy_and_October_counts_minus_80()
    {
        var user = await w.UserAsync();
        var september = new DateOnly(2026, 9, 1);
        var o = await w.PendingPolicyAsync(await w.ClientAsync(), september, premium: 400m, schedule: [(30, 100m), (61, 100m), (91, 100m), (122, 100m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, o.Id.ToString())).Outcome);
        Assert.NotNull((await w.EndorseAsync(user, o.Id, new DateOnly(2026, 10, 2), "-80")).Redirect);
        await w.CloseMonthAsync(september, user.Id);
        var change = await w.ScalarAsync<Guid>("select id from core.policy where replaces_policy_id = @id", new { id = o.Id });
        var answer = await PolicyActions.ApprovePolicyAsync(w.App, user, change.ToString());
        Assert.Equal(("done", $"{o.Number}/1"), (answer.Outcome, answer.Number));
        var row = Assert.Single(await w.PremiumRowsAsync(change));
        Assert.Equal((-80m, new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 2)), (row.Amount, row.BookedOn, row.CoverFrom));
        var report = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, "2026-10"));
        // By hand (L225:2419): written −8000; earned 3397 − round(8000 × 30/334) = 3397 − 719 = 2678; UPR 33315 − 7281 = 26034;
        // owed 32000, the first 100.00 overdue (the reduction came off the last instalment, the company's default order).
        Assert.Equal((-8000L, -8000L, 2678L, 26034L), ((long)report.Totals.StatisticalWrittenGel, (long)report.Totals.FinancialWrittenGel, (long)report.Totals.EarnedGel, (long)report.Totals.UnearnedGel));
        var receivable = report.Receivables.Single(r => r.Currency == "GEL");
        Assert.Equal((32000L, 10000L), ((long)receivable.Outstanding, (long)receivable.Overdue));
    }
}
