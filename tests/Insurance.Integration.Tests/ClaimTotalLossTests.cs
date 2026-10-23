using Insurance.Application.Services;
using Insurance.Domain.Finance;

namespace Insurance.Integration.Tests;

/// <summary>
/// A total loss settled on payment, the three rules of docs/04 rule 15 (legacy scripts/total-loss-regression.ts, Codex H24, and
/// docs/07 §6 ბ / დ): (a) the policy is cancelled on the loss day and only the earned unpaid premium is set off; (b) the whole
/// unpaid premium is set off and the policy stays in force; (c) cancelled without a set-off. A cancellation already booked from
/// a later day is brought forward to the loss by new rows. No month is closed in this class: a closed month would move every
/// cancellation's booking day (PolicyCancel books in the first open month).
/// Fixture as in the old test: cover 365 days from START, premium 365.00 in 4 × 91.25 (START, +91, +182, +273), 91.25 paid on
/// START+4; loss on START+153 (153.00 earned, 61.75 earned unpaid); claim 1 000.00 approved as a total loss.
/// </summary>
public sealed class ClaimTotalLossTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2200;

    private sealed record Case(CurrentUser User, ClaimWorld.Policy Policy, DateOnly Start, DateOnly Loss, DateOnly Past, DateOnly Future, DateOnly Today);

    private async Task<Case> PolicyAsync(string? cancel)
    {
        var y = Interlocked.Increment(ref year);
        var start = new DateOnly(y, 1, 1);
        var user = await w.UserAsync("admin");
        var p = await w.PolicyAsync(user.Id, start, [new ClaimWorld.Line("damage", 20000m, 0m, 365m)],
            [.. new[] { 0, 91, 182, 273 }.Select(d => (start.AddDays(d), 91.25m))], days: 365);
        await w.PaidAsync(user.Id, p, start.AddDays(4), 91.25m);
        var c = new Case(user, p, start, start.AddDays(153), start.AddDays(172), start.AddDays(274), start.AddDays(200));
        if (cancel is not null)
        {
            var on = cancel == "past" ? c.Past : c.Future;
            var (outcome, _) = await w.App.AsUserAsync(user.Id, tx => PolicyCancel.CancelPolicyInTxAsync(tx, p.Id, on, "გაუქმება", user.Id));
            Assert.Equal(PolicyCancel.CancelOutcome.Done, outcome);
        }
        return c;
    }

    private async Task<(Guid Claim, List<string?> Paid)> ClaimAsync(Case c, string mode, params string[] payments)
    {
        var claim = (await w.RegisterAsync(c.User, c.Policy, c.Loss, c.Today, ("damage", "1000")))[0];
        await w.ApproveAsync(c.User, claim, "1000", c.Today, totalLoss: true);
        var paid = new List<string?>();
        foreach (var amount in payments)
        {
            var outcome = await ClaimActions.PayClaimAsync(w.App, w.Rates, c.User, ClaimWorld.Form(("claim_id", claim.ToString()), ("amount", amount), ("paid_on", ClaimWorld.D(c.Today)),
                ("payee_kind", "policyholder"), ("total_loss_mode", mode)), c.Today);
            paid.Add(outcome.RedirectTo ?? outcome.Message ?? string.Join(",", outcome.Errors));
        }
        return (claim, paid);
    }

    /// <summary>The policy's debt on a day: the schedule as of that day and every allocation (the card's view).</summary>
    private Task<decimal> OwedOnAsync(Case c, DateOnly asOf) => w.App.ReadTxAsync(async tx =>
    {
        var schedule = Billing.ToInstallments(await Billing.LoadScheduleAsync(tx, c.Policy.Id, asOf), Currency.GEL);
        var allocations = (await tx.QueryAsync<Billing.AllocationRow>("""
            select a.amount as Amount, a.booked_on as ReceivedOn, p.method as Method from core.payment_allocation a
            join core.payment p on p.id = a.payment_id join core.installment i on i.id = a.installment_id where i.policy_id = @id
            """, new { id = c.Policy.Id })).ToList();
        var position = Receivable.ReceivableAsOf(schedule, Billing.ToCredits(allocations, Currency.GEL), asOf, (_, _) => 1m);
        return Billing.FromTetri(position.Outstanding.Minor - position.Overpaid.Minor);
    });

    private async Task<(string Offsets, string Nets, string Status, DateOnly? CancelledOn, List<(string Kind, decimal Amount, DateOnly BookedOn)> Ledger, decimal Reconciliation)> StateAsync(Case c, Guid claim)
    {
        var payments = await w.RowsAsync<(decimal, decimal)>("select premium_offset, net_paid from core.claim_payment where claim_id = @claim order by created_at", new { claim });
        var (status, cancelledOn) = (await w.RowsAsync<(string, DateOnly?)>("select status, cancelled_on from core.policy where id = @id", new { id = c.Policy.Id })).Single();
        var ledger = await w.RowsAsync<(string, decimal, DateOnly)>("""
            select t.kind, t.amount, t.booked_on from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id order by t.created_at
            """, new { id = c.Policy.Id });
        var reconciliation = await w.ScalarAsync<decimal>("select difference from core.schedule_reconciliation where policy_id = @id", new { id = c.Policy.Id });
        static string J(IEnumerable<decimal> xs) => string.Join(",", xs.Select(x => x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        return (J(payments.Select(p => p.Item1)), J(payments.Select(p => p.Item2)), status, cancelledOn, ledger, reconciliation);
    }

    [Fact]
    public async Task Rule_a_on_a_policy_cancelled_from_a_later_day_brings_the_cancellation_forward_to_the_loss_and_sets_off_61_75()
    {
        var c = await PolicyAsync("future");
        var (claim, paid) = await ClaimAsync(c, "earned_unpaid", "1000");
        Assert.All(paid, p => Assert.EndsWith("?paid=1", p));
        var s = await StateAsync(c, claim);
        Assert.Equal(("61.75", "938.25", "cancelled", (DateOnly?)c.Loss), (s.Offsets, s.Nets, s.Status, s.CancelledOn));
        Assert.Equal(0m, await OwedOnAsync(c, c.Today));
        Assert.Equal(0m, await OwedOnAsync(c, c.Future));
        Assert.Equal(153.00m, s.Ledger.Sum(r => r.Amount));
        Assert.Contains(("cancellation", -91.00m, c.Future), s.Ledger);
        Assert.Contains(("cancellation", -212.00m, c.Loss), s.Ledger);
        Assert.Contains(("correction", 91.00m, c.Future), s.Ledger);
        Assert.Equal(4, s.Ledger.Count);
        Assert.Equal(0m, s.Reconciliation);
    }

    [Fact]
    public async Task Rule_a_with_a_facultative_placement_mirrors_one_cession_per_row_adding_up_to_30_percent_of_the_premium_kept()
    {
        var c = await PolicyAsync("future");
        var treaty = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent, commission_percent)
            values (@treaty, @number, N'ფაკულტატური 30%', @reinsurer, N'facultative', @from, @to, N'GEL', 30, 10)
            """, new { treaty, number = "FAC-" + ClaimWorld.Tag(), reinsurer = await w.PartyAsync(), from = c.Start, to = c.Start.AddDays(365) });
        Assert.Equal(ReinsuranceStore.FacultativeOutcome.Placed, await w.App.AsUserAsync(c.User.Id, tx => ReinsuranceStore.PlaceFacultativeAsync(tx, c.Policy.Id, treaty, c.User.Id)));
        await ClaimAsync(c, "earned_unpaid", "1000");
        var cessions = await w.RowsAsync<(string Kind, decimal Amount)>("""
            select t.kind, c.amount from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id
            where t.policy_line_id = @line order by c.created_at
            """, new { line = c.Policy.LineOf("damage") });
        Assert.Equal(4, cessions.Count);
        Assert.Equal(45.90m, cessions.Sum(x => x.Amount));
    }

    [Fact]
    public async Task Rule_c_on_a_policy_cancelled_from_a_later_day_cancels_from_the_loss_and_sets_off_nothing()
    {
        var c = await PolicyAsync("future");
        var (claim, _) = await ClaimAsync(c, "none", "1000");
        var s = await StateAsync(c, claim);
        Assert.Equal(("0.00", "1000.00", (DateOnly?)c.Loss), (s.Offsets, s.Nets, s.CancelledOn));
        Assert.Equal(61.75m, await OwedOnAsync(c, c.Today));
        Assert.Equal(61.75m, await OwedOnAsync(c, c.Future));
        Assert.Equal(0m, s.Reconciliation);
    }

    [Fact]
    public async Task Rule_a_on_a_policy_whose_later_cancellation_is_already_in_force()
    {
        var c = await PolicyAsync("past");
        var (claim, _) = await ClaimAsync(c, "earned_unpaid", "1000");
        var s = await StateAsync(c, claim);
        Assert.Equal(("61.75", "938.25", (DateOnly?)c.Loss), (s.Offsets, s.Nets, s.CancelledOn));
        Assert.Equal(0m, await OwedOnAsync(c, c.Today));
        Assert.Equal(153.00m, s.Ledger.Sum(r => r.Amount));
    }

    [Fact]
    public async Task Rule_a_paid_in_two_parts_brings_the_cancellation_forward_once_and_sets_off_once()
    {
        var c = await PolicyAsync("future");
        var (claim, _) = await ClaimAsync(c, "earned_unpaid", "600", "400");
        var s = await StateAsync(c, claim);
        Assert.Equal(("61.75,0.00", "538.25,400.00"), (s.Offsets, s.Nets));
        Assert.Single(s.Ledger, r => r.Kind == "correction");
        Assert.Single(s.Ledger, r => r.BookedOn == c.Loss);
        Assert.Equal(0m, await OwedOnAsync(c, c.Today));
    }

    [Fact]
    public async Task Control_rule_a_on_an_active_policy_cancels_from_the_loss_without_a_correction()
    {
        var c = await PolicyAsync(null);
        var (claim, _) = await ClaimAsync(c, "earned_unpaid", "1000");
        var s = await StateAsync(c, claim);
        Assert.Equal(("61.75", "938.25", "cancelled", (DateOnly?)c.Loss), (s.Offsets, s.Nets, s.Status, s.CancelledOn));
        Assert.DoesNotContain(s.Ledger, r => r.Kind == "correction");
    }

    [Fact]
    public async Task Rule_b_sets_off_the_whole_unpaid_premium_and_the_policy_stays_in_force_to_its_end()
    {
        var c = await PolicyAsync(null);
        var (claim, _) = await ClaimAsync(c, "all_unpaid", "1000");
        var s = await StateAsync(c, claim);
        Assert.Equal(("273.75", "726.25", "active", (DateOnly?)null), (s.Offsets, s.Nets, s.Status, s.CancelledOn));
        Assert.Equal([("issue", 365m, c.Start)], s.Ledger);
        Assert.Equal(0m, await OwedOnAsync(c, c.Today));
        Assert.Equal(c.Start.AddDays(365), await w.ScalarAsync<DateOnly>("select effective_to from core.policy where id = @id", new { id = c.Policy.Id }));
        // The 273.75 is allocated to the instalments, the earliest first.
        Assert.Equal([91.25m, 91.25m, 91.25m, 91.25m], await w.RowsAsync<decimal>("""
            select sum(a.amount) from core.payment_allocation a join core.installment i on i.id = a.installment_id
            where i.policy_id = @id group by i.id, i.due_on order by i.due_on
            """, new { id = c.Policy.Id }));
    }

    [Fact]
    public async Task Rule_b_paid_in_two_parts_does_not_set_off_again()
    {
        var c = await PolicyAsync(null);
        var (claim, _) = await ClaimAsync(c, "all_unpaid", "500", "500");
        var s = await StateAsync(c, claim);
        Assert.Equal(("273.75,0.00", "226.25,500.00"), (s.Offsets, s.Nets));
    }

    [Fact]
    public async Task An_ordinary_claim_with_the_set_off_takes_only_what_is_owed_up_to_the_net()
    {
        var c = await PolicyAsync(null);
        var claim = (await w.RegisterAsync(c.User, c.Policy, c.Loss, c.Today, ("damage", "1000")))[0];
        await w.ApproveAsync(c.User, claim, "1000", c.Today);
        Assert.NotNull((await w.PayAsync(c.User, claim, "100", c.Today, c.Today, ("offset_premium", "on"))).RedirectTo);
        Assert.NotNull((await w.PayAsync(c.User, claim, "900", c.Today, c.Today, ("offset_premium", "on"))).RedirectTo);
        var s = await StateAsync(c, claim);
        Assert.Equal(("100.00,173.75", "0.00,726.25", "active"), (s.Offsets, s.Nets, s.Status));
        Assert.Equal(0m, await OwedOnAsync(c, c.Today));
    }
}
