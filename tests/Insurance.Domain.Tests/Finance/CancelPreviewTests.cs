using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.CancelPreview;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>cancel-preview.test.ts: cancellation preview on the chosen date (UAT H22).</summary>
public class CancelPreviewTests
{
    // UAT policy A: 365.00 GEL for 2026, 91.25 paid.
    private static readonly PremiumTransaction year = Tx(36_500, Currency.GEL, "2026-01-01", "2026-01-01", "2027-01-01");
    private static readonly DateRange cover = new(D("2026-01-01"), D("2027-01-01"));

    [Fact(DisplayName = "cancellation preview on the chosen date (UAT H22) follows the date the employee chose, not today")]
    public void FollowsTheChosenDate()
    {
        var july = CancellationPreview([year], D("2026-07-01"), 9_125, cover);
        Assert.Equal(new CancelPreviewResult(D("2026-07-01"), 36_500, 18_400, 18_100, 9_125, 8_975), july);
        // What the screen showed for 05.10.2026 whatever the date chosen: 88.00 back, 185.75 owed.
        var october = CancellationPreview([year], D("2026-10-05"), 9_125, cover);
        Assert.Equal(8_800, october.Refund);
        Assert.Equal(18_575, october.Balance);
    }

    [Fact(DisplayName = "cancellation preview on the chosen date (UAT H22) shows money to return when more was paid than is retained")]
    public void ShowsMoneyToReturn()
    {
        var paidInFull = CancellationPreview([year], D("2026-07-01"), 36_500, cover);
        Assert.Equal(-18_400, paidInFull.Balance);
    }

    [Fact(DisplayName = "cancellation preview on the chosen date (UAT H22) keeps the date inside the cover")]
    public void KeepsTheDateInsideTheCover()
    {
        Assert.Equal(D("2026-01-01"), CancellationPreview([year], D("2025-12-01"), 0, cover).On);
        Assert.Equal(D("2026-12-31"), CancellationPreview([year], D("2027-03-01"), 0, cover).On);
        Assert.Equal(36_500, CancellationPreview([year], D("2026-01-01"), 0, cover).Refund);
    }
}
