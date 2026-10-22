using System.Numerics;

namespace Insurance.Domain.Finance;

/// <summary>Line of business for the statutory IBNR rate.</summary>
public enum IbnrLine
{
    Health,
    Other,
}

/// <summary>Statutory reserves (reserves.ts).</summary>
public static class Reserves
{
    /// <summary>IBNR rates of the base premium (reserves order №01 of 24.12.2013, art. 7.4).</summary>
    private static int IbnrPercent(IbnrLine line) => line switch
    {
        IbnrLine.Health => 2,
        IbnrLine.Other => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(line)),
    };

    /// <summary>
    /// Statutory IBNR for one line of business at year end: a fixed percentage of the
    /// year's net written premium (gross less ceded). No reinsurer's share is recognised.
    /// </summary>
    public static long StatutoryIbnr(long grossWrittenGel, long cededGel, IbnrLine line) =>
        Money.MulDiv(Math.Max(grossWrittenGel - cededGel, 0), IbnrPercent(line), 100);

    /// <summary>
    /// StatutoryIbnr's own rule on whole tetri as big integers, for amounts beyond what a JS number holds exactly
    /// (MONTH_REPORT_IBNR_DECIMAL_PRECISION_20261019): the same percentages, the net never below zero, rounded to the tetri half away
    /// from zero (MulDiv's rounding; the net is never negative, so a half goes up).
    /// </summary>
    public static BigInteger StatutoryIbnrExact(BigInteger grossWrittenGel, BigInteger cededGel, IbnrLine line)
    {
        var net = grossWrittenGel > cededGel ? grossWrittenGel - cededGel : BigInteger.Zero;
        return (net * IbnrPercent(line) * 2 + 100) / 200;
    }
}
