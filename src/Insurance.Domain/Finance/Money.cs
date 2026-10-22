namespace Insurance.Domain.Finance;

/// <summary>
/// Policy currency (docs/04 rule 1). An enum: no rule depends on the text of the code except where it is shown, and the
/// names are the ISO codes the old code used as strings ('GEL' | 'USD' | 'EUR').
/// </summary>
public enum Currency
{
    GEL,
    USD,
    EUR,
}

/// <summary>National Bank of Georgia official rate: GEL per 1 unit of currency on a date.</summary>
public delegate decimal RateProvider(Currency currency, DateOnly date);

/// <summary>
/// Amount in minor units (tetri, cents) so that sums are exact. The record also carries the functions of the old
/// money.ts (money, mulDiv, toGel, fromGel) as static members.
/// </summary>
public readonly record struct Money(long Minor, Currency Currency)
{
    private const long RateScale = 1_000_000;

    /// <summary>money(amount, currency): the amount in minor units, Math.round(amount × 100).</summary>
    public static Money Of(decimal amount, Currency currency) => new(JsMath.Round(amount * 100), currency);

    /// <summary>Integer division rounding half away from zero.</summary>
    internal static Int128 DivRound(Int128 n, Int128 d)
    {
        var negative = n < 0 != d < 0;
        var absN = n < 0 ? -n : n;
        var absD = d < 0 ? -d : d;
        var q = (absN * 2 + absD) / (absD * 2);
        return negative ? -q : q;
    }

    /// <summary>amount × numerator / denominator, exact, rounded to the minor unit.</summary>
    public static long MulDiv(long minor, long numerator, long denominator)
    {
        if (denominator == 0) throw new ArgumentException("Division by zero");
        return checked((long)DivRound((Int128)minor * numerator, denominator));
    }

    private static long RateFor(Currency currency, DateOnly date, RateProvider rates)
    {
        if (currency == Currency.GEL) return RateScale;
        var rate = rates(currency, date);
        if (!(rate > 0)) throw new InvalidOperationException($"No {currency} rate for {Dates.ToIso(date)}");
        return JsMath.Round(rate * RateScale);
    }

    public static long ToGel(Money amount, DateOnly date, RateProvider rates) =>
        MulDiv(amount.Minor, RateFor(amount.Currency, date, rates), RateScale);

    public static Money FromGel(long gelMinor, Currency currency, DateOnly date, RateProvider rates) =>
        new(MulDiv(gelMinor, RateScale, RateFor(currency, date, rates)), currency);
}
