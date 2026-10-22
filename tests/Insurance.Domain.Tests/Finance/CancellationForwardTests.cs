using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.EarnedPremium;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>cancellation-forward.test.ts: bringing a later cancellation forward (H24).</summary>
public class CancellationForwardTests
{
    // UAT policy: 365.00 for 01.04.2026–01.04.2027, cancelled first from 31.12.2026 (booked that
    // day), then the cancellation brought forward to a total loss on 01.09.2026 (H24): a
    // cancellation from 01.09 is added and the 31.12 one undone on its own day.
    private static readonly PremiumTransaction issue = Tx(36_500, Currency.GEL, "2026-04-01", "2026-04-01", "2027-04-01");
    private static readonly PremiumTransaction later = CancellationOf(issue, D("2026-12-31"))!;
    private static readonly PremiumTransaction earlier = CancellationOf(issue, D("2026-09-01"))!;
    private static readonly PremiumTransaction undo = ReversalOf(later, later.BookedOn);
    private static readonly PremiumTransaction[] broughtForward = [issue, later, earlier, undo];
    private static readonly PremiumTransaction[] once = [issue, earlier];
    private static readonly DateOnly[] days = new[] { "2026-08-31", "2026-09-01", "2026-10-05", "2026-12-30", "2026-12-31", "2027-03-31", "2027-06-01" }.Select(D).ToArray();

    private static void SamePositions(IReadOnlyDictionary<Currency, PremiumPosition> expected, IReadOnlyDictionary<Currency, PremiumPosition> actual) =>
        Assert.Equal(expected.ToList(), actual.ToList());

    [Fact(DisplayName = "bringing a later cancellation forward (H24) the undo row mirrors the cancellation it reverses")]
    public void UndoMirrorsTheCancellation()
    {
        Assert.Equal(-9_100, later.Amount.Minor);
        Assert.Equal(9_100, undo.Amount.Minor);
        Assert.Equal((D("2026-12-31"), D("2027-04-01"), D("2026-12-31")), (undo.CoverFrom, undo.CoverTo, undo.BookedOn));
        foreach (var day in days) Assert.Equal(0, EarnedAsOf(later, day) + EarnedAsOf(undo, day));
    }

    [Fact(DisplayName = "bringing a later cancellation forward (H24) shows the policy as cancelled once from the loss date, on every day")]
    public void CancelledOnceFromTheLossDate()
    {
        foreach (var day in days) SamePositions(PremiumPositionOf(once, day), PremiumPositionOf(broughtForward, day));
        // From 01.09 nothing is unearned: written 153.00, UPR 0 (not 91.00 until 31.12).
        Assert.Equal(new PremiumPosition(15_300, 15_300, 0), PremiumPositionOf(broughtForward, D("2026-10-05"))[Currency.GEL]);
    }

    [Fact(DisplayName = "bringing a later cancellation forward (H24) keeps every row at its original GEL rate, to the tetri")]
    public void KeepsEveryRowAtItsOriginalRate()
    {
        var usd = Tx(100_001, Currency.USD, "2026-04-01", "2026-04-01", "2027-04-01");
        RateProvider rates = (_, day) => day == D("2026-04-01") ? 2.7015m : 2.6039m;
        var l = CancellationOf(usd, D("2026-12-31"))!;
        PremiumTransaction[] rows = [usd, l, CancellationOf(usd, D("2026-09-01"))!, ReversalOf(l, l.BookedOn)];
        PremiumTransaction[] single = [usd, CancellationOf(usd, D("2026-09-01"))!];
        Assert.Equal(-InGel(l, rates).Amount.Minor, InGel(rows[3], rates).Amount.Minor);
        foreach (var day in days) Assert.Equal(PremiumPositionGel(single, day, rates), PremiumPositionGel(rows, day, rates));
    }

    [Fact(DisplayName = "bringing a later cancellation forward (H24) reverses only a cancellation of an original row")]
    public void ReversesOnlyACancellationOfAnOriginal()
    {
        Assert.Throws<InvalidOperationException>(() => ReversalOf(issue, D("2026-12-31")));
        Assert.Throws<InvalidOperationException>(() => ReversalOf(undo, D("2026-12-31")));
    }
}
