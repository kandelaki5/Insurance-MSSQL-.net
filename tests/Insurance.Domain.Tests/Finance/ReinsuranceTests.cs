using System.Globalization;
using System.Numerics;
using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.EarnedPremium;
using static Insurance.Domain.Finance.Reinsurance;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>reinsurance.test.ts.</summary>
public class ReinsuranceTests
{
    private static readonly PremiumTransaction policy = Tx(120_000, Currency.USD, "2026-01-01", "2026-01-01", "2027-01-01");

    [Fact(DisplayName = "reinsurance quota share cedes the treaty percent; surplus cedes what exceeds the retention up to the lines")]
    public void QuotaShareAndSurplus()
    {
        Assert.Equal(30m, CededPercentOf(new TreatyTerms(TreatyKind.QuotaShare, 20, CededPercent: 30), null));
        // Retention 100 000, 4 lines: a 300 000 risk cedes 200 000 = 66.6667 %.
        Assert.Equal(66.6667m, CededPercentOf(new TreatyTerms(TreatyKind.Surplus, 0, Retention: 10_000_000, Lines: 4), 30_000_000));
        // Below the retention nothing is ceded; above the capacity the cession is capped.
        Assert.Equal(0m, CededPercentOf(new TreatyTerms(TreatyKind.Surplus, 0, Retention: 10_000_000, Lines: 4), 8_000_000));
        Assert.Equal(40m, CededPercentOf(new TreatyTerms(TreatyKind.Surplus, 0, Retention: 10_000_000, Lines: 4), 100_000_000));
        Assert.Equal(0m, CededPercentOf(new TreatyTerms(TreatyKind.ExcessOfLoss, 0, Priority: 1, Limit: 2), 30_000_000));
    }

    [Fact(DisplayName = "reinsurance a cession mirrors the premium row with the ceded part and earns the same way")]
    public void CessionMirrorsThePremiumRow()
    {
        var cession = CessionOf(policy, 30);
        Assert.Equal(36_000, cession.Amount.Minor);
        Assert.Equal(policy.CoverFrom, cession.CoverFrom);
        Assert.Equal(policy.CoverTo, cession.CoverTo);
        // Reinsurer's share in UPR after 100 days: 36 000 × 265 / 365.
        var asOf = D("2026-04-10");
        var gross = PremiumPositionOf([policy], asOf)[Currency.USD];
        var ceded = PremiumPositionOf([cession], asOf)[Currency.USD];
        Assert.Equal(EarnedAsOf(cession, asOf), ceded.Earned);
        Assert.Equal(36_000 - ceded.Earned, ceded.Unearned);
        Assert.True(Math.Abs(ceded.Unearned * 10 - gross.Unearned * 3) <= 10);
    }

    [Fact(DisplayName = "reinsurance the cession of a cancellation reverses the cession of the original, never a tetri more")]
    public void CessionOfACancellation()
    {
        var cancel = CancellationOf(policy, D("2026-07-01"))!;
        var cededIssue = CessionOf(policy, 30);
        var cededCancel = CessionOf(cancel, 30);
        Assert.NotNull(cededCancel.Cancels);
        Assert.Equal(-(36_000 - EarnedAsOf(cededIssue, D("2026-06-30"))), cededCancel.Amount.Minor);
        var pos = PremiumPositionOf([cededIssue, cededCancel], D("2027-01-01"))[Currency.USD];
        Assert.Equal(0, pos.Unearned);
        Assert.Equal(cededIssue.Amount.Minor + cededCancel.Amount.Minor, pos.Written);
    }

    [Fact(DisplayName = "reinsurance commission is a share of the cession, rounded to the minor unit")]
    public void CommissionIsAShareOfTheCession()
    {
        var cession = CessionOf(policy, 30);
        Assert.Equal(8_100, CommissionOf(cession, 22.5m).Amount.Minor);
        Assert.Equal(33, CededAmount(100, 33.3333m));
    }

