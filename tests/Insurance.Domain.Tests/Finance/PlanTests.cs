using static Insurance.Domain.Finance.Plan;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>plan.test.ts.</summary>
public class PlanTests
{
    private static List<(DateOnly, long)> Laid(IEnumerable<Insurance.Domain.Finance.PlanInstallment> rows) => rows.Select(r => (r.DueOn, r.Minor)).ToList();

    [Fact(DisplayName = "the standard plans are 1, 4, 11 and 12 — two payments is an individual schedule (owner, 13.10.2026)")]
    public void StandardPlans() =>
        Assert.Equal([true, false, false, true, false, true, true], new[] { 1, 2, 3, 4, 6, 11, 12 }.Select(IsPaymentPlan));

    [Fact(DisplayName = "1, 4 and 12 lay the premium out as before: equal instalments on calendar months, the last takes the remainder")]
    public void OneFourAndTwelve()
    {
        // 400.00 for 01.10.2026–01.10.2027 (the demo's policy), by hand.
        Assert.Equal([(D("2026-10-01"), 40000L)], Laid(PlanInstallments(1, D("2026-10-01"), D("2027-10-01"), 40000)));
        Assert.Equal([(D("2026-10-01"), 10000L), (D("2027-01-01"), 10000L), (D("2027-04-01"), 10000L), (D("2027-07-01"), 10000L)], Laid(PlanInstallments(4, D("2026-10-01"), D("2027-10-01"), 40000)));
        // 1 000.00 in twelve: 83.33 eleven times, the last 83.37.
        var twelve = PlanInstallments(12, D("2026-01-01"), D("2027-01-01"), 100000);
        Assert.Equal(12, twelve.Count);
        Assert.Equal(new(D("2026-01-01"), 8333), twelve[0]);
        Assert.Equal(new(D("2026-12-01"), 8337), twelve[11]);
        Assert.Equal(100000, twelve.Sum(r => r.Minor));
        // A term that does not divide into whole months: equal day spans (100 days in four).
        Assert.Equal([(D("2026-01-01"), 10000L), (D("2026-01-26"), 10000L), (D("2026-02-20"), 10000L), (D("2026-03-17"), 10000L)], Laid(PlanInstallments(4, D("2026-01-01"), D("2026-04-11"), 40000)));
    }

    [Fact(DisplayName = "11 payments: the first and the last month on the start, then ten monthly (owner, 13.10.2026: 1 200.00 → 200.00 + 10 × 100.00)")]
    public void ElevenPayments()
    {
        Assert.Equal(
            [
                (D("2026-01-01"), 20000L), (D("2026-02-01"), 10000L), (D("2026-03-01"), 10000L), (D("2026-04-01"), 10000L), (D("2026-05-01"), 10000L), (D("2026-06-01"), 10000L),
                (D("2026-07-01"), 10000L), (D("2026-08-01"), 10000L), (D("2026-09-01"), 10000L), (D("2026-10-01"), 10000L), (D("2026-11-01"), 10000L),
            ],
            Laid(PlanInstallments(11, D("2026-01-01"), D("2027-01-01"), 120000)));
        // A premium that does not divide by twelve: a month is 83.33, the first 166.66, the last takes the remainder 83.37.
        var odd = PlanInstallments(11, D("2026-10-01"), D("2027-10-01"), 100000);
        Assert.Equal(new(D("2026-10-01"), 16666), odd[0]);
        Assert.Equal(new(D("2026-11-01"), 8333), odd[1]);
        Assert.Equal(new(D("2027-08-01"), 8337), odd[10]);
        Assert.Equal(100000, odd.Sum(r => r.Minor));
        // Only a term of twelve calendar months fits it.
        Assert.Equal([true, false, false, true], new[] { FitsPlan(11, D("2026-01-01"), D("2027-01-01")), FitsPlan(11, D("2026-01-01"), D("2026-07-01")), FitsPlan(11, D("2026-01-01"), D("2027-01-02")), FitsPlan(4, D("2026-01-01"), D("2026-07-01")) });
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanInstallments(11, D("2026-01-01"), D("2026-07-01"), 60000));
    }
}
