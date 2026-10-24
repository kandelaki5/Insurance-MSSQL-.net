using System.Globalization;
using System.Numerics;

namespace Insurance.Application.Services;

/// <summary>
/// How a currency is valued in GEL on a day for a report: the official rate of the day, else the last stored one with its date;
/// an error when there is none (legacy latestRateOnOrBefore). A test can pass a double of it (legacy tests replaced the module).
/// </summary>
public delegate Task<(decimal Rate, DateOnly Date)> Valuation(string currency, DateOnly date);

/// <summary>
/// The exact arithmetic of the reports (legacy premium-report.ts, impairment-report.ts, bordereau.ts): whole tetri as big
/// integers read from the stored decimals, rates to the millionth, and the rounding rules of the old code — never a double.
/// </summary>
public static class ReportMath
{
    public static readonly BigInteger RateScale = 1_000_000;

    /// <summary>A stored decimal(…, 2) amount (or a sum of them) as whole tetri, exactly (legacy exactTetri).</summary>
    public static BigInteger Tetri(decimal amount)
    {
        var scaled = amount * 100m;
        if (scaled != decimal.Truncate(scaled)) throw new ArgumentException($"not an amount in tetri: {amount.ToString(CultureInfo.InvariantCulture)}");
        return new BigInteger(scaled);
    }

    /// <summary>A stored rate (six places) in millionths: Math.round(rate × 1 000 000), exactly.</summary>
    public static BigInteger Micro(decimal rate)
    {
        var micro = rate * 1_000_000m;
        return micro == decimal.Truncate(micro) ? new BigInteger(micro) : new BigInteger(Math.Floor(micro + 0.5m));
    }

    /// <summary>n / d rounded half away from zero (finance/money.ts divRound), on big integers.</summary>
    public static BigInteger DivRound(BigInteger n, BigInteger d)
    {
        var negative = n.Sign < 0 != d.Sign < 0;
        var q = (BigInteger.Abs(n) * 2 + BigInteger.Abs(d)) / (BigInteger.Abs(d) * 2);
        return negative ? -q : q;
    }

    /// <summary>n / d to the nearest whole, a half towards +∞ — Math.round(n / d), exactly.</summary>
    public static BigInteger DivHalfUp(BigInteger n, BigInteger d)
    {
        if (d.Sign < 0) { n = -n; d = -d; }
        var t = 2 * n + d;
        var u = 2 * d;
        var q = BigInteger.Divide(t, u);
        return !(t % u).IsZero && t.Sign < 0 ? q - 1 : q;
    }

    /// <summary>Tetri of a currency in GEL at a rate to the millionth, a half towards +∞ — Math.round(tetri × rate), exactly.</summary>
    public static BigInteger AtRateHalfUp(BigInteger tetri, decimal rate) => DivHalfUp(tetri * Micro(rate), RateScale);

    /// <summary>Tetri at a rate to the millionth, half away from zero (finance/money.ts toGel / mulDiv).</summary>
    public static BigInteger AtRateAway(BigInteger tetri, decimal rate) => DivRound(tetri * Micro(rate), RateScale);

    /// <summary>Whole tetri as numeric(…, 2) text ('-0.05').</summary>
    public static string TetriText(BigInteger tetri)
    {
        var sign = tetri.Sign < 0 ? "-" : "";
        var whole = BigInteger.Abs(tetri);
        return $"{sign}{(whole / 100).ToString(CultureInfo.InvariantCulture)}.{(whole % 100).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}";
    }

    /// <summary>Whole tetri for reading, grouped by thousands ('-1,234.50'), exactly (legacy groupedTetri / reportMoney).</summary>
    public static string Grouped(BigInteger tetri)
    {
        var text = TetriText(tetri);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var sign = text.StartsWith('-') ? "-" : "";
        var digits = text[sign.Length..dot];
        var grouped = new System.Text.StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) grouped.Append(',');
            grouped.Append(digits[i]);
        }
        return sign + grouped + text[dot..];
    }

    /// <summary>A whole-tetri amount as a decimal of two places (the value a JS Number((minor / 100).toFixed(2)) gave a workbook cell).</summary>
    public static decimal Money(BigInteger tetri) => (decimal)tetri / 100m;
}
