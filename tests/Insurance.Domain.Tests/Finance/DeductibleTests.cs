using static Insurance.Domain.Finance.Deductible;

namespace Insurance.Domain.Tests.Finance;

/// <summary>deductible.test.ts.</summary>
public class DeductibleTests
{
    /// <summary>Runs the payments in order and returns what was withheld from each and the net paid.</summary>
    private static (List<(long Withheld, long Net)> Parts, long WithheldTotal, long NetTotal) Settle(long agreed, long[] payments)
    {
        long withheld = 0;
        var parts = payments.Select(gross =>
        {
            var d = DeductibleToWithhold(agreed, withheld, gross);
            withheld += d;
            return (Withheld: d, Net: gross - d);
        }).ToList();
        return (parts, withheld, parts.Sum(p => p.Net));
    }

    [Fact(DisplayName = "deductible over partial payments (H02) 100 + 900 against a 200 deductible withholds 200 in total and pays 800")]
    public void TwoPayments()
    {
        var r = Settle(20_000, [10_000, 90_000]);
        Assert.Equal([10_000, 10_000], r.Parts.Select(p => p.Withheld));
        Assert.Equal(20_000, r.WithheldTotal);
        Assert.Equal(80_000, r.NetTotal);
    }

    [Fact(DisplayName = "deductible over partial payments (H02) a single payment withholds the whole deductible once")]
    public void SinglePayment()
    {
        var r = Settle(20_000, [100_000]);
        Assert.Equal(20_000, r.WithheldTotal);
        Assert.Equal(80_000, r.NetTotal);
    }

    [Fact(DisplayName = "deductible over partial payments (H02) a first payment exactly equal to the deductible pays nothing, the rest is net")]
    public void FirstPaymentEqualToTheDeductible()
    {
        var r = Settle(20_000, [20_000, 80_000]);
        Assert.Equal([0, 80_000], r.Parts.Select(p => p.Net));
        Assert.Equal(20_000, r.WithheldTotal);
    }

    [Fact(DisplayName = "deductible over partial payments (H02) three small payments: nothing is lost and nothing is withheld twice")]
    public void ThreeSmallPayments()
    {
        var r = Settle(20_000, [5_000, 5_000, 90_000]);
        Assert.Equal([5_000, 5_000, 10_000], r.Parts.Select(p => p.Withheld));
        Assert.Equal(80_000, r.NetTotal);
        Assert.True(r.Parts.All(p => p.Net >= 0));
    }

    [Fact(DisplayName = "deductible over partial payments (H02) no deductible means nothing is withheld")]
    public void NoDeductible() => Assert.Equal(0, Settle(0, [10_000, 90_000]).WithheldTotal);

    [Fact(DisplayName = "one deductible per class in a loss event (H50) equal deductibles of two coverages are withheld once: 2000 + 500 gross, 200 → 200 in all")]
    public void EqualDeductiblesWithheldOnce()
    {
        var shared = ClassDeductible([20000, 20000]);
        Assert.Equal(20000, shared);
        var first = DeductibleToWithhold(shared!.Value, 0, 200000);
        var second = DeductibleToWithhold(shared.Value, first, 50000);
        Assert.Equal(20000, first + second);
        Assert.Equal(230000, 200000 - first + (50000 - second));
    }

    [Fact(DisplayName = "one deductible per class in a loss event (H50) a single coverage keeps its own deductible, as before")]
    public void SingleCoverageKeepsItsDeductible() => Assert.Equal(15000, ClassDeductible([15000]));

    [Fact(DisplayName = "one deductible per class in a loss event (H50) different deductibles (200 and 50) give no shared one")]
    public void DifferentDeductiblesGiveNone() => Assert.Null(ClassDeductible([20000, 5000]));
}
