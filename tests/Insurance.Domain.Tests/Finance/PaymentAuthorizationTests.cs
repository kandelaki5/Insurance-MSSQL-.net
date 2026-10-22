using Insurance.Domain.Finance;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>payment-authorization.test.ts: one payment above 10 000 GEL needs the financial director (owner, 17.10.2026, docs/05 ვ8).</summary>
public class PaymentAuthorizationTests
{
    // The owner's threshold, 10 000 GEL (docs/05 ვ8), in tetri — a company setting, passed in.
    private const long Threshold = 1_000_000;

    /// <summary>Official rates by currency and day (fictional); every call is recorded, so the day used is seen.</summary>
    private static readonly Dictionary<string, decimal> Table = new()
    {
        ["USD 2026-10-15"] = 2.7m,
        ["USD 2026-10-16"] = 2.8m,
        ["EUR 2026-10-15"] = 3.1234m,
        ["EUR 2026-10-16"] = 2.000001m,
    };

    private static (List<(Currency, string)> Calls, RateProvider Rates) Provider()
    {
        var calls = new List<(Currency, string)>();
        RateProvider rates = (currency, date) =>
        {
            calls.Add((currency, Dates.ToIso(date)));
            return Table.TryGetValue($"{currency} {Dates.ToIso(date)}", out var r) ? r : 0m;
        };
        return (calls, rates);
    }

    /// <summary>The result and the rates asked for, as 'CUR YYYY-MM-DD' joined by ';'.</summary>
    private static (long GelMinor, bool RequiresAuthorization, string Calls) Check(long minor, Currency currency, string day)
    {
        var p = Provider();
        var result = PaymentAuthorization.Check(new Money(minor, currency), day, p.Rates, Threshold);
        return (result.GelMinor, result.RequiresAuthorization, string.Join(";", p.Calls.Select(c => $"{c.Item1} {c.Item2}")));
    }

    private const string Prefix = "one payment above 10 000 GEL needs the financial director (owner, 17.10.2026, docs/05 ვ8) ";

    [Fact(DisplayName = Prefix + "GEL: 9 999.99 and 10 000.00 do not, 10 000.01 does; no rate is asked for")]
    public void Gel()
    {
        Assert.Equal((999_999L, false, ""), Check(999_999, Currency.GEL, "2026-10-15"));
        Assert.Equal((1_000_000L, false, ""), Check(1_000_000, Currency.GEL, "2026-10-15"));
        Assert.Equal((1_000_001L, true, ""), Check(1_000_001, Currency.GEL, "2026-10-15"));
    }

    [Fact(DisplayName = Prefix + "USD at the rate of the payment day: 3 703.70 is 9 999.99 on 15.10 (2.7) but 10 370.36 on 16.10 (2.8)")]
    public void UsdAtThePaymentDayRate()
    {
        Assert.Equal((999_999L, false, "USD 2026-10-15"), Check(370_370, Currency.USD, "2026-10-15"));
        Assert.Equal((1_037_036L, true, "USD 2026-10-16"), Check(370_370, Currency.USD, "2026-10-16"));
        // 3 703.71 × 2.7 = 10 000.017 → 10 000.02.
        Assert.Equal((1_000_002L, true, "USD 2026-10-15"), Check(370_371, Currency.USD, "2026-10-15"));
    }

    [Fact(DisplayName = Prefix + "EUR, by the existing rounding to the tetri: 3 201.64 × 3.1234 = 10 000.0002 → 10 000.00, not needed; 3 201.65 → 10 000.0336 → 10 000.03, needed")]
    public void EurRounding()
    {
        Assert.Equal((1_000_000L, false, "EUR 2026-10-15"), Check(320_164, Currency.EUR, "2026-10-15"));
        Assert.Equal((1_000_003L, true, "EUR 2026-10-15"), Check(320_165, Currency.EUR, "2026-10-15"));
    }

    [Fact(DisplayName = Prefix + "a half tetri rounds away from zero, as everywhere: EUR 5 000.00 × 2.000001 = 10 000.005 → 10 000.01, needed")]
    public void HalfTetriRoundsAwayFromZero() =>
        Assert.Equal((1_000_001L, true, "EUR 2026-10-16"), Check(500_000, Currency.EUR, "2026-10-16"));

    [Fact(DisplayName = Prefix + "each payment on its own: two of 6 000 GEL do not, one of 12 000 does; USD 2 000 twice do not, 4 000 once does")]
    public void EachPaymentOnItsOwn()
    {
        Assert.Equal([false, false], new[] { Check(600_000, Currency.GEL, "2026-10-15"), Check(600_000, Currency.GEL, "2026-10-15") }.Select(r => r.RequiresAuthorization));
        Assert.True(Check(1_200_000, Currency.GEL, "2026-10-15").RequiresAuthorization);
        Assert.Equal([(540_000L, false), (540_000L, false)], new[] { Check(200_000, Currency.USD, "2026-10-15"), Check(200_000, Currency.USD, "2026-10-15") }.Select(r => (r.GelMinor, r.RequiresAuthorization)));
        Assert.Equal([(1_080_000L, true)], new[] { Check(400_000, Currency.USD, "2026-10-15") }.Select(r => (r.GelMinor, r.RequiresAuthorization)));
    }

    [Fact(DisplayName = Prefix + "no rate on the payment day is an error, never \"not needed\": USD on 17.10, a zero or a NaN rate")]
    public void NoRateIsAnError()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Check(100, Currency.USD, "2026-10-17"));
        Assert.Matches("No USD rate for 2026-10-17", e.Message);
        // A decimal rate cannot be NaN; zero and a negative rate remain.
        foreach (var bad in new[] { 0m, -2.7m })
        {
            var x = Assert.Throws<InvalidOperationException>(() => PaymentAuthorization.Check(new Money(100, Currency.USD), D("2026-10-15"), (_, _) => bad, Threshold));
            Assert.Matches("No USD rate", x.Message);
        }
    }

    [Fact(DisplayName = Prefix + "refuses what is not one payment: zero, negative or fractional amounts, a bad day or threshold")]
    public void RefusesWhatIsNotOnePayment()
    {
        var rates = Provider().Rates;
        // A fractional or NaN amount cannot be a whole number of minor units (long); zero, negative and beyond 2^53 − 1 remain.
        foreach (var minor in new[] { 0L, -1_000_001L, 9_007_199_254_740_992L })
        {
            var e = Assert.Throws<ArgumentException>(() => PaymentAuthorization.Check(new Money(minor, Currency.GEL), D("2026-10-15"), rates, Threshold));
            Assert.Matches("positive whole number", e.Message);
        }
        Assert.Matches("Not a payment day", Assert.Throws<ArgumentException>(() => PaymentAuthorization.Check(new Money(1, Currency.GEL), "15.10.2026", rates, Threshold)).Message);
        foreach (var t in new[] { -1L, 9_007_199_254_740_992L })
        {
            Assert.Matches("threshold", Assert.Throws<ArgumentException>(() => PaymentAuthorization.Check(new Money(1, Currency.GEL), D("2026-10-15"), rates, t)).Message);
        }
    }
}
