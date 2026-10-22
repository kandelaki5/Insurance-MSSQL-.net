using System.Numerics;
using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.Dates;

namespace Insurance.Application.Services;

/// <summary>
/// The policy card's figures, exactly (legacy policies/[id]/page.tsx exactReceivable, exactCancellableFrom,
/// exactPremiumPosition and payment/page.tsx exactOutstanding): the finance core's rules step for step, in whole tetri as
/// big integers from the stored amounts, so a tetri on the largest amounts numeric(18, 2) allows is neither lost nor doubled.
/// </summary>
public static class PolicyCard
{
    private static readonly BigInteger RateScale = 1_000_000;

    private static BigInteger Exact(decimal amount) => PaymentActions.Exact(amount);

    /// <summary>Math.round(rate × 10^6) for a foreign currency (an error without a rate above 0); 10^6 for GEL.</summary>
    private static BigInteger Scaled(string currency, decimal rate, DateOnly? asOf = null)
    {
        if (currency == "GEL") return RateScale;
        if (!(rate > 0)) throw new InvalidOperationException(asOf is { } d ? $"No {currency} rate for {ToIso(d)}" : $"No {currency} rate");
        return new BigInteger(Math.Round(rate * 1_000_000m, MidpointRounding.AwayFromZero));
    }

    /// <summary>finance mulDiv: n × num / den, rounded half away from zero, for either sign.</summary>
    public static BigInteger MulDiv(BigInteger n, BigInteger num, BigInteger den)
    {
        var p = n * num;
        var negative = p < 0 != den < 0;
        var absD = BigInteger.Abs(den);
        var q = (BigInteger.Abs(p) * 2 + absD) / (absD * 2);
        return negative ? -q : q;
    }

    /// <summary>One instalment's balance on the card, in whole tetri of the policy currency.</summary>
    public sealed record Balance(Guid Id, DateOnly DueOn, BigInteger Amount, BigInteger Outstanding);

    public sealed record Receivable(IReadOnlyList<Balance> Balances, BigInteger Outstanding, BigInteger Overdue, BigInteger Overpaid, BigInteger OutstandingGel, Func<BigInteger, BigInteger> InGel);

    /// <summary>
    /// receivableAsOf exactly: credits received by <paramref name="asOf"/> settle the instalments oldest due date first,
    /// signed as stored; what is unpaid, what of it is overdue (due before the day), what is paid beyond the schedule, and
    /// the unpaid part in GEL at the reporting day's rate (to the millionth, half away from zero).
    /// </summary>
    public static Receivable ExactReceivable(IEnumerable<Billing.InstallmentRow> schedule, IEnumerable<Billing.AllocationRow> credits, DateOnly asOf, string currency, decimal rate)
    {
        var available = credits.Where(c => c.ReceivedOn <= asOf).Aggregate(BigInteger.Zero, (s, c) => s + Exact(c.Amount));
        var balances = new List<Balance>();
        foreach (var row in schedule.OrderBy(r => r.DueOn))
        {
            var amount = Exact(row.Amount);
            var settled = available < amount ? available : amount;
            available -= settled;
            balances.Add(new Balance(row.Id, row.DueOn, amount, amount - settled));
        }
        var outstanding = balances.Aggregate(BigInteger.Zero, (s, b) => s + b.Outstanding);
        var overdue = balances.Where(b => b.DueOn < asOf).Aggregate(BigInteger.Zero, (s, b) => s + b.Outstanding);
        var scaled = Scaled(currency, rate, asOf);
        BigInteger InGel(BigInteger tetri)
        {
            var n = tetri * scaled;
            var q = (BigInteger.Abs(n) * 2 + RateScale) / (RateScale * 2);
            return n < 0 ? -q : q;
        }
        return new Receivable(balances, outstanding, overdue, available, InGel(outstanding), InGel);
    }

    /// <summary>What the policy still owes on the day (payment page), receivableAsOf's rule exactly; no rate needed.</summary>
    public static BigInteger ExactOutstanding(IEnumerable<Billing.InstallmentRow> schedule, IEnumerable<Billing.AllocationRow> credits, DateOnly asOf)
    {
        var available = credits.Where(c => c.ReceivedOn <= asOf).Aggregate(BigInteger.Zero, (s, c) => s + Exact(c.Amount));
        BigInteger outstanding = 0;
        foreach (var row in schedule.OrderBy(r => r.DueOn))
        {
            var amount = Exact(row.Amount);
            var settled = available < amount ? available : amount;
            available -= settled;
            outstanding += amount - settled;
        }
        return outstanding;
    }

    /// <summary>cancellableFrom on the exact balances: the oldest unpaid instalment's due date plus the grace; null without a grace.</summary>
    public static DateOnly? ExactCancellableFrom(IReadOnlyList<Balance> balances, int? graceDays)
    {
        if (graceDays is not { } grace) return null;
        var oldest = balances.FirstOrDefault(b => b.Outstanding > 0);
        return oldest is null ? null : AddDays(oldest.DueOn, grace);
    }

    /// <summary>A premium ledger row as read for the card.</summary>
    public sealed record LedgerRow(Guid Id, Guid? CancelsId, decimal Amount, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo);

    public sealed record Position(BigInteger Written, BigInteger Earned, BigInteger Unearned);

    private sealed class ExactTx(BigInteger amount, DateOnly bookedOn, DateOnly coverFrom, DateOnly coverTo, ExactTx? cancels = null)
    {
        public BigInteger Amount { get; } = amount;
        public DateOnly BookedOn { get; } = bookedOn;
        public DateOnly CoverFrom { get; } = coverFrom;
        public DateOnly CoverTo { get; } = coverTo;
        public ExactTx? Cancels { get; set; } = cancels;
    }

