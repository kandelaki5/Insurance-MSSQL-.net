using System.Numerics;
using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.EarnedPremium;
using static Insurance.Domain.Finance.Impairment;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>
/// impairment-exact.test.ts. The exact impairment (INS-FORM1-DATA-FIDELITY-FIX-OFFLINE-20261019) is the number one's rules,
/// line for line: on every amount a double holds exactly they agree to the tetri — an issue, a cancellation linked to it and
/// its reversal, booked late, overpaid — and beyond, the exact one keeps the last tetri the number one loses.
/// </summary>
public class ImpairmentExactTests
{
    private static PremiumTransaction T(long minor, string bookedOn, string coverFrom, string coverTo) => Tx(minor, Currency.GEL, bookedOn, coverFrom, coverTo);

    private static ExactPremiumTx Exact(PremiumTransaction t) =>
        new(t.Amount.Minor, t.BookedOn, t.CoverFrom, t.CoverTo, t.Cancels is null ? null : Exact(t.Cancels));

    private static readonly PremiumTransaction issue = T(36_500, "2026-01-01", "2026-01-01", "2027-01-01");
    private static readonly PremiumTransaction cancel = T(-12_345, "2026-08-15", "2026-08-15", "2027-01-01") with { Cancels = issue };
    private static readonly PremiumTransaction undo = T(12_345, "2026-09-01", "2026-08-15", "2027-01-01") with { Cancels = cancel };
    private static readonly PremiumTransaction late = T(100_001, "2026-06-30", "2026-02-10", "2026-12-10");

    private static readonly Dictionary<string, (PremiumTransaction[] Txs, long Paid)> Sets = new()
    {
        ["an issue, unpaid"] = ([issue], 0),
        ["an issue, part paid"] = ([issue], 10_001),
        ["overpaid"] = ([issue], 40_000),
        ["cancelled"] = ([issue, cancel], 5_000),
        ["cancelled and undone"] = ([issue, cancel, undo], 0),
        ["booked late"] = ([late], 333),
    };

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in Sets.Keys)
        {
            foreach (var asOf in new[] { "2026-03-31", "2026-08-31", "2026-12-31", "2027-03-31" }) data.Add(name, asOf);
        }
        return data;
    }

    [Theory(DisplayName = "the exact impairment equals the number one wherever a double is exact")]
    [MemberData(nameof(Cases))]
    public void ExactEqualsNumber(string name, string asOfText)
    {
        var (txs, paid) = Sets[name];
        var asOf = D(asOfText);
        foreach (var t in txs) Assert.Equal(new BigInteger(EarnedAsOf(t, asOf)), EarnedAsOfExact(Exact(t), asOf));
        var n = EarnedByAge(txs, asOf);
        var x = EarnedByAgeExact(txs.Select(Exact), asOf);
        foreach (var band in AgeBands) Assert.Equal(new BigInteger(n[band]), x[band]);
        var ni = ImpairmentOf(txs, paid, asOf);
        var xi = ImpairmentOfExact(txs.Select(Exact), paid, asOf);
        Assert.Equal((new BigInteger(ni.TotalEarned), new BigInteger(ni.UnpaidEarned), new BigInteger(ni.TotalReserve)), (xi.TotalEarned, xi.UnpaidEarned, xi.TotalReserve));
        foreach (var band in AgeBands) Assert.Equal((new BigInteger(ni.Unpaid[band]), new BigInteger(ni.Reserve[band])), (xi.Unpaid[band], xi.Reserve[band]));
    }

    [Fact(DisplayName = "the exact impairment equals the number one wherever a double is exact beyond a double: 90071992547408.01 earned in full is itself; through numbers it is not")]
    public void BeyondADouble()
    {
        var big = new ExactPremiumTx(BigInteger.Parse("9007199254740801"), D("2026-01-01"), D("2026-01-01"), D("2027-01-01"));
        Assert.Equal(BigInteger.Parse("9007199254740801"), EarnedAsOfExact(big, D("2026-12-31")));
        Assert.Equal(BigInteger.Parse("9007199254740801"), ImpairmentOfExact([big], 0, D("2026-12-31")).UnpaidEarned);
        // Through a JS number (a double) the last tetri is lost: Math.round(Number('90071992547408.01') * 100).
        Assert.NotEqual(BigInteger.Parse("9007199254740801"), new BigInteger(Math.Round(double.Parse("90071992547408.01", System.Globalization.CultureInfo.InvariantCulture) * 100)));
    }
}
