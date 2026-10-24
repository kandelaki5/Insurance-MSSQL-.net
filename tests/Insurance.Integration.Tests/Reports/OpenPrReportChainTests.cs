using Insurance.Application.Services;
using Insurance.Integration.Tests.Policies;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The old program's open PRs #198, #200 and #201 (scripts/integration/demo-closed-claim-report.test.ts,
/// demo-reserve-writer-report.test.ts, demo-payment-writer-report.test.ts on the stacked chain off old main 5cea59d): the real
/// claim actions and the real monthly report on one database. The legacy tests ran them over an in-memory store whose answers
/// they wrote by hand; here the actions write the rows and PremiumReport reads them back on SQL Server. The demo's world: an
/// active GEL CASCO policy from 01.10.2026, damage 300.00 and theft 100.00 (class 5.1), four instalments of 100.00; one loss
/// event registered 07.10 with the damage claim reserved at 1 000.00 and the theft claim at 500.00; the damage claim approved
/// for 1 000.00. Worked by hand (tetri), as the legacy headers: IBNR is 5% of the 400.00 written in 2026 = 2000.
/// </summary>
public sealed class ClaimReportChain(TestDatabase db)
{
    public readonly ClaimWorld W = new(db);
    public static readonly DateOnly October = new(2026, 10, 1);

    public async Task<(CurrentUser Claims, Guid Damage, Guid Theft)> WorldAsync()
    {
        db.Clock.SetToday(new DateOnly(2026, 10, 7));
        var claims = await W.UserAsync("claims");
        var policy = await W.PolicyAsync(claims.Id, October,
            [new ClaimWorld.Line("damage", Premium: 300m, Deductible: 0m), new ClaimWorld.Line("theft", Premium: 100m, Deductible: 0m)],
            [(October, 100m), (new DateOnly(2027, 1, 1), 100m), (new DateOnly(2027, 4, 1), 100m), (new DateOnly(2027, 7, 1), 100m)]);
        var ids = await W.RegisterAsync(claims, policy, new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7), ("damage", "1000"), ("theft", "500"));
        await W.ApproveAsync(claims, ids[0], "1000", new DateOnly(2026, 10, 7));
        return (claims, ids[0], ids[1]);
    }

    /// <summary>The month's claims totals: paid, the reserve booked up to its end (RBNS), IBNR, in tetri.</summary>
    public async Task<(long Paid, long Rbns, long Ibnr)> MonthAsync(string month)
    {
        var r = await W.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, W.Rates, month));
        return ((long)r.ClaimsTotals.PaidGel, (long)r.ClaimsTotals.RbnsGel, (long)r.ClaimsTotals.IbnrGel);
    }

    /// <summary>The accountant pays (claim.payment.record) on 08.10.2026, to the policyholder, nothing withheld.</summary>
    public async Task PayAsync(Guid claim, string amount)
    {
        db.Clock.SetToday(new DateOnly(2026, 10, 8));
        var paid = await W.PayAsync(await W.UserAsync("accountant"), claim, amount, new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 8));
        Assert.True(paid.RedirectTo is not null, $"{paid.Message} {string.Join("; ", paid.Errors.Select(e => $"{e.Key}={e.Value}"))}");
    }
}

public sealed class OpenPr201PaymentReportTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimReportChain x = new(db);

    [Fact]
    public async Task PR201_paying_100_of_the_damage_claim_on_08_10_moves_October_to_paid_10000_reserve_140000_and_leaves_November_unpaid()
    {
        var (claims, damage, _) = await x.WorldAsync();
        // Before: nothing paid; reserve 1 000.00 + 500.00.
        Assert.Equal((0L, 150000L, 2000L), await x.MonthAsync("2026-10"));
        await x.PayAsync(damage, "100.00");
        // The payment and the reserve down by the gross on its day, written by the action itself.
        Assert.Equal((10000L, 140000L, 2000L), await x.MonthAsync("2026-10"));
        // November: October's payment is not paid again; the reserve stands.
        Assert.Equal((0L, 140000L, 2000L), await x.MonthAsync("2026-11"));
    }
}