    /// <summary>
    /// The card's premium position (earned-premium linkLedger, earnedAsOf, premiumPosition, inGel and
    /// premiumPositionGel) step for step on big integers: daily pro rata from coverFrom up to but not including coverTo,
    /// each movement rounded on its own; a cancellation mirrors its original from the cancel date on and is valued in GEL
    /// from the original's GEL; nothing booked after <paramref name="asOf"/> counts. With <paramref name="basisPoints"/>
    /// below 10 000 each row is its commission instead (amount × percent, half away from zero, each movement on its own),
    /// earned and valued exactly as the premium (docs/04 rule 11).
    /// </summary>
    public static (Position Position, Position PositionGel) ExactPremiumPosition(IReadOnlyList<LedgerRow> rows, DateOnly asOf, string currency, decimal fixedRate, long basisPoints = 10_000)
    {
        var byId = new Dictionary<Guid, ExactTx>();
        foreach (var row in rows)
            byId[row.Id] = new ExactTx(MulDiv(Exact(row.Amount), basisPoints, 10_000), row.BookedOn, row.CoverFrom, row.CoverTo);
        foreach (var row in rows)
        {
            if (row.CancelsId is not { } cancels) continue;
            if (!byId.TryGetValue(cancels, out var original)) throw new InvalidOperationException($"Ledger row {row.Id} cancels {cancels}, which is not among the rows read");
            byId[row.Id].Cancels = original;
        }
        var txs = rows.Select(r => byId[r.Id]).ToList();

        static BigInteger EarnedAsOf(ExactTx tx, DateOnly day)
        {
            if (DayNumber(day) < DayNumber(tx.BookedOn)) return 0;
            if (tx.Cancels is not null) return -(EarnedAsOf(tx.Cancels, day) - EarnedAsOf(tx.Cancels, AddDays(tx.CoverFrom, -1)));
            var total = DaysBetween(tx.CoverFrom, tx.CoverTo);
            if (total <= 0) throw new InvalidOperationException($"Cover period ends before it starts: {ToIso(tx.CoverFrom)}..{ToIso(tx.CoverTo)}");
            var elapsed = Math.Min(Math.Max(DaysBetween(tx.CoverFrom, day) + 1, 0), total);
            return MulDiv(tx.Amount, elapsed, total);
        }
        BigInteger Scale() => Scaled(currency, fixedRate);
        ExactTx? CancellationOf(ExactTx tx, DateOnly cancelOn)
        {
            if (tx.Cancels is not null) throw new InvalidOperationException("A cancellation cannot be cancelled; reverse the original instead");
            if (DayNumber(cancelOn) >= DayNumber(tx.CoverTo)) return null;
            var coverFrom = DayNumber(cancelOn) > DayNumber(tx.CoverFrom) ? cancelOn : tx.CoverFrom;
            var refund = tx.Amount - EarnedAsOf(tx, AddDays(coverFrom, -1));
            return new ExactTx(-refund, cancelOn, coverFrom, tx.CoverTo, tx);
        }
        static ExactTx ReversalOf(ExactTx cancellation, DateOnly bookedOn)
        {
            if (cancellation.Cancels is null || cancellation.Cancels.Cancels is not null) throw new InvalidOperationException("Only a cancellation of an original row can be reversed");
            return new ExactTx(-cancellation.Amount, bookedOn, cancellation.CoverFrom, cancellation.CoverTo, cancellation);
        }
        ExactTx InGel(ExactTx tx)
        {
            if (tx.Cancels?.Cancels is not null) return ReversalOf(InGel(tx.Cancels), tx.BookedOn);
            if (tx.Cancels is not null)
                return CancellationOf(InGel(tx.Cancels), tx.CoverFrom) ?? throw new InvalidOperationException($"Cancellation on {ToIso(tx.CoverFrom)} is outside the cover it reverses");
            return new ExactTx(MulDiv(tx.Amount, Scale(), RateScale), tx.BookedOn, tx.CoverFrom, tx.CoverTo);
        }

        BigInteger written = 0, earned = 0, unearned = 0, writtenGel = 0, earnedGel = 0, unearnedGel = 0;
        foreach (var tx in txs)
        {
            if (DayNumber(asOf) < DayNumber(tx.BookedOn)) continue;
            var e = EarnedAsOf(tx, asOf);
            written += tx.Amount;
            earned += e;
            unearned += tx.Amount - e;
            var gel = InGel(tx);
            var eg = EarnedAsOf(gel, asOf);
            writtenGel += gel.Amount;
            earnedGel += eg;
            unearnedGel += gel.Amount - eg;
        }
        return (new Position(written, earned, unearned), new Position(writtenGel, earnedGel, unearnedGel));
    }

    /// <summary>Whole tetri for reading ('-1,234.50'), exactly.</summary>
    public static string Grouped(BigInteger tetri) => PremiumReport.Money(tetri);

    /// <summary>A stored amount as the card shows it, exactly: grouping, two decimals, the sign.</summary>
    public static string Stored(decimal amount) => Grouped(Exact(amount));

    /// <summary>
    /// Whole tetri from the address bar (a receipt's, a transfer's or an allocation's answer), exactly; anything else —
    /// missing, empty, a fraction, an exponent, a sign — is null, so no notice shows a wrong amount. At most 30 digits.
    /// </summary>
    public static BigInteger? TetriParam(string? value) =>
        value is { Length: >= 1 and <= 30 } && value.All(c => c is >= '0' and <= '9') ? BigInteger.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null;
}
