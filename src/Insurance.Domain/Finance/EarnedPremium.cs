using System.Numerics;
using static Insurance.Domain.Finance.Dates;

namespace Insurance.Domain.Finance;

/// <summary>
/// One movement of written premium. Endorsements and cancellations are further
/// transactions (possibly negative) with their own cover period; nothing is edited.
///
/// The cover period runs from <see cref="CoverFrom"/> (the date the policy or the change takes
/// effect) up to but not including <see cref="CoverTo"/>, so its length is `CoverTo − CoverFrom`
/// days. A cancellation is a negative transaction from the cancel date to the policy
/// end that reverses the original's unearned part; it is tied to the original through
/// <see cref="Cancels"/>, so it earns exactly what the original would have earned after the cancel
/// date and is valued in GEL at the original's rate.
/// </summary>
/// <param name="BookedOn">Date the premium is recognized in the books.</param>
/// <param name="Cancels">For a cancellation: the transaction it reverses.</param>
public sealed record PremiumTransaction(Money Amount, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, PremiumTransaction? Cancels = null);

public sealed record PremiumPosition(long Written, long Earned, long Unearned);

/// <summary>
/// A premium movement in whole minor units, exactly (INS-FORM1-DATA-FIDELITY-FIX-OFFLINE-20261019): the same row as
/// <see cref="PremiumTransaction"/>, its amount a big integer read from the stored text, so the largest amounts keep their last tetri.
/// </summary>
public sealed record ExactPremiumTx(BigInteger Minor, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, ExactPremiumTx? Cancels = null);

/// <summary>A premium ledger row as read: its id, the id of the row it cancels (if any), and its transaction.</summary>
public sealed record LedgerRow(string Id, string? CancelsId, PremiumTransaction Tx);

/// <summary>Earned premium, UPR and cancellations (earned-premium.ts).</summary>
public static class EarnedPremium
{
    /// <summary>n / d rounded half away from zero, on big integers (Money.MulDiv's rule); d is positive.</summary>
    private static BigInteger DivAway(BigInteger n, BigInteger d)
    {
        var q = (BigInteger.Abs(n) * 2 + d) / (2 * d);
        return n < 0 ? -q : q;
    }

    /// <summary>EarnedAsOf exactly, on big integers: the same rule, line for line.</summary>
    public static BigInteger EarnedAsOfExact(ExactPremiumTx tx, DateOnly asOf)
    {
        if (DayNumber(asOf) < DayNumber(tx.BookedOn)) return BigInteger.Zero;
        if (tx.Cancels is not null) return -(EarnedAsOfExact(tx.Cancels, asOf) - EarnedAsOfExact(tx.Cancels, AddDays(tx.CoverFrom, -1)));
        var total = DaysBetween(tx.CoverFrom, tx.CoverTo);
        if (total <= 0) throw new ArgumentException($"Cover period ends before it starts: {ToIso(tx.CoverFrom)}..{ToIso(tx.CoverTo)}");
        var elapsed = Math.Min(Math.Max(DaysBetween(tx.CoverFrom, asOf) + 1, 0), total);
        return DivAway(tx.Minor * elapsed, total);
    }

    /// <summary>Earned part of one transaction as of the end of a date, daily pro rata.</summary>
    public static long EarnedAsOf(PremiumTransaction tx, DateOnly asOf)
    {
        if (DayNumber(asOf) < DayNumber(tx.BookedOn)) return 0;
        if (tx.Cancels is not null)
        {
            // Mirrors the original's earning from the cancel date on, so the pair earns exactly
            // the retained premium and never a tetri more or less.
            return -(EarnedAsOf(tx.Cancels, asOf) - EarnedAsOf(tx.Cancels, AddDays(tx.CoverFrom, -1)));
        }
        var total = DaysBetween(tx.CoverFrom, tx.CoverTo);
        if (total <= 0) throw new ArgumentException($"Cover period ends before it starts: {ToIso(tx.CoverFrom)}..{ToIso(tx.CoverTo)}");
        var elapsed = Math.Min(Math.Max(DaysBetween(tx.CoverFrom, asOf) + 1, 0), total);
        return Money.MulDiv(tx.Amount.Minor, elapsed, total);
    }

    /// <summary>
    /// Written, earned and unearned premium (UPR) as of a date, per policy currency. Only the currencies that have a
    /// transaction booked by the date appear, in the order they first occur.
    /// </summary>
    public static IReadOnlyDictionary<Currency, PremiumPosition> PremiumPositionOf(IEnumerable<PremiumTransaction> txs, DateOnly asOf)
    {
        var result = new Dictionary<Currency, PremiumPosition>();
        foreach (var tx in txs)
        {
            if (DayNumber(asOf) < DayNumber(tx.BookedOn)) continue;
            var pos = result.TryGetValue(tx.Amount.Currency, out var p) ? p : new PremiumPosition(0, 0, 0);
            var earned = EarnedAsOf(tx, asOf);
            result[tx.Amount.Currency] = new PremiumPosition(pos.Written + tx.Amount.Minor, pos.Earned + earned, pos.Unearned + (tx.Amount.Minor - earned));
        }
        return result;
    }

    /// <summary>Premium earned within a reporting period (both dates included), per currency.</summary>
    public static IReadOnlyDictionary<Currency, long> EarnedInPeriod(IReadOnlyCollection<PremiumTransaction> txs, DateOnly from, DateOnly to)
    {
        var opening = PremiumPositionOf(txs, AddDays(from, -1));
        var closing = PremiumPositionOf(txs, to);
        var result = new Dictionary<Currency, long>();
        foreach (var (currency, position) in closing)
        {
            result[currency] = position.Earned - (opening.TryGetValue(currency, out var o) ? o.Earned : 0);
        }
        return result;
    }