public sealed class OpenPr198ClosedClaimReportTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimReportChain x = new(db);

    [Fact]
    public async Task PR198_the_damage_claim_paid_1000_then_closed_gives_the_same_reports_paid_100000_reserve_50000_then_nothing_paid()
    {
        var (claims, damage, _) = await x.WorldAsync();
        await x.PayAsync(damage, "100.00");
        await x.PayAsync(damage, "900.00");
        Assert.Equal("paid", await x.W.ScalarAsync<string>("select status from core.claim where id = @damage", new { damage }));
        var paid = (await x.MonthAsync("2026-09"), await x.MonthAsync("2026-10"), await x.MonthAsync("2026-11"));
        // September: nothing written, paid or reserved yet. October: 1 000.00 paid; reserve 1 000 − 100 − 900 + 500 = 500.00.
        Assert.Equal(((0L, 0L, 0L), (100000L, 50000L, 2000L), (0L, 50000L, 2000L)), paid);

        // Closed on 09.10: the report reads the history, not the claim's status today — the same three months.
        x.W.Database.Clock.SetToday(new DateOnly(2026, 10, 9));
        await ClaimActions.DecideClaimAsync(x.W.App, claims, ClaimWorld.Form(("claim_id", damage.ToString()), ("decision", "close")), new DateOnly(2026, 10, 9));
        Assert.Equal("closed", await x.W.ScalarAsync<string>("select status from core.claim where id = @damage", new { damage }));
        Assert.Equal(paid, (await x.MonthAsync("2026-09"), await x.MonthAsync("2026-10"), await x.MonthAsync("2026-11")));
    }
}

/// <summary>
/// PR #200 under the baseline rule: legacy changeReserve booked the movement at once; since migration 0035 (owner, 17.10.2026,
/// docs/05 ვ8) a change is a request that moves the reserve only when someone else approves it, on the request's day.
/// </summary>
public sealed class OpenPr200ReserveReportTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimReportChain x = new(db);

    [Fact]
    public async Task PR200_the_theft_reserve_moved_from_500_to_700_on_10_11_counts_in_November_not_October_and_the_same_form_again_adds_nothing()
    {
        var (claims, damage, theft) = await x.WorldAsync();
        await x.PayAsync(damage, "100.00");
        await x.PayAsync(damage, "900.00");
        Assert.Equal((100000L, 50000L, 2000L), await x.MonthAsync("2026-10"));

        var november = new DateOnly(2026, 11, 10);
        x.W.Database.Clock.SetToday(november);
        var form = ClaimWorld.Form(("claim_id", theft.ToString()), ("booked_on", ClaimWorld.D(november)), ("new_level", "700"), ("reason", "ხარჯთაღრიცხვა გაიზარდა"));
        Assert.Equal($"/claims/{theft}?reserve=requested", await ClaimActions.ChangeReserveAsync(x.W.App, claims, form, november));
        // Asked, not yet approved: nothing moves.
        Assert.Equal((0L, 50000L, 2000L), await x.MonthAsync("2026-11"));
        var request = await x.W.ScalarAsync<Guid>("select id from core.claim_reserve_request where claim_id = @theft", new { theft });
        Assert.Equal($"/claims/{theft}?reserve=approved", await ClaimActions.ApproveReserveRequestAsync(x.W.App, await x.W.UserAsync("admin"),
            ClaimWorld.Form(("claim_id", theft.ToString()), ("request_id", request.ToString()))));
        // November: damage 0 + theft 700.00; October still 500.00 (the movement is booked 10.11).
        Assert.Equal(((100000L, 50000L, 2000L), (0L, 70000L, 2000L)), (await x.MonthAsync("2026-10"), await x.MonthAsync("2026-11")));
        // The same form again: the reserve is already 700.00 — no request, no movement.
        Assert.Equal($"/claims/{theft}?reserve=unchanged", await ClaimActions.ChangeReserveAsync(x.W.App, claims, form, november));
        Assert.Equal(1, await x.W.ScalarAsync<int>("select count(*) from core.claim_reserve_request where claim_id = @theft", new { theft }));
        Assert.Equal(((100000L, 50000L, 2000L), (0L, 70000L, 2000L)), (await x.MonthAsync("2026-10"), await x.MonthAsync("2026-11")));
    }
}

