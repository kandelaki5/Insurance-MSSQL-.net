using Insurance.Application.Data;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;
using static Insurance.Application.Services.Billing;

namespace Insurance.Application.Services;

/// <summary>Cancelling an active policy (legacy src/lib/policy-cancel.ts).</summary>
public static class PolicyCancel
{
    public enum CancelOutcome { Done, NotActive, OutsideTerm }

    private sealed record LedgerRowDb(Guid Id, Guid PolicyLineId, decimal Amount, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo);

    private const string InsertRow = """
        insert into core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, cancels_transaction_id, created_by)
        values (@id, @lineId, @kind, @amount, @amountGel, @bookedOn, @coverFrom, @coverTo, @cancels, @userId)
        """;

    /// <summary>The first day after the last closed month, or null when no month is closed.</summary>
    public static Task<DateOnly?> FirstOpenDayAsync(Tx tx) =>
        tx.QuerySingleOrDefaultAsync<DateOnly?>("select dateadd(month, 1, max(period_start)) from core.accounting_period where status = N'closed'");

    /// <summary>
    /// Cancels an active policy from a date inside one transaction (docs/04 rules რ6, რ11): cover ends at the start of
    /// that day, the unearned part of every ledger row is reversed by a row tied to the original (valued at the
    /// original's GEL rate), the schedule shrinks to the retained premium, and the policy becomes 'cancelled'. The
    /// reversal is booked on the cancel date, or in the first open month when that date lies in a closed one.
    /// </summary>
    public static async Task<(CancelOutcome Outcome, long RetainedMinor)> CancelPolicyInTxAsync(Tx tx, Guid policyId, DateOnly cancelOn, string reason, Guid userId, bool bringForward = false)
    {
        var policy = await tx.QuerySingleOrDefaultAsync<(string Status, DateOnly EffectiveFrom, DateOnly EffectiveTo, string PremiumCurrency, DateOnly? CancelledOn)?>(
            "select status, effective_from, effective_to, premium_currency, cancelled_on from core.policy with (updlock, rowlock, holdlock) where id = @policyId", new { policyId });
        // Only a total-loss settlement may bring a later cancellation forward to the loss day (docs/04 rule 15, H24); an
        // ordinary cancellation stays one-shot, so a stale or repeated form never changes a cancellation already made.
        // No booked row changes: the later cancellation is undone by a mirror row on its own day and a cancellation from
        // the earlier day is added, so every date shows the policy as if cancelled once.
        var forward = bringForward && policy is { Status: "cancelled", CancelledOn: { } was } && cancelOn < was;
        if (policy is not { } p || (p.Status != "active" && !forward)) return (CancelOutcome.NotActive, 0);
        if (cancelOn < p.EffectiveFrom || cancelOn >= p.EffectiveTo) return (CancelOutcome.OutsideTerm, 0);
        var currency = ParseCurrency(p.PremiumCurrency);

        var firstOpen = await FirstOpenDayAsync(tx);
        DateOnly OpenFrom(DateOnly day) => firstOpen is { } f && f > day ? f : day;
        var bookedOn = OpenFrom(cancelOn);
        var scheduleReason = $"{PolicyTexts.Cancel.ScheduleReason} {Dates.ToIso(cancelOn)}";

        if (forward)
        {
            // Undo each cancellation still in force, on its own booking day (or the first open month).
            var live = (await tx.QueryAsync<LedgerRowDb>("""
                select c.id as Id, c.policy_line_id as PolicyLineId, c.amount as Amount, c.amount_gel as AmountGel, c.booked_on as BookedOn,
                       c.cover_from as CoverFrom, c.cover_to as CoverTo
                from core.premium_transaction c join core.premium_transaction o on o.id = c.cancels_transaction_id
                join core.policy_line l on l.id = c.policy_line_id
                where l.policy_id = @policyId and o.cancels_transaction_id is null
                  and not exists (select 1 from core.premium_transaction u where u.cancels_transaction_id = c.id)
                """, new { policyId })).ToList();
            foreach (var c in live)
            {
                var undo = Guid.NewGuid();
                await tx.ExecuteAsync(InsertRow, new
                {
                    id = undo, lineId = c.PolicyLineId, kind = "correction", amount = -c.Amount, amountGel = -c.AmountGel,
                    bookedOn = OpenFrom(c.BookedOn), coverFrom = c.CoverFrom, coverTo = c.CoverTo, cancels = c.Id, userId,
                });
                await ReinsuranceStore.CedeCancellationAsync(tx, undo, userId);
            }
            // The schedule reductions booked for a later day count from the new date: each is undone on its own day and
            // booked again now (the pairs net to nothing in the full schedule).
            await AdvanceScheduleAdjustmentsAsync(tx, policyId, bookedOn, scheduleReason, userId);
        }

        // Every original ledger row (not itself a reversal) that no cancellation reverses any more.
        var rows = (await tx.QueryAsync<LedgerRowDb>("""
            select t.id as Id, t.policy_line_id as PolicyLineId, t.amount as Amount, t.amount_gel as AmountGel, t.booked_on as BookedOn,
                   t.cover_from as CoverFrom, t.cover_to as CoverTo
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
            where l.policy_id = @policyId and t.cancels_transaction_id is null
              and not exists (select 1 from core.premium_transaction r where r.cancels_transaction_id = t.id
                              and not exists (select 1 from core.premium_transaction u where u.cancels_transaction_id = r.id))
            """, new { policyId })).ToList();
        long retainedMinor = 0;
        foreach (var row in rows)
        {
            var original = new PremiumTransaction(new Money(Tetri(row.Amount), currency), row.BookedOn, row.CoverFrom, row.CoverTo);
            var originalGel = original with { Amount = new Money(Tetri(row.AmountGel), Currency.GEL) };
            var reversal = EarnedPremium.CancellationOf(original, cancelOn);
            var reversalGel = EarnedPremium.CancellationOf(originalGel, cancelOn);
            retainedMinor += original.Amount.Minor + (reversal?.Amount.Minor ?? 0);
            if (reversal is null || reversalGel is null) continue;
            var booked = Guid.NewGuid();
            await tx.ExecuteAsync(InsertRow, new
            {
                id = booked, lineId = row.PolicyLineId, kind = "cancellation", amount = FromTetri(reversal.Amount.Minor), amountGel = FromTetri(reversalGel.Amount.Minor),
                bookedOn, coverFrom = reversal.CoverFrom, coverTo = reversal.CoverTo, cancels = row.Id, userId,
            });
            await ReinsuranceStore.CedeCancellationAsync(tx, booked, userId);
        }

        // The schedule shrinks to the retained premium through dated adjustments (review H03): instalments and their
        // original amounts stay, the change is booked on the cancel date.
        await ReduceScheduleAsync(tx, policyId, currency, retainedMinor, bookedOn, scheduleReason, userId);

        await tx.ExecuteAsync("update core.policy set status = N'cancelled', cancelled_on = @cancelOn, cancel_reason = @reason where id = @policyId",
            new { cancelOn, reason, policyId });
        return (CancelOutcome.Done, retainedMinor);
    }
}
