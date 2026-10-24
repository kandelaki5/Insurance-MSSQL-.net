using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Insurance.Integration.Tests.Accounting;

/// <summary>
/// The independent integer oracle of the legacy month-report checks (scripts/integration/month-report-cases.ts and
/// month-report-scenario.ts, ported as written there — never a production helper): stored text to whole tetri, a row's earned to the
/// end of a day half away from zero, a currency in GEL at a rate to the millionth (half away from zero, or half towards +∞ as
/// Math.round did), proportional slices from a running total.
/// </summary>
public static partial class MonthReportOracle
{
    [GeneratedRegex(@"^(-?)(\d+)\.(\d{2})$")]
    private static partial Regex AmountText();

    /// <summary>numeric(…, 2) text to whole tetri, exactly ("90071992547408.01" → 9007199254740801).</summary>
    public static BigInteger T(string text)
    {
        var m = AmountText().Match(text);
        if (!m.Success) throw new ArgumentException($"not an amount: {text}");
        var t = BigInteger.Parse(m.Groups[2].Value + m.Groups[3].Value, CultureInfo.InvariantCulture);
        return m.Groups[1].Value == "-" ? -t : t;
    }

    /// <summary>Whole tetri as numeric(…, 2) text (legacy amount()).</summary>
    public static string Amount(BigInteger t)
    {
        var a = BigInteger.Abs(t);
        return $"{(t < 0 ? "-" : "")}{a / 100}.{(int)(a % 100):D2}";
    }

    /// <summary>The value as decimal(18, 2) (for writing a row).</summary>
    public static decimal Dec(BigInteger t) => decimal.Parse(Amount(t), CultureInfo.InvariantCulture);

    /// <summary>n / d, a half away from zero (d &gt; 0).</summary>
    public static BigInteger Away(BigInteger n, BigInteger d)
    {
        var a = BigInteger.Abs(n);
        var q = (a * 2 + d) / (2 * d);
        return n < 0 ? -q : q;
    }

    /// <summary>n / d, a half towards +∞ (Math.round's rule), for either sign of d (legacy `up`).</summary>
    public static BigInteger Up(BigInteger n, BigInteger d)
    {
        if (d < 0) { n = -n; d = -d; }
        var fl = n >= 0 ? n / d : -((-n + d - 1) / d);
        return (n - fl * d) * 2 >= d ? fl + 1 : fl;
    }

    /// <summary>A row's earned to the end of its k-th day of <paramref name="total"/>, half away from zero (owner, 15.10.2026).</summary>
    public static BigInteger EarnedTo(BigInteger minor, int k, int total) => Away(minor * Math.Clamp(k, 0, total), total);

    /// <summary>A rate's text ("2.8", "2.800000") in millionths.</summary>
    public static BigInteger Millionths(string rate)
    {
        var parts = rate.Split('.');
        var frac = (parts.Length > 1 ? parts[1] : "").PadRight(6, '0');
        if (frac.Length != 6) throw new ArgumentException($"not a rate to the millionth: {rate}");
        return BigInteger.Parse(parts[0] + frac, CultureInfo.InvariantCulture);
    }

    /// <summary>Tetri of a currency in GEL at a rate, half away from zero (a receivable, a payment's GEL).</summary>
    public static BigInteger AtRateAway(BigInteger minor, string rate) => Away(minor * Millionths(rate), 1_000_000);

    /// <summary>Tetri of a currency in GEL at a rate, half towards +∞ (a reserve, a share, the treaty premium).</summary>
    public static BigInteger AtRateUp(BigInteger minor, string rate) => Up(minor * Millionths(rate), 1_000_000);

    /// <summary>Proportional slices of an amount from their running total (percents in millionths of a whole).</summary>
    public static BigInteger[] Slices(BigInteger amount, params BigInteger[] percents)
    {
        BigInteger before = 0;
        return percents.Select(x =>
        {
            var after = before + x;
            var v = amount > 0 ? Away(amount * after, 1_000_000) - Away(amount * before, 1_000_000) : 0;
            before = after;
            return v;
        }).ToArray();
    }

    public static string Dmy(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly Day(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string NextMonth(string month) => Day(month + "-01").AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static DateOnly MonthEnd(string month) => Day(month + "-01").AddMonths(1).AddDays(-1);
}