/// <summary>
/// The old program's open PRs #206, #209 and #219 (scripts/integration/demo-paid-reduction-approval.test.ts): a premium receipt
/// written by the real action and the real monthly report's receivables. The world: an approved 400.00 GEL policy from
/// 01.10.2026 to 01.10.2027, four instalments of 100.00 due 01.10, 01.01, 01.04 and 01.07 (a single line, where the demo had
/// two of 300.00 and 100.00: the receivables are the same, the earned premium is compared before and after, not to the demo's).
/// </summary>
public sealed class ReceiptReportChain(TestDatabase db)
{
    public readonly World W = new(db);
    public static readonly DateOnly October = new(2026, 10, 1);
    public static readonly (int, decimal)[] Quarterly = [(0, 100m), (92, 100m), (182, 100m), (273, 100m)];

    public async Task<(CurrentUser User, World.Policy Policy)> ApprovedAsync()
    {
        var user = await W.UserAsync();
        var p = await W.PendingPolicyAsync(await W.ClientAsync(), October, premium: 400m, schedule: Quarterly);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(W.App, user, p.Id.ToString())).Outcome);
        return (user, p);
    }

    public Task<PremiumReport.Report> ReportAsync(string month) => W.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, W.Rates, month));

    public static (long Owed, long Overdue) Owed(PremiumReport.Report r) =>
        r.Receivables.Single(x => x.Currency == "GEL") is var g ? ((long)g.Outstanding, (long)g.Overdue) : default;
}

public sealed class OpenPr206YearEndAllocationTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ReceiptReportChain x = new(db);

    [Fact]
    public async Task PR206_a_receipt_of_31_12_allocated_on_31_01_leaves_December_as_it_was_and_January_owes_300_with_100_overdue()
    {
        var (_, p) = await x.ApprovedAsync();
        var receipt = await x.W.ReceiptAsync(p.Holder, new DateOnly(2026, 12, 31), 100m, reference: "DEMO-ADV-YEAR");
        var decemberBefore = await x.ReportAsync("2026-12");
        var januaryBefore = await x.ReportAsync("2027-01");
        Assert.Equal(((40000L, 10000L), (40000L, 20000L)), (ReceiptReportChain.Owed(decemberBefore), ReceiptReportChain.Owed(januaryBefore)));

        On(x.W, new DateOnly(2027, 1, 31));
        Assert.Equal($"/policies/{p.Id}?advance=allocated&gel=10000&left=0",
            await PaymentActions.AllocateReceiptAsync(x.W.App, x.W.Rates, await x.W.UserAsync("underwriter"), p.Id.ToString(), receipt.ToString()));
        // No new receipt: the receipt keeps 31.12, its allocation is booked 31.01 (owner, 05.10.2026; H21), on the oldest instalment.
        Assert.Equal([(100m, new DateOnly(2027, 1, 31), new DateOnly(2026, 12, 31))], await x.W.QueryAsync<(decimal, DateOnly, DateOnly)>("""
            select a.amount, a.booked_on, p.received_on from core.payment_allocation a join core.payment p on p.id = a.payment_id where a.payment_id = @receipt
            """, new { receipt }));
        Assert.Equal(1, await x.W.ScalarAsync<int>("select count(*) from core.payment"));

        var decemberAfter = await x.ReportAsync("2026-12");
        var januaryAfter = await x.ReportAsync("2027-01");
        // December unchanged, every figure; January owes 300.00, the 01.01 instalment still overdue; the rest of January as before.
        Assert.Equivalent(decemberBefore, decemberAfter, strict: true);
        Assert.Equal((30000L, 10000L), ReceiptReportChain.Owed(januaryAfter));
        Assert.Equivalent(januaryBefore with { Receivables = [] }, januaryAfter with { Receivables = [] }, strict: true);
    }
}

