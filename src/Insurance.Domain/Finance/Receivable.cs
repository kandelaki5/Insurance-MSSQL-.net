using System.Numerics;
using static Insurance.Domain.Finance.Dates;

namespace Insurance.Domain.Finance;

/// <summary>Scheduled premium instalment, kept in the policy currency until settled.</summary>
public sealed record Installment(string Id, DateOnly DueOn, Money Amount);

public enum CreditKind
{
    Payment,
    ClaimOffset,
    CommissionOffset,
}

/// <summary>
/// Something that reduces the premium receivable: a GEL payment (already converted
/// to the policy currency) or a set-off against a claim payout or against commission
/// owed to the intermediary.
/// </summary>
public sealed record Credit(DateOnly On, Money Amount, CreditKind Kind);

public sealed record InstallmentBalance(Installment Installment, long Outstanding);

/// <param name="Outstanding">Whole unpaid premium in the policy currency.</param>
/// <param name="Overdue">Part of it whose due date has passed.</param>
/// <param name="Overpaid">Credits received beyond the schedule.</param>
/// <param name="OutstandingGel">Outstanding premium revalued at the NBG rate of the reporting date.</param>
public sealed record ReceivablePosition(IReadOnlyList<InstallmentBalance> Balances, Money Outstanding, Money Overdue, Money Overpaid, long OutstandingGel);

/// <param name="Credit">Credit to post against the premium receivable.</param>
/// <param name="NetPayable">What is actually paid out to the claimant.</param>
public sealed record ClaimOffset(Credit Credit, Money NetPayable);

/// <param name="Amount">Settled part of the instalment, in the policy currency.</param>
/// <param name="AmountGel">The GEL actually received that this line accounts for.</param>
/// <param name="Rate">NBG rate of the payment day (1 for GEL).</param>
public sealed record AllocationLine(string InstallmentId, Money Amount, long AmountGel, decimal Rate);

/// <param name="UnallocatedGel">GEL received beyond the unpaid schedule; stays on the payment as an advance.</param>
public sealed record PaymentAllocation(IReadOnlyList<AllocationLine> Lines, long UnallocatedGel);

/// <summary>Why <see cref="Receivable.OrderMirrorLines(IReadOnlyList{BigInteger}, BigInteger, IReadOnlyList{int}?, int)"/> found no order.</summary>
public enum MirrorOrderReason
{
    /// <summary>The whole search ran and no order exists (the lines are not a valid history).</summary>
    Impossible,

    /// <summary>The search stopped at `maxStates` before finishing — no proof that no order exists.</summary>
    Budget,
}

/// <summary>The outcome of ordering a reversal's mirror lines: an order (Reason null), or why there is none (Order null).</summary>
public sealed record MirrorOrder(IReadOnlyList<int>? Order, MirrorOrderReason? Reason, int States);

/// <summary>How an approved reduction of the premium is spread over the schedule (a company setting).</summary>
public enum ReductionOrder
{
    LastFirst,
    EqualUnpaid,
}

/// <summary>Premium receivable, payment allocation and schedule changes (receivable.ts).</summary>
public static class Receivable
{
    /// <summary>Premium is always paid in GEL; it settles the debt at the NBG rate of the payment day.</summary>
    public static Credit PaymentCredit(long gelMinor, Currency policyCurrency, DateOnly paidOn, RateProvider rates) =>
        new(paidOn, Money.FromGel(gelMinor, policyCurrency, paidOn, rates), CreditKind.Payment);

    /// <summary>Receivable as of a date. Credits settle instalments oldest due date first.</summary>
    public static ReceivablePosition ReceivableAsOf(IReadOnlyList<Installment> installments, IReadOnlyList<Credit> credits, DateOnly asOf, RateProvider rates)
    {
        var currency = installments.Count > 0 ? installments[0].Amount.Currency : credits.Count > 0 ? credits[0].Amount.Currency : Currency.GEL;
        foreach (var amount in installments.Select(i => i.Amount).Concat(credits.Select(c => c.Amount)))
        {
            if (amount.Currency != currency) throw new InvalidOperationException("Receivable mixes currencies");
        }

        var available = credits.Where(c => DayNumber(c.On) <= DayNumber(asOf)).Sum(c => c.Amount.Minor);

        // A stable sort, as Array.prototype.sort: instalments due the same day keep their order.
        var balances = installments.OrderBy(i => DayNumber(i.DueOn)).Select(installment =>
        {
            var settled = Math.Min(available, installment.Amount.Minor);
            available -= settled;
            return new InstallmentBalance(installment, installment.Amount.Minor - settled);
        }).ToList();

        var outstanding = balances.Sum(b => b.Outstanding);
        var overdue = balances.Where(b => DayNumber(b.Installment.DueOn) < DayNumber(asOf)).Sum(b => b.Outstanding);

        return new ReceivablePosition(
            balances,
            new Money(outstanding, currency),
            new Money(overdue, currency),
            new Money(available, currency),
            Money.ToGel(new Money(outstanding, currency), asOf, rates));
    }