    [Fact(DisplayName = "reinsurance reinsurer's share of a claim: proportional by percent, excess of loss above the priority up to the limit")]
    public void ReinsurerShareOfAClaim()
    {
        Assert.Equal(45_000, ReinsurerShareOfClaim(new ClaimTreatyTerms(TreatyKind.QuotaShare), 30, 150_000));
        var xl = new ClaimTreatyTerms(TreatyKind.ExcessOfLoss, 100_000, 500_000);
        Assert.Equal(0, ReinsurerShareOfClaim(xl, 0, 80_000));
        Assert.Equal(50_000, ReinsurerShareOfClaim(xl, 0, 150_000));
        Assert.Equal(500_000, ReinsurerShareOfClaim(xl, 0, 900_000));
        Assert.Equal(0, ReinsurerShareOfClaim(new ClaimTreatyTerms(TreatyKind.Surplus), 66.6667m, 0));
    }

    private static CessionShare Qs(decimal sharePercent) => new(TreatyKind.QuotaShare, sharePercent, "GEL");

    [Fact(DisplayName = "reinsurance several treaties on one row never cede more than the row (review H08)")]
    public void SeveralTreatiesNeverCedeMoreThanTheRow()
    {
        // Two 60 % quota shares on one 1 000 GEL row are refused before anything is booked.
        Assert.Equal(CessionProblem.OverCeded, CheckCessionPlan([Qs(60), Qs(60)], "GEL"));
        // 60 % + 40 % is the whole row, split exactly.
        Assert.Null(CheckCessionPlan([Qs(60), Qs(40)], "GEL"));
        Assert.Equal(100_000, CededSlice(100_000, 0, 60) + CededSlice(100_000, 60, 40));
        // Rounding is applied to the running total: 50 % + 50 % of one tetri is one tetri, not two.
        Assert.Equal(1, CededSlice(1, 0, 50) + CededSlice(1, 50, 50));
        Assert.Equal(-1, CededSlice(-1, 0, 50) + CededSlice(-1, 50, 50));
        foreach (var amount in new long[] { 1, 3, 7, 99_999, 123_457 })
        {
            long[] slices = [CededSlice(amount, 0, 33.3333m), CededSlice(amount, 33.3333m, 33.3333m), CededSlice(amount, 66.6666m, 33.3334m)];
            Assert.Equal(amount, slices.Sum());
        }
        // Facultative counts together with the obligatory treaties.
        Assert.Equal(CessionProblem.OverCeded, CheckCessionPlan([Qs(70), new CessionShare(TreatyKind.Facultative, 40, "GEL")], "GEL"));
    }

    private static CessionShare Xl(string currency) => new(TreatyKind.ExcessOfLoss, 0, currency);

    [Fact(DisplayName = "reinsurance excess of loss is refused in another currency or on top of a proportional share (review H07)")]
    public void ExcessOfLossRefusedInAnotherCurrencyOrOnTopOfAShare()
    {
        // 1 000 GEL priority against a 1 000 USD loss: no comparison without a confirmed rate rule.
        Assert.Equal(CessionProblem.CurrencyMismatch, CheckCessionPlan([Xl("GEL")], "USD"));
        Assert.Null(CheckCessionPlan([Xl("USD")], "USD"));
        // Gross or net of the quota share is the owner's decision; until then the pair is refused.
        Assert.Equal(CessionProblem.XolWithProportional, CheckCessionPlan([new CessionShare(TreatyKind.QuotaShare, 30, "GEL"), Xl("GEL")], "GEL"));
    }

    [Fact(DisplayName = "reinsurance two excess-of-loss treaties on one loss are refused until layers are confirmed (review H08)")]
    public void TwoExcessOfLossTreatiesRefused()
    {
        // 1 000 GEL loss, two XLs each priority 100 / limit 10 000: each alone takes 900, together 1 800.
        var xl = Xl("GEL");
        Assert.Equal(90_000, ReinsurerShareOfClaim(new ClaimTreatyTerms(TreatyKind.ExcessOfLoss, 10_000, 1_000_000), 0, 100_000));
        Assert.Equal(CessionProblem.XolLayers, CheckCessionPlan([xl, xl], "GEL"));
        // One XL still works.
        Assert.Null(CheckCessionPlan([xl], "GEL"));
    }

