using System.Numerics;
using static Insurance.Domain.Finance.Dates;

namespace Insurance.Domain.Finance;

/// <summary>Age of earned premium on the reporting date, oldest first.</summary>
public enum AgeBand
{
    Over12,
    M9to12,
    M6to9,
    M3to6,
    M0to3,
}

/// <summary>One whole number per age band (minor units, or a percent per band).</summary>
public sealed record ByBand(long Over12, long M9to12, long M6to9, long M3to6, long M0to3)
{
    public static readonly ByBand Zero = new(0, 0, 0, 0, 0);

    public long this[AgeBand band] => band switch
    {
        AgeBand.Over12 => Over12,
        AgeBand.M9to12 => M9to12,
        AgeBand.M6to9 => M6to9,
        AgeBand.M3to6 => M3to6,
        AgeBand.M0to3 => M0to3,
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };

    public static ByBand From(Func<AgeBand, long> value) =>
        new(value(AgeBand.Over12), value(AgeBand.M9to12), value(AgeBand.M6to9), value(AgeBand.M3to6), value(AgeBand.M0to3));
}

/// <summary>One big integer per age band.</summary>
public sealed record ByBandExact(BigInteger Over12, BigInteger M9to12, BigInteger M6to9, BigInteger M3to6, BigInteger M0to3)
{
    public static readonly ByBandExact Zero = new(0, 0, 0, 0, 0);

    public BigInteger this[AgeBand band] => band switch
    {
        AgeBand.Over12 => Over12,
        AgeBand.M9to12 => M9to12,
        AgeBand.M6to9 => M6to9,
        AgeBand.M3to6 => M3to6,
        AgeBand.M0to3 => M0to3,
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };

    public static ByBandExact From(Func<AgeBand, BigInteger> value) =>
        new(value(AgeBand.Over12), value(AgeBand.M9to12), value(AgeBand.M6to9), value(AgeBand.M3to6), value(AgeBand.M0to3));
}

/// <summary>The last day of each older band (see <see cref="Impairment.BandCutoffs"/>).</summary>
public sealed record BandCutoffs(DateOnly M12, DateOnly M9, DateOnly M6, DateOnly M3);

/// <summary>Impairment of one policy (the old Impairment).</summary>
/// <param name="UnpaidEarned">Earned premium not yet paid (never negative: prepayment covers nothing in the past).</param>
/// <param name="Unpaid">The unpaid earned premium laid over the bands, newest first.</param>
public sealed record ImpairmentResult(ByBand Earned, long TotalEarned, long Paid, long UnpaidEarned, ByBand Unpaid, ByBand Reserve, long TotalReserve);

/// <summary>Impairment of one policy on big integers (the old ImpairmentExact).</summary>
public sealed record ImpairmentResultExact(ByBandExact Earned, BigInteger TotalEarned, BigInteger Paid, BigInteger UnpaidEarned, ByBandExact Unpaid, ByBandExact Reserve, BigInteger TotalReserve);

/// <summary>
/// Impairment reserve on insurance receivables (financial reporting order №02/2013, annex 2,
/// art. 2.50; the owner's calculation template "gaufasurebis rezervis kalkulacia"). (impairment.ts)
///
/// Per policy, the base is the earned premium not yet paid. Earned premium is aged by when it
/// was earned: what was earned more than a year before the reporting date, 9–12 months, 6–9,
/// 3–6 and in the last 3 months. Payments cover the oldest earned premium first, so what is
/// still unpaid is the most recently earned part: the unpaid amount is laid over the bands
/// from the newest back. Each band carries its reserve percent (100 / 50 / 30 / 10 / 0).
/// </summary>
public static class Impairment
{
    /// <summary>Age bands, oldest first.</summary>
    public static readonly IReadOnlyList<AgeBand> AgeBands = [AgeBand.Over12, AgeBand.M9to12, AgeBand.M6to9, AgeBand.M3to6, AgeBand.M0to3];

    /// <summary>Reserve percent per band (order №02 annex 2 art. 2.50).</summary>
    public static readonly ByBand ImpairmentPercent = new(100, 50, 30, 10, 0);

    /// <summary>
    /// The last day of each older band for a reporting date: 12, 9, 6 and 3 whole months before
    /// the day after it. For 31.12.2023: 31.12.2022, 31.03.2023, 30.06.2023, 30.09.2023. The bands
    /// follow the template's column headings, as the owner confirmed (04.10.2026); the dates typed
    /// above the columns in the template (1 March, 1 June, 1 September) are a mistake there.
    /// </summary>
    public static BandCutoffs BandCutoffsOf(DateOnly asOf)
    {
        var next = AddDays(asOf, 1);
        DateOnly Back(int months) => AddDays(AddMonths(next, -months), -1);
        return new BandCutoffs(Back(12), Back(9), Back(6), Back(3));
    }

    /// <summary>
    /// Ageing follows the cover days, not the booking date: premium booked late for an earlier
    /// cover was still earned on those days. Moving the booking back to the cover start (never
    /// forward) keeps cancellations tied to their own date.
    /// </summary>
    private static PremiumTransaction ByCoverDays(PremiumTransaction tx)
    {
        var bookedOn = DayNumber(tx.CoverFrom) < DayNumber(tx.BookedOn) ? tx.CoverFrom : tx.BookedOn;
        return tx with { BookedOn = bookedOn, Cancels = tx.Cancels is null ? null : ByCoverDays(tx.Cancels) };
    }