    /// <summary>
    /// Set-off of unpaid premium against a claim payout (e.g. total loss: the whole
    /// remaining premium is deducted from the indemnity).
    /// </summary>
    public static ClaimOffset OffsetClaim(Money claimAmount, Money outstandingPremium, DateOnly on)
    {
        if (claimAmount.Currency != outstandingPremium.Currency)
        {
            throw new InvalidOperationException("Claim and premium must be in the same currency to set off");
        }
        var offset = Math.Max(Math.Min(claimAmount.Minor, outstandingPremium.Minor), 0);
        return new ClaimOffset(
            new Credit(on, new Money(offset, claimAmount.Currency), CreditKind.ClaimOffset),
            new Money(claimAmount.Minor - offset, claimAmount.Currency));
    }

    /// <summary>
    /// Date from which the policy may be cancelled for non-payment: the oldest unpaid
    /// instalment's due date plus the grace period. The grace period is agreed per insurer
    /// or policy; without one, non-payment never cancels automatically.
    /// </summary>
    public static DateOnly? CancellableFrom(ReceivablePosition position, int? graceDays)
    {
        if (graceDays is not { } grace) return null;
        var oldestUnpaid = position.Balances.FirstOrDefault(b => b.Outstanding > 0);
        return oldestUnpaid is null ? null : AddDays(oldestUnpaid.Installment.DueOn, grace);
    }

    /// <summary>
    /// Allocates a GEL payment to the instalments' outstanding balances, oldest due date first
    /// (docs/04 rule 9), converting at the NBG rate of the payment day. `balances` are each
    /// instalment's balance before this payment. Line GEL amounts always add up to the money
    /// received minus the unallocated advance, so nothing is lost to rounding.
    /// </summary>
    public static PaymentAllocation AllocatePayment(long gelMinor, DateOnly paidOn, Currency currency, IReadOnlyList<InstallmentBalance> balances, RateProvider rates)
    {
        if (!(gelMinor > 0)) throw new ArgumentException("Payment must be positive");
        var rate = currency == Currency.GEL ? 1m : rates(currency, paidOn);
        var left = Money.FromGel(gelMinor, currency, paidOn, rates).Minor;
        var open = balances.Where(b => b.Outstanding > 0).OrderBy(b => DayNumber(b.Installment.DueOn)).ToList();

        var parts = new List<(string InstallmentId, long Minor)>();
        foreach (var b in open)
        {
            if (left <= 0) break;
            var minor = Math.Min(left, b.Outstanding);
            left -= minor;
            parts.Add((b.Installment.Id, minor));
        }

        var gels = parts.Select(p => Money.ToGel(new Money(p.Minor, currency), paidOn, rates)).ToList();
        // The payment is fully used: the last line absorbs the rounding difference.
        var fullyUsed = left <= 0 && parts.Count > 0;
        if (fullyUsed) gels[^1] = gelMinor - gels.Take(gels.Count - 1).Sum();
        var allocatedGel = gels.Sum();

        return new PaymentAllocation(
            parts.Select((p, i) => new AllocationLine(p.InstallmentId, new Money(p.Minor, currency), gels[i], rate)).ToList(),
            gelMinor - allocatedGel);
    }

    /// <summary>The most search states OrderMirrorLines visits by default before giving up with 'budget'.</summary>
    public const int MirrorOrderMaxStates = 1_000_000;

    /// <summary>OrderMirrorLines on whole tetri held in 64 bits.</summary>
    public static MirrorOrder OrderMirrorLines(IReadOnlyList<long> gelMinor, long limitMinor, IReadOnlyList<int>? hint = null, int maxStates = MirrorOrderMaxStates) =>
        OrderMirrorLines(gelMinor.Select(v => new BigInteger(v)).ToList(), new BigInteger(limitMinor), hint, maxStates);