    [Fact(DisplayName = "reinsurance a treaty premium not tied to policies is spread over the treaty period by days")]
    public void TreatyPremiumSpreadByDays()
    {
        // 365 000 over 2026: January carries 31 000, the whole year carries everything.
        Assert.Equal(3_100_000, TreatyPremiumInPeriod(36_500_000, D("2026-01-01"), D("2026-12-31"), D("2026-01-01"), D("2026-01-31")));
        Assert.Equal(2_800_000, TreatyPremiumInPeriod(36_500_000, D("2026-01-01"), D("2026-12-31"), D("2026-02-01"), D("2026-02-28")));
        Assert.Equal(36_500_000, TreatyPremiumInPeriod(36_500_000, D("2026-01-01"), D("2026-12-31"), D("2025-12-01"), D("2027-01-31")));
    }

    [Fact(DisplayName = "reinsurance the exact share arithmetic (claimSharesExact) agrees with the number one on ordinary amounts and stays exact beyond it")]
    public void ExactShareArithmetic()
    {
        // The stored percent to the 1/10 000: as PercentToUnits reads it.
        foreach (var p in new[] { "100.0000", "50.0000", "33.3333", "0.0001", "66.6667", "30", "7.5" })
        {
            Assert.Equal(new BigInteger(PercentToUnits(decimal.Parse(p, CultureInfo.InvariantCulture))), PercentUnitsExact(p));
        }
        Assert.Matches("not a share percent", Assert.Throws<ArgumentException>(() => PercentUnitsExact("12.34567")).Message);
        // CededSlice: every pair of an ordinary amount and two shares the same, the halves included.
        foreach (var amount in new long[] { 1, 2, 3, 5, 99, 100, 36_500, 12_345_678 })
        {
            foreach (var (already, share) in new (decimal, decimal)[] { (0, 50), (50, 50), (0, 33.3333m), (33.3333m, 33.3333m), (66.6666m, 33.3334m), (0, 100), (12.5m, 0.0001m) })
            {
                var text = (decimal d) => d.ToString("F4", CultureInfo.InvariantCulture);
                Assert.Equal(new BigInteger(CededSlice(amount, already, share)), CededSliceExact(amount, PercentUnitsExact(text(already)), PercentUnitsExact(text(share))));
            }
        }
        // 50 % + 50 % of one tetri: 1 + 0, never 2.
        Assert.Equal([BigInteger.One, BigInteger.Zero], new[] { CededSliceExact(1, 0, 500_000), CededSliceExact(1, 500_000, 500_000) });
        // Excess of loss: what exceeds the priority up to the limit; nothing of a non-positive amount; the same as the number version.
        foreach (var (amount, priority, limit) in new (long, long, long)[] { (150_000, 10_000, 100_000), (50_000, 10_000, 100_000), (5_000, 10_000, 100_000), (0, 0, 0), (-5, 0, 0), (70_000, 10_000, 0) })
        {
            var n = ReinsurerShareOfClaim(new ClaimTreatyTerms(TreatyKind.ExcessOfLoss, priority, limit), 0, amount);
            Assert.Equal(new BigInteger(n), ReinsurerShareOfClaimExact(new ClaimTreatyTermsExact(TreatyKind.ExcessOfLoss, priority, limit), 0, amount));
        }
        Assert.Equal(new BigInteger(ReinsurerShareOfClaim(new ClaimTreatyTerms(TreatyKind.QuotaShare), 30, 100_000)), ReinsurerShareOfClaimExact(new ClaimTreatyTermsExact(TreatyKind.QuotaShare), 300_000, 100_000));
        // Beyond 2^53: 100 % of numeric(18, 2)'s largest is itself; 50 % of 90071992547408.01 is …704.005 → …704.01; the XL share of it whole.
        Assert.Equal(BigInteger.Parse("999999999999999999"), CededSliceExact(BigInteger.Parse("999999999999999999"), 0, 1_000_000));
        Assert.Equal(BigInteger.Parse("4503599627370401"), CededSliceExact(BigInteger.Parse("9007199254740801"), 0, 500_000));
        Assert.Equal(BigInteger.Parse("9007199254740801"), ReinsurerShareOfClaimExact(new ClaimTreatyTermsExact(TreatyKind.ExcessOfLoss, 0), 0, BigInteger.Parse("9007199254740801")));
    }
}
