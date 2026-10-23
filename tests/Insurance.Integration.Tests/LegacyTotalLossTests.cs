using Insurance.Application.Services;
using Insurance.Domain.Finance;
using Insurance.Integration.Tests.Policies;

namespace Insurance.Integration.Tests;

/// <summary>
/// Legacy scripts/total-loss-regression.ts (Codex H24, docs/04 rule 15 (a) and (c)) as it ran: through the real actions
/// approvePolicy, recordPayment, cancelPolicy, registerClaim, decideClaim, payClaim, placeFacultative — on real days (the
/// script took its dates from the first open month; here from today, so that the loss and the cancellation in force lie
/// before today and the later cancellation after it, as the script required). Cover 365 days from START, 4 × 91.25, 91.25
/// paid on START+4; loss START+153 (153.00 earned, 61.75 earned unpaid); cancellations from START+172 (in force) and
/// START+274 (future); claim 1 000.00 paid today. ClaimTotalLossTests proves the same rules on fixture rows; this class is
/// the legacy script's own run and its two extra checks (the premium by day, and a stale ordinary cancellation).
/// </summary>
public sealed class LegacyTotalLossTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private readonly ClaimWorld cw = new(db);
    private static readonly DateOnly Today = World.Today;
    private static readonly DateOnly Start = Today.AddDays(-200);
    private static readonly DateOnly Future = Start.AddDays(274);
    private static readonly DateOnly Past = Start.AddDays(172);
    private static readonly DateOnly Loss = Start.AddDays(153);
    private static readonly DateOnly PaidOn = Start.AddDays(4);

    private async Task<(CurrentUser User, ClaimWorld.Policy Policy)> PolicyAsync(DateOnly? cancelOn, Func<CurrentUser, World.Policy, Task>? beforeCancel = null)
    {
        Assert.True(Loss < Past && Past <= Today && Future > Today);
        var user = await cw.UserAsync("admin");
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), Start);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, p.Id.ToString())).Outcome);
        Assert.NotNull((await w.PayAsync(user, p.Id, "91.25", PaidOn, reference: "91.25")).Redirect);
        if (beforeCancel is not null) await beforeCancel(user, p);
        if (cancelOn is { } on) Assert.NotNull((await w.CancelAsync(user, p.Id, on, "cancel")).Redirect);
        var line = await w.ScalarAsync<Guid>("select id from core.policy_line where policy_id = @id", new { id = p.Id });
        var instalments = await w.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = p.Id });
        return (user, new ClaimWorld.Policy(p.Id, p.Number, p.Holder, p.ContractId, new Dictionary<string, Guid> { ["damage"] = line }, instalments));
    }

    private async Task<(Guid Claim, List<string?> Paid)> ClaimAsync(CurrentUser user, ClaimWorld.Policy p, string mode, params string[] payments)
    {
        var claim = (await cw.RegisterAsync(user, p, Loss, Today, ("damage", "1000")))[0];
        await cw.ApproveAsync(user, claim, "1000", Today, totalLoss: true);
        var paid = new List<string?>();
        foreach (var amount in payments)
            paid.Add((await ClaimActions.PayClaimAsync(w.App, cw.Rates, user, ClaimWorld.Form(("claim_id", claim.ToString()), ("paid_on", ClaimWorld.D(Today)), ("amount", amount),
                ("payee_kind", "policyholder"), ("total_loss_mode", mode)), Today)).RedirectTo);
        return (claim, paid);
    }

    private async Task<decimal> OwedOnAsync(Guid policyId, DateOnly asOf) => await w.OwedOnAsync(policyId, asOf);

    /// <summary>Written premium and UPR on a day, from the ledger as the card links it.</summary>
    private async Task<string> PremiumOnAsync(Guid policyId, DateOnly asOf)
    {
        var rows = await w.QueryAsync<(Guid Id, Guid? CancelsId, decimal Amount, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo)>("""
            select t.id, t.cancels_transaction_id, t.amount, t.booked_on, t.cover_from, t.cover_to
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @policyId
            """, new { policyId });
        var txs = EarnedPremium.LinkLedger([.. rows.Select(r => new LedgerRow(r.Id.ToString(), r.CancelsId?.ToString(),
            new PremiumTransaction(new Money(Billing.Tetri(r.Amount), Currency.GEL), r.BookedOn, r.CoverFrom, r.CoverTo)))]);
        var pos = EarnedPremium.PremiumPositionOf(txs, asOf).GetValueOrDefault(Currency.GEL, new PremiumPosition(0, 0, 0));
        return $"written {Billing.TetriText(pos.Written)} UPR {Billing.TetriText(pos.Unearned)}";
    }

    private async Task<(string Offsets, string Nets, DateOnly? CancelledOn, List<(string Kind, decimal Amount, DateOnly BookedOn)> Ledger, decimal Reconciliation)> StateAsync(Guid policyId, Guid? claim)
    {
        var payments = claim is null ? [] : await w.QueryAsync<(decimal, decimal)>("select premium_offset, net_paid from core.claim_payment where claim_id = @claim order by created_at", new { claim });
        var cancelledOn = await w.ScalarAsync<DateOnly?>("select cancelled_on from core.policy where id = @policyId", new { policyId });
        var ledger = await w.QueryAsync<(string, decimal, DateOnly)>("""
            select t.kind, t.amount, t.booked_on from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @policyId order by t.created_at
            """, new { policyId });
        static string J(IEnumerable<decimal> xs) => string.Join(",", xs.Select(x => x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        return (J(payments.Select(p => p.Item1)), J(payments.Select(p => p.Item2)), cancelledOn, ledger,
            await w.ScalarAsync<decimal>("select difference from core.schedule_reconciliation where policy_id = @policyId", new { policyId }));
    }

    [Fact]
    public async Task A_earned_unpaid_with_a_future_cancellation_already_booked_and_the_premium_by_day()
    {
        var (user, p) = await PolicyAsync(Future);
        var beforeLoss = await PremiumOnAsync(p.Id, Loss.AddDays(-1));
        var (claim, paid) = await ClaimAsync(user, p, "earned_unpaid", "1000");
        Assert.All(paid, x => Assert.EndsWith("?paid=1", x));
        var s = await StateAsync(p.Id, claim);
        Assert.Equal(("61.75", "938.25", (DateOnly?)Loss), (s.Offsets, s.Nets, s.CancelledOn));
        Assert.Equal((0m, 0m), (await OwedOnAsync(p.Id, Today), await OwedOnAsync(p.Id, Future)));
        Assert.Equal(153.00m, s.Ledger.Sum(r => r.Amount));
        Assert.Contains(("cancellation", -91.00m, Future), s.Ledger);
        Assert.Contains(("cancellation", -212.00m, Loss), s.Ledger);
        Assert.Contains(("correction", 91.00m, Future), s.Ledger);
        Assert.Equal(0m, s.Reconciliation);
        // The loss ends the cover at once; the day before the loss is as before the claim.
        Assert.Equal("written 153.00 UPR 0.00", await PremiumOnAsync(p.Id, Today));
        Assert.Equal(beforeLoss, await PremiumOnAsync(p.Id, Loss.AddDays(-1)));
    }

    [Fact]
    public async Task A_with_a_facultative_placement_placed_after_the_later_cancellation_mirrors_one_cession_per_row_adding_up_to_30_percent()
    {
        var treaty = Guid.NewGuid();
        await cw.ExecAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent, commission_percent)
            values (@treaty, @number, N'ფაკულტატური 30%', @reinsurer, N'facultative', @from, @to, N'GEL', 30, 10)
            """, new { treaty, number = "FAC-" + ClaimWorld.Tag(), reinsurer = await cw.PartyAsync(), from = Start, to = Start.AddDays(365) });
        var (user, p) = await PolicyAsync(Future);
        Assert.Equal($"/policies/{p.Id}?facultative=placed", await PolicyActions.PlaceFacultativeAsync(w.App, user, p.Id.ToString(), treaty.ToString()));
        await ClaimAsync(user, p, "earned_unpaid", "1000");
        var cessions = await w.QueryAsync<(string Kind, decimal Amount)>("""
            select t.kind, c.amount from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
            where t.policy_line_id = @line order by c.created_at
            """, new { line = p.LineOf("damage") });
        Assert.Equal(4, cessions.Count);
        // 30 % of the 153.00 kept: 365 × 0.3 = 109.50, cancelled once from the loss.
        Assert.Equal(45.90m, cessions.Sum(x => x.Amount));
    }

    [Fact]
    public async Task C_none_with_a_future_cancellation_cancels_from_the_loss_and_sets_off_nothing()
    {
        var (user, p) = await PolicyAsync(Future);
        var (claim, _) = await ClaimAsync(user, p, "none", "1000");
        var s = await StateAsync(p.Id, claim);
        Assert.Equal(("0.00", "1000.00", (DateOnly?)Loss, 0m), (s.Offsets, s.Nets, s.CancelledOn, s.Reconciliation));
        Assert.Equal((61.75m, 61.75m), (await OwedOnAsync(p.Id, Today), await OwedOnAsync(p.Id, Future)));
    }

    [Fact]
    public async Task A_with_the_later_cancellation_already_in_force()
    {
        var (user, p) = await PolicyAsync(Past);
        var (claim, _) = await ClaimAsync(user, p, "earned_unpaid", "1000");
        var s = await StateAsync(p.Id, claim);
        Assert.Equal(("61.75", "938.25", (DateOnly?)Loss, 153.00m), (s.Offsets, s.Nets, s.CancelledOn, s.Ledger.Sum(r => r.Amount)));
        Assert.Equal(0m, await OwedOnAsync(p.Id, Today));
    }

    [Fact]
    public async Task A_paid_in_two_parts_brings_the_cancellation_forward_once_and_sets_off_once()
    {
        var (user, p) = await PolicyAsync(Future);
        var (claim, _) = await ClaimAsync(user, p, "earned_unpaid", "600", "400");
        var s = await StateAsync(p.Id, claim);
        Assert.Equal(("61.75,0.00", "538.25,400.00"), (s.Offsets, s.Nets));
        Assert.Single(s.Ledger, r => r.Kind == "correction");
        Assert.Single(s.Ledger, r => r.BookedOn == Loss);
        Assert.Equal(0m, await OwedOnAsync(p.Id, Today));
    }

    [Fact]
    public async Task An_ordinary_cancellation_from_an_earlier_day_on_an_already_cancelled_policy_is_refused_and_changes_nothing()
    {
        var (user, p) = await PolicyAsync(Future);
        var before = (await StateAsync(p.Id, null)).Ledger;
        var stale = await w.CancelAsync(user, p.Id, Loss, "stale form");
        Assert.Null(stale.Redirect);
        var s = await StateAsync(p.Id, null);
        Assert.Equal((DateOnly?)Future, s.CancelledOn);
        Assert.Equal(before, s.Ledger);
        Assert.DoesNotContain(s.Ledger, r => r.Kind == "correction");
    }

    [Fact]
    public async Task Control_A_on_an_active_policy_cancels_from_the_loss_without_a_correction()
    {
        var (user, p) = await PolicyAsync(null);
        var (claim, _) = await ClaimAsync(user, p, "earned_unpaid", "1000");
        var s = await StateAsync(p.Id, claim);
        Assert.Equal(("61.75", "938.25", (DateOnly?)Loss), (s.Offsets, s.Nets, s.CancelledOn));
        Assert.DoesNotContain(s.Ledger, r => r.Kind == "correction");
    }
}