    /// <summary>Earned premium of a policy split by how long ago it was earned (minor units, policy currency).</summary>
    public static ByBand EarnedByAge(IEnumerable<PremiumTransaction> txs, DateOnly asOf)
    {
        var counted = txs.Where(tx => DayNumber(tx.BookedOn) <= DayNumber(asOf)).Select(ByCoverDays).ToList();
        long Earned(DateOnly date) => counted.Sum(tx => EarnedPremium.EarnedAsOf(tx, date));
        var c = BandCutoffsOf(asOf);
        long m12 = Earned(c.M12), m9 = Earned(c.M9), m6 = Earned(c.M6), m3 = Earned(c.M3), all = Earned(asOf);
        return new ByBand(
            Math.Max(0, m12),
            Math.Max(0, m9 - m12),
            Math.Max(0, m6 - m9),
            Math.Max(0, m3 - m6),
            Math.Max(0, all - m3));
    }

    /// <summary>Impairment reserve of one policy from its premium ledger rows and the premium paid by the reporting date.</summary>
    public static ImpairmentResult ImpairmentOf(IEnumerable<PremiumTransaction> txs, long paidMinor, DateOnly asOf, ByBand? percent = null)
    {
        percent ??= ImpairmentPercent;
        var earned = EarnedByAge(txs, asOf);
        var totalEarned = AgeBands.Sum(band => earned[band]);
        var unpaidEarned = Math.Max(0, totalEarned - paidMinor);
        var unpaid = new Dictionary<AgeBand, long>();
        var left = unpaidEarned;
        foreach (var band in AgeBands.Reverse())
        {
            unpaid[band] = Math.Min(earned[band], left);
            left -= unpaid[band];
        }
        var unpaidByBand = ByBand.From(b => unpaid[b]);
        var reserve = ByBand.From(b => Money.MulDiv(unpaidByBand[b], percent[b], 100));
        return new ImpairmentResult(earned, totalEarned, paidMinor, unpaidEarned, unpaidByBand, reserve, AgeBands.Sum(b => reserve[b]));
    }

    // ---- Exactly, on big integers (INS-FORM1-DATA-FIDELITY-FIX-OFFLINE-20261019): the same rules as EarnedByAge and ImpairmentOf, line
    // for line, over amounts read from their stored text — through JS numbers 90071992547408.01 became …408.02. For N1-4 of form 1.

    private static BigInteger MaxZero(BigInteger x) => x < 0 ? BigInteger.Zero : x;

    private static ExactPremiumTx ByCoverDaysExact(ExactPremiumTx tx)
    {
        var bookedOn = DayNumber(tx.CoverFrom) < DayNumber(tx.BookedOn) ? tx.CoverFrom : tx.BookedOn;
        return tx with { BookedOn = bookedOn, Cancels = tx.Cancels is null ? null : ByCoverDaysExact(tx.Cancels) };
    }

    /// <summary>EarnedByAge exactly.</summary>
    public static ByBandExact EarnedByAgeExact(IEnumerable<ExactPremiumTx> txs, DateOnly asOf)
    {
        var counted = txs.Where(tx => DayNumber(tx.BookedOn) <= DayNumber(asOf)).Select(ByCoverDaysExact).ToList();
        BigInteger Earned(DateOnly date) => counted.Aggregate(BigInteger.Zero, (sum, tx) => sum + EarnedPremium.EarnedAsOfExact(tx, date));
        var c = BandCutoffsOf(asOf);
        BigInteger m12 = Earned(c.M12), m9 = Earned(c.M9), m6 = Earned(c.M6), m3 = Earned(c.M3), all = Earned(asOf);
        return new ByBandExact(MaxZero(m12), MaxZero(m9 - m12), MaxZero(m6 - m9), MaxZero(m3 - m6), MaxZero(all - m3));
    }

    /// <summary>ImpairmentOf exactly: each band's reserve its percent of the unpaid, half away from zero (MulDiv's rule).</summary>
    public static ImpairmentResultExact ImpairmentOfExact(IEnumerable<ExactPremiumTx> txs, BigInteger paid, DateOnly asOf, ByBand? percent = null)
    {
        percent ??= ImpairmentPercent;
        var earned = EarnedByAgeExact(txs, asOf);
        var totalEarned = AgeBands.Aggregate(BigInteger.Zero, (sum, band) => sum + earned[band]);
        var unpaidEarned = MaxZero(totalEarned - paid);
        var unpaid = new Dictionary<AgeBand, BigInteger>();
        var left = unpaidEarned;
        foreach (var band in AgeBands.Reverse())
        {
            unpaid[band] = earned[band] < left ? earned[band] : left;
            left -= unpaid[band];
        }
        var unpaidByBand = ByBandExact.From(b => unpaid[b]);
        var reserve = ByBandExact.From(b =>
        {
            var n = unpaidByBand[b] * percent[b];
            var q = (BigInteger.Abs(n) * 2 + 100) / 200;
            return n < 0 ? -q : q;
        });
        return new ImpairmentResultExact(earned, totalEarned, paid, unpaidEarned, unpaidByBand, reserve, AgeBands.Aggregate(BigInteger.Zero, (sum, b) => sum + reserve[b]));
    }

    /// <summary>
    /// A receivable from a broker is not impaired (owner, 19.10.2026): its earned and unpaid premium are aged as any other, the
    /// reserve is 0 in every band.
    /// </summary>
    public static ImpairmentResult WithoutReserve(ImpairmentResult impairment) => impairment with { Reserve = ByBand.Zero, TotalReserve = 0 };
}