    /// <summary>
    /// An order for the mirror lines of a receipt's reversal (Codex H60, H61). The receipt's lines,
    /// each a signed GEL amount (allocations positive, what a transfer gave back negative), were
    /// written in an order that kept every running sum between 0 and the receipt; the ledger guard
    /// checks the mirror the same way, row by row, between −receipt and 0 — but the order the lines
    /// were written in is not recorded. The lines are therefore sequenced by a depth-first search.
    ///
    /// Lines of the same amount are interchangeable for the guard, so a search state is how many lines
    /// of each distinct amount are still to place (the running sum follows from it); a state from
    /// which no order completes is remembered and not searched again. The number of states is at most
    /// the product of (count + 1) over the distinct amounts — 2 048 for 19 lines of 7 amounts (H61) —
    /// and the search tries the amounts in the order of `hint` (the lines' recorded order, usually
    /// right) first.
    ///
    /// Within `maxStates` the search is complete: it returns an order when one exists, and
    /// 'impossible' only after it has ruled every state out. For a valid history an order always
    /// exists (the original order mirrored is one), so 'impossible' means the lines are not a valid
    /// history. Should the search stop at `maxStates` first, the answer is 'budget' — not a verdict on
    /// the history. Returns the lines' indices in the order to write their mirrors.
    /// The amounts are whole tetri; the search runs on big integers, so an amount numeric(18, 2) allows
    /// keeps its last tetri (Codex PR264_OFFSET_RECEIPT_MIRROR_DECIMAL_LOSS_20261017).
    /// </summary>
    public static MirrorOrder OrderMirrorLines(IReadOnlyList<BigInteger> gelMinor, BigInteger limitMinor, IReadOnlyList<int>? hint = null, int maxStates = MirrorOrderMaxStates)
    {
        var n = gelMinor.Count;
        var sequence = hint ?? Enumerable.Range(0, n).ToList();
        var limit = limitMinor;
        // The distinct amounts, in the order they first appear in the hint, each with its lines.
        var values = new List<BigInteger>();
        var linesOf = new Dictionary<BigInteger, List<int>>();
        foreach (var i in sequence)
        {
            var v = gelMinor[i];
            if (!linesOf.TryGetValue(v, out var lines)) { linesOf[v] = lines = []; values.Add(v); }
            lines.Add(i);
        }
        var left = values.Select(v => linesOf[v].Count).ToArray();
        var dead = new HashSet<string>();
        var picks = new List<int>(); // the amount (its index in `values`) placed at each step
        var states = 0;
        var stopped = false;
        bool Search(BigInteger sum, int remaining)
        {
            if (remaining == 0) return true;
            var key = string.Join(',', left);
            if (dead.Contains(key)) return false;
            if (++states > maxStates) { stopped = true; return false; }
            for (var k = 0; k < values.Count; k++)
            {
                if (left[k] == 0) continue;
                var next = sum - values[k]; // the mirror of a line of this amount
                if (next > 0 || next < -limit) continue;
                left[k]--;
                picks.Add(k);
                if (Search(next, remaining - 1)) return true;
                picks.RemoveAt(picks.Count - 1);
                left[k]++;
                if (stopped) return false;
            }
            dead.Add(key);
            return false;
        }
        if (!Search(BigInteger.Zero, n)) return new MirrorOrder(null, stopped ? MirrorOrderReason.Budget : MirrorOrderReason.Impossible, Math.Min(states, maxStates));
        // Back to lines: each amount's lines in hint order (any of them would do for the guard).
        var nextLine = new int[values.Count];
        return new MirrorOrder(picks.Select(k => linesOf[values[k]][nextLine[k]++]).ToList(), null, states);
    }

    /// <summary>
    /// The schedule after a reduction of `reductionMinor` (an approved change; owner, 07.10.2026):
    /// LastFirst — the last instalments shrink or go to zero, as after a cancellation;
    /// EqualUnpaid — the reduction is split equally over the instalments that still hold an unpaid
    /// balance, none below what it still owes; what those balances cannot absorb comes off the last
    /// instalments. Either way an instalment may end below what was paid on it: the excess stays to
    /// the client's credit (shown as overpaid; refunded or kept as an advance by a separate action).
    /// Returns the new amount of every instalment by id. A reduction larger than the whole schedule
    /// cannot be spread and is refused.
    /// </summary>
    public static Dictionary<string, long> ReduceInstallments(IReadOnlyList<Installment> installments, IReadOnlyDictionary<string, long> settled, long reductionMinor, ReductionOrder order)
    {
        var total = installments.Sum(i => i.Amount.Minor);
        if (reductionMinor > total) throw new ArgumentOutOfRangeException(null, $"a reduction of {JsMath.Text(reductionMinor)} exceeds the schedule of {JsMath.Text(total)}");
        var amounts = new Dictionary<string, long>();
        foreach (var i in installments) amounts[i.Id] = i.Amount.Minor;
        if (reductionMinor <= 0) return amounts;
        var remaining = reductionMinor;
        if (order == ReductionOrder.EqualUnpaid)
        {
            var sorted = installments.OrderBy(i => DayNumber(i.DueOn)).ToList();
            var unpaid = new Dictionary<string, long>();
            foreach (var i in sorted) unpaid[i.Id] = Math.Max(i.Amount.Minor - (settled.TryGetValue(i.Id, out var s) ? s : 0), 0);
            // Equal shares, the odd units to the earliest; an instalment that cannot take its share
            // takes what it can and the rest is shared again among the others.
            for (var open = sorted.Where(i => unpaid[i.Id] > 0).ToList(); remaining > 0 && open.Count > 0; open = open.Where(i => unpaid[i.Id] > 0).ToList())
            {
                var share = remaining / open.Count;
                var odd = remaining - share * open.Count;
                foreach (var i in open)
                {
                    var take = Math.Min(unpaid[i.Id], share + (odd > 0 ? 1 : 0));
                    if (odd > 0) odd--;
                    unpaid[i.Id] -= take;
                    amounts[i.Id] -= take;
                    remaining -= take;
                }
            }
        }
        if (remaining > 0)
        {
            var current = installments.Select(i => i with { Amount = i.Amount with { Minor = amounts[i.Id] } }).ToList();
            var kept = new Dictionary<string, long>();
            foreach (var i in RescheduleInstallments(current, total - reductionMinor)) kept[i.Id] = i.Amount.Minor;
            foreach (var i in installments) amounts[i.Id] = kept.TryGetValue(i.Id, out var k) ? k : 0;
        }
        return amounts;
    }