public sealed class OpenPr209CorrectionThenReceiptTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ReceiptReportChain x = new(db);

    /// <summary>
    /// PR #209 under the rule of PRs #218/#225 (owner, 13.10.2026): the corrected schedule is laid out again by its first shares,
    /// 4 × 80.00, where #209's head took the reduction off the last instalment (100/100/100/20): before the receipt 80.00 is overdue
    /// where #209 had 100.00; after it the month's figures are #209's.
    /// </summary>
    [Fact]
    public async Task PR209_a_minus_80_change_approved_in_October_then_100_received_on_07_10_leaves_October_owing_220_nothing_overdue()
    {
        var (user, o) = await x.ApprovedAsync();
        var before = await x.ReportAsync("2026-10");
        Assert.Equal((40000L, 10000L), ReceiptReportChain.Owed(before));
        Assert.Equal((1, 40000L, 3397L, 36603L, 2000L), ((int)before.Totals.PoliciesIssued, (long)before.Totals.FinancialWrittenGel, (long)before.Totals.EarnedGel,
            (long)before.Totals.UnearnedGel, (long)before.ClaimsTotals.IbnrGel));

        Assert.NotNull((await x.W.EndorseAsync(user, o.Id, ReceiptReportChain.October.AddDays(6), "-80")).Redirect);
        var change = await x.W.ScalarAsync<Guid>("select id from core.policy where replaces_policy_id = @id", new { id = o.Id });
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(x.W.App, user, change.ToString())).Outcome);
        var approved = await x.ReportAsync("2026-10");
        // Owed 320.00, the first instalment (now 80.00) overdue — #209's head had 100.00 there, its schedule 100/100/100/20.
        Assert.Equal((32000L, 8000L), ReceiptReportChain.Owed(approved));
        Assert.Equal((1, 32000L, 2718L, 29282L, 1600L), ((int)approved.Totals.PoliciesIssued, (long)approved.Totals.FinancialWrittenGel, (long)approved.Totals.EarnedGel,
            (long)approved.Totals.UnearnedGel, (long)approved.ClaimsTotals.IbnrGel));

        On(x.W, ReceiptReportChain.October.AddDays(6));
        Assert.NotNull((await x.W.PayAsync(user, o.Id, "100.00", ReceiptReportChain.October.AddDays(6))).Redirect);
        // One receipt on the original, 80.00 on its first instalment and 20.00 on the second; nothing on the change's document.
        Assert.Equal([80m, 20m], await x.W.QueryAsync<decimal>("""
            select a.amount from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @id order by i.due_on
            """, new { id = o.Id }));
        Assert.Equal(0, await x.W.ScalarAsync<int>("select count(*) from core.installment i join core.payment_allocation a on a.installment_id = i.id where i.policy_id = @change", new { change }));
        var after = await x.ReportAsync("2026-10");
        Assert.Equal((22000L, 0L), ReceiptReportChain.Owed(after));
        Assert.Equivalent(approved with { Receivables = [] }, after with { Receivables = [] }, strict: true);
    }
}

public sealed class OpenPr219PaidInFullTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ReceiptReportChain x = new(db);

    [Fact]
    public async Task PR219_September_closed_the_400_policy_of_01_10_paid_400_on_07_10_four_allocations_of_100_October_owes_nothing_no_advance()
    {
        var admin = await x.W.UserAsync();
        await x.W.CloseMonthAsync(new DateOnly(2026, 9, 1), admin.Id);
        var (user, p) = await x.ApprovedAsync();
        var unpaid = await x.ReportAsync("2026-10");
        Assert.Equal((40000L, 10000L), ReceiptReportChain.Owed(unpaid));

        On(x.W, ReceiptReportChain.October.AddDays(6));
        Assert.NotNull((await x.W.PayAsync(user, p.Id, "400.00", ReceiptReportChain.October.AddDays(6))).Redirect);
        Assert.Equal([100m, 100m, 100m, 100m], await x.W.QueryAsync<decimal>("""
            select a.amount from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @id order by i.due_on
            """, new { id = p.Id }));
        Assert.Equal(0m, await x.W.ScalarAsync<decimal>("""
            select p.amount_gel - coalesce((select sum(a.amount_gel) from core.payment_allocation a where a.payment_id = p.id), 0) from core.payment p
            """));
        var paid = await x.ReportAsync("2026-10");
        Assert.Equal((0L, 0L), ReceiptReportChain.Owed(paid));
        // The premium as before: one policy, 400.00 written both ways.
        Assert.Equivalent(unpaid with { Receivables = [] }, paid with { Receivables = [] }, strict: true);
        Assert.Equal((1, 40000L, 40000L), ((int)paid.Totals.PoliciesIssued, (long)paid.Totals.StatisticalWrittenGel, (long)paid.Totals.FinancialWrittenGel));
    }
}