    /// <summary>
    /// The same transaction valued in GEL at the official rate of the date it took effect
    /// (reserves order №01 of 24.12.2013, art. 5). A cancellation is derived from the GEL
    /// value of the original, so later rate changes never touch a policy's valuation.
    /// </summary>
    public static PremiumTransaction InGel(PremiumTransaction tx, RateProvider rates)
    {
        if (tx.Cancels?.Cancels is not null) return ReversalOf(InGel(tx.Cancels, rates), tx.BookedOn);
        if (tx.Cancels is not null)
        {
            return CancellationOf(InGel(tx.Cancels, rates), tx.CoverFrom)
                ?? throw new InvalidOperationException($"Cancellation on {ToIso(tx.CoverFrom)} is outside the cover it reverses");
        }
        return tx with { Amount = new Money(Money.ToGel(tx.Amount, tx.CoverFrom, rates), Currency.GEL) };
    }

    /// <summary>Statutory position in GEL: every transaction at its own fixed rate.</summary>
    public static PremiumPosition PremiumPositionGel(IEnumerable<PremiumTransaction> txs, DateOnly asOf, RateProvider rates)
    {
        long written = 0, earned = 0, unearned = 0;
        foreach (var tx in txs)
        {
            if (DayNumber(asOf) < DayNumber(tx.BookedOn)) continue;
            var gel = InGel(tx, rates);
            var e = EarnedAsOf(gel, asOf);
            written += gel.Amount.Minor;
            earned += e;
            unearned += gel.Amount.Minor - e;
        }
        return new PremiumPosition(written, earned, unearned);
    }

    /// <summary>
    /// Links each cancellation to the row it cancels, whatever order the rows were read in (review
    /// H14: rows written in the same instant may come back cancellation first). All rows are known
    /// before any link is made. A cancellation whose original is not among the rows is an error —
    /// it is never silently treated as a plain negative premium, which would earn differently.
    /// Returns each row's transaction, in the rows' order, with <see cref="PremiumTransaction.Cancels"/> set (records are
    /// immutable, so linked copies are returned; the old code set the link on the rows it was given). When two rows share
    /// an id, the later one is the one linked to, as before. Rows that cancel each other in a circle are refused.
    /// </summary>
    public static List<PremiumTransaction> LinkLedger(IReadOnlyList<LedgerRow> rows)
    {
        var byId = new Dictionary<string, int>();
        for (var i = 0; i < rows.Count; i++) byId[rows[i].Id] = i;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.CancelsId)) continue;
            if (!byId.ContainsKey(row.CancelsId)) throw new InvalidOperationException($"Ledger row {row.Id} cancels {row.CancelsId}, which is not among the rows read");
        }
        var linked = new PremiumTransaction?[rows.Count];
        var linking = new bool[rows.Count];
        PremiumTransaction Link(int i)
        {
            if (linked[i] is { } done) return done;
            var row = rows[i];
            if (string.IsNullOrEmpty(row.CancelsId)) return linked[i] = row.Tx;
            if (linking[i]) throw new InvalidOperationException($"Ledger row {row.Id} cancels itself through {row.CancelsId}");
            linking[i] = true;
            var tx = row.Tx with { Cancels = Link(byId[row.CancelsId]) };
            linking[i] = false;
            return linked[i] = tx;
        }
        return Enumerable.Range(0, rows.Count).Select(Link).ToList();
    }

    /// <summary>
    /// Ledger row that undoes a cancellation, booked on the same day as it (or in the first open
    /// month): the pair nets to nothing from that day on. Used when a later cancellation is brought
    /// forward to a total loss (H24): a full cancellation from the loss date is added and the later
    /// one is undone on its own day, so written premium, UPR and earned premium are right on every
    /// date and no booked row is changed. It mirrors the cancellation it undoes, with the opposite sign.
    /// </summary>
    public static PremiumTransaction ReversalOf(PremiumTransaction cancellation, DateOnly bookedOn)
    {
        if (cancellation.Cancels is null || cancellation.Cancels.Cancels is not null) throw new InvalidOperationException("Only a cancellation of an original row can be reversed");
        return cancellation with { Amount = cancellation.Amount with { Minor = -cancellation.Amount.Minor }, BookedOn = bookedOn, Cancels = cancellation };
    }

    /// <summary>
    /// Ledger row that cancels a transaction from a date: cover ends at the start of
    /// `cancelOn` and exactly the unearned part at that moment is returned, with no
    /// cancellation fee. Returns null when the cover has already run out.
    /// </summary>
    public static PremiumTransaction? CancellationOf(PremiumTransaction tx, DateOnly cancelOn)
    {
        if (tx.Cancels is not null) throw new InvalidOperationException("A cancellation cannot be cancelled; reverse the original instead");
        if (DayNumber(cancelOn) >= DayNumber(tx.CoverTo)) return null;
        var coverFrom = DayNumber(cancelOn) > DayNumber(tx.CoverFrom) ? cancelOn : tx.CoverFrom;
        var refund = tx.Amount.Minor - EarnedAsOf(tx, AddDays(coverFrom, -1));
        return new PremiumTransaction(new Money(-refund, tx.Amount.Currency), cancelOn, coverFrom, tx.CoverTo, tx);
    }
}