    /// <summary>
    /// The schedule laid out again for a new total, as if the policy had been issued with it (owner, 13.10.2026: a change
    /// approved while the policy's own month is open corrects the policy, and its schedule with it: 4 × 100.00 becomes
    /// 4 × 125.00 on the same dates; legacy PR #218 rebuildInstallments). Every instalment keeps its date and its share of
    /// the schedule, rounded down; the last takes the rounding remainder. What was already paid is not looked at: an
    /// instalment may end above it (owed, overdue if its date has passed) or below it (the client's credit). Returns the new
    /// amount of every instalment by id. The shares are those of <paramref name="laidOut"/>, the instalments as first
    /// scheduled, when given: a second correction is laid out as the policy was issued, not by the first correction's
    /// rounded amounts (review PR218-F2: 400 → 333.33 → 500 is 4 × 125.00), and a schedule corrected to nothing keeps its
    /// shape (PR218-F1: 400 → 0 → 100 is 4 × 25.00). Without them the current amounts are the shares. A negative total, or
    /// no shares to lay a total out by, is refused (ArgumentOutOfRangeException, legacy RangeError). Share × target is
    /// exact (the old code multiplied in floating point, exact below 2^53 only).
    /// </summary>
    public static Dictionary<string, long> RebuildInstallments(IReadOnlyList<Installment> installments, long targetMinor, IReadOnlyList<Installment>? laidOut = null)
    {
        var sorted = installments.OrderBy(i => DayNumber(i.DueOn)).ToList();
        if (targetMinor < 0) throw new ArgumentOutOfRangeException(null, $"a schedule cannot be laid out for {JsMath.Text(targetMinor)}");
        if (targetMinor == 0) return sorted.ToDictionary(i => i.Id, _ => 0L);
        var current = sorted.Sum(i => i.Amount.Minor);
        var first = (laidOut ?? []).ToDictionary(i => i.Id, i => i.Amount.Minor);
        long Share(Installment i) => laidOut is { Count: > 0 } ? first.GetValueOrDefault(i.Id) : i.Amount.Minor;
        var total = sorted.Sum(Share);
        if (total <= 0) throw new ArgumentOutOfRangeException(null, $"a schedule of {JsMath.Text(current)} has no shares to lay {JsMath.Text(targetMinor)} out by");
        // Math.floor of share × target / total, the product exact in 128 bits.
        static long FloorDiv(Int128 a, long b)
        {
            var q = a / b;
            if (a % b != 0 && (a < 0) != (b < 0)) q--;
            return (long)q;
        }
        var amounts = sorted.ToDictionary(i => i.Id, i => FloorDiv((Int128)Share(i) * targetMinor, total));
        var last = sorted[^1];
        amounts[last.Id] = targetMinor - sorted.Take(sorted.Count - 1).Sum(i => amounts[i.Id]);
        return amounts;
    }

    /// <summary>
    /// Schedule after the premium changes to `targetMinor` (e.g. the retained premium after a
    /// cancellation): instalments keep their dates and order, the last ones are reduced or
    /// removed so the schedule adds up to the target. Nothing is added when the target is
    /// larger than the schedule; that case is an endorsement with its own schedule.
    /// </summary>
    public static List<Installment> RescheduleInstallments(IReadOnlyList<Installment> installments, long targetMinor)
    {
        var sorted = installments.OrderBy(i => DayNumber(i.DueOn)).ToList();
        var remaining = Math.Max(targetMinor, 0);
        var result = new List<Installment>();
        foreach (var installment in sorted)
        {
            if (remaining <= 0) break;
            var minor = Math.Min(installment.Amount.Minor, remaining);
            remaining -= minor;
            result.Add(installment with { Amount = installment.Amount with { Minor = minor } });
        }
        return result;
    }
}
