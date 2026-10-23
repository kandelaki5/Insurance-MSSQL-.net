// Legacy PR #218 finance.test.ts:398 (rebuildInstallments) and PR #225 change-number.test.ts, ported: the schedule laid out
// again for a corrected premium, and the numbers a change takes (owner, 13–14.10.2026).
using Insurance.Application.Services;
using Insurance.Domain.Finance;

namespace Insurance.Domain.Tests.Finance;

public class OpenMonthCorrectionTests
{
    private static Installment I(string id, string due, long minor) => new(id, DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture), new Money(minor, Currency.GEL));

    private static readonly Installment[] Four = new[] { "2026-09-01", "2026-10-01", "2026-11-01", "2026-12-01" }.Select((d, n) => I((n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), d, 10000)).ToArray();

    private static long[] Amounts(Dictionary<string, long> m, params string[] ids) => ids.Select(id => m[id]).ToArray();

    [Fact(DisplayName = "rebuildInstallments: 4 × 100.00 to 500.00 is 4 × 125.00, to 320.00 4 × 80.00, to nothing zeros")]
    public void SameShares()
    {
        Assert.Equal([12500L, 12500, 12500, 12500], Amounts(Receivable.RebuildInstallments(Four, 50000), "1", "2", "3", "4"));
        Assert.Equal([8000L, 8000, 8000, 8000], Amounts(Receivable.RebuildInstallments(Four, 32000), "1", "2", "3", "4"));
        Assert.Equal([0L, 0, 0, 0], Amounts(Receivable.RebuildInstallments(Four, 0), "1", "2", "3", "4"));
    }

    [Fact(DisplayName = "rebuildInstallments: each keeps its share rounded down, the last by date takes the remainder")]
    public void Rounding()
    {
        Installment[] three = [I("c", "2026-12-01", 13334), I("a", "2026-06-01", 13333), I("b", "2026-09-01", 13333)];
        Assert.Equal([16666L, 16666, 16668], Amounts(Receivable.RebuildInstallments(three, 50000), "a", "b", "c"));
        var eleven = Enumerable.Range(0, 11).Select(i => I(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"2026-{i + 1:00}-01", i == 0 ? 20000 : 10000)).ToArray();
        Assert.Equal([25000L, .. Enumerable.Repeat(12500L, 10)], Receivable.RebuildInstallments(eleven, 150000).OrderBy(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture)).Select(p => p.Value));
    }

    [Fact(DisplayName = "rebuildInstallments: a negative total or no shares to lay a total out by is refused")]
    public void Refusals()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Receivable.RebuildInstallments(Four, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Receivable.RebuildInstallments([], 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Receivable.RebuildInstallments(Four.Select(i => i with { Amount = i.Amount with { Minor = 0 } }).ToList(), 100));
    }

    [Fact(DisplayName = "rebuildInstallments: the shares first laid out win over a correction's rounded amounts (PR218-F1/F2)")]
    public void FirstShares()
    {
        var zero = Four.Select(i => i with { Amount = i.Amount with { Minor = 0 } }).ToList();
        Assert.Equal([2500L, 2500, 2500, 2500], Amounts(Receivable.RebuildInstallments(zero, 10000, Four), "1", "2", "3", "4"));
        Assert.Equal([0L, 0, 0, 0], Amounts(Receivable.RebuildInstallments(zero, 0), "1", "2", "3", "4"));
        var eleven = Enumerable.Range(0, 11).Select(i => I(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"2026-{i + 1:00}-01", i == 0 ? 20000 : 10000)).ToArray();
        var elevenZero = eleven.Select(i => i with { Amount = i.Amount with { Minor = 0 } }).ToList();
        Assert.Equal([25000L, .. Enumerable.Repeat(12500L, 10)], Receivable.RebuildInstallments(elevenZero, 150000, eleven).OrderBy(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture)).Select(p => p.Value));
        var corrected = Four.Select((i, n) => i with { Amount = i.Amount with { Minor = n == 3 ? 8334 : 8333 } }).ToList();
        Assert.Equal([8333L, 8333, 8333, 8334], Amounts(Receivable.RebuildInstallments(Four, 33333, Four), "1", "2", "3", "4"));
        Assert.Equal([12500L, 12500, 12500, 12500], Amounts(Receivable.RebuildInstallments(corrected, 50000, Four), "1", "2", "3", "4"));
        Assert.Equal([12499L, 12499, 12499, 12503], Amounts(Receivable.RebuildInstallments(corrected, 50000), "1", "2", "3", "4"));
    }

    [Fact(DisplayName = "rebuildInstallments: share × target is exact beyond 2^53 (the old floating product was not)")]
    public void Exact()
    {
        Installment[] two = [I("a", "2026-01-01", 4_503_599_627_370_497), I("b", "2026-02-01", 4_503_599_627_370_497)];
        var r = Receivable.RebuildInstallments(two, 9_007_199_254_740_993);
        Assert.Equal((4_503_599_627_370_496L, 4_503_599_627_370_497L), (r["a"], r["b"]));
        Assert.Equal(9_007_199_254_740_993L, r.Values.Sum());
    }

    [Fact(DisplayName = "nextChangeNumber: /1 then /2 by the original, whatever requests and corrections are taken")]
    public void ChangeNumbers()
    {
        Assert.Equal(["MHL000001/1", "MHL000001/2", "MHL000001/2"], new[]
        {
            ChangeNumber.Next("MHL000001", []),
            ChangeNumber.Next("MHL000001", ["MHL000001/1"]),
            ChangeNumber.Next("MHL000001", ["MHL000001/1", "MHL000001/მოთხოვნა-4", "MHL000001/შესწორება-2"]),
        });
        Assert.Equal(["MHL000001/4", "MHL000001/1", "MHL000001/მოთხოვნა-2", "MHL000001/შესწორება-2"], new[]
        {
            ChangeNumber.Next("MHL000001", ["MHL000001/1", "MHL000001/3"]),
            ChangeNumber.Next("MHL000001", ["MHL000001/03", "MHL000001/1x", "MHL0000011/7", "MHL000001/1/1"]),
            ChangeNumber.Next("MHL000001", ["MHL000001/მოთხოვნა-1", "MHL000001/2"], "მოთხოვნა"),
            ChangeNumber.Next("MHL000001", ["MHL000001/მოთხოვნა-1", "MHL000001/შესწორება-1"], "შესწორება"),
        });
        // Beyond a long: one past the highest still (the old code's Number lost exactness past 2^53).
        Assert.Equal("R/100000000000000000001", ChangeNumber.Next("R", ["R/100000000000000000000"]));
    }
}
