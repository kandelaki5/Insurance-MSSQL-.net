using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.EarnedPremium;
using static Insurance.Domain.Finance.Impairment;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>impairment.test.ts: impairment reserve on receivables (order №02 annex 2 art. 2.50).</summary>
public class ImpairmentTests
{
    // 365 000 tetri over 365 days: 1 000 a day, so band sizes are easy to read.
    private static readonly PremiumTransaction year2023 = Tx(365_000, Currency.GEL, "2023-01-01", "2023-01-01", "2024-01-01");

    private const string Prefix = "impairment reserve on receivables (order №02 annex 2 art. 2.50) ";

    [Fact(DisplayName = Prefix + "ages by whole months back from the reporting date")]
    public void AgesByWholeMonths()
    {
        Assert.Equal(new BandCutoffs(D("2022-12-31"), D("2023-03-31"), D("2023-06-30"), D("2023-09-30")), BandCutoffsOf(D("2023-12-31")));
        // A month-end in a leap February keeps month ends.
        Assert.Equal(new BandCutoffs(D("2023-02-28"), D("2023-05-31"), D("2023-08-31"), D("2023-11-30")), BandCutoffsOf(D("2024-02-29")));
    }

    [Fact(DisplayName = Prefix + "splits earned premium by when it was earned")]
    public void SplitsEarnedPremium() =>
        Assert.Equal(new ByBand(0, 90_000, 91_000, 92_000, 92_000), EarnedByAge([year2023], D("2023-12-31")));

    [Fact(DisplayName = Prefix + "nothing is reserved when the premium is paid (the template example)")]
    public void NothingReservedWhenPaid()
    {
        var r = ImpairmentOf([year2023], 365_000, D("2023-12-31"));
        Assert.Equal(0, r.UnpaidEarned);
        Assert.Equal(0, r.TotalReserve);
    }

    [Fact(DisplayName = Prefix + "payments cover the oldest earned premium; the unpaid rest is the newest, reserved by its age")]
    public void PaymentsCoverTheOldest()
    {
        // Paid 100 000: unpaid earned 265 000 = 92 000 (0–3) + 92 000 (3–6) + 81 000 of 6–9.
        var r = ImpairmentOf([year2023], 100_000, D("2023-12-31"));
        Assert.Equal(new ByBand(0, 0, 81_000, 92_000, 92_000), r.Unpaid);
        // 30 % of 81 000 + 10 % of 92 000 + 0 % of 92 000.
        Assert.Equal(24_300 + 9_200, r.TotalReserve);
    }

    [Fact(DisplayName = Prefix + "premium earned more than a year ago and still unpaid is reserved in full")]
    public void OldUnpaidReservedInFull()
    {
        var old = Tx(365_000, Currency.USD, "2022-01-01", "2022-01-01", "2023-01-01");
        var r = ImpairmentOf([old], 0, D("2023-12-31"));
        Assert.Equal(365_000, r.Unpaid.Over12);
        Assert.Equal(365_000, r.TotalReserve);
    }

    [Fact(DisplayName = Prefix + "a cancelled policy earns only up to the cancel date")]
    public void CancelledPolicyEarnsUpToTheCancelDate()
    {
        var cancel = CancellationOf(year2023, D("2023-04-01"))!;
        var r = ImpairmentOf([year2023, cancel], 0, D("2023-12-31"));
        // Earned 1 Jan – 31 Mar = 90 days, all in the 9–12 month band: 50 %.
        Assert.Equal(90_000, r.TotalEarned);
        Assert.Equal(45_000, r.TotalReserve);
    }

    [Fact(DisplayName = Prefix + "a prepayment reserves nothing and a premium booked after the date is not counted")]
    public void PrepaymentAndLaterBooking()
    {
        Assert.Equal(0, ImpairmentOf([year2023], 400_000, D("2023-12-31")).TotalReserve);
        var later = year2023 with { BookedOn = D("2024-02-01") };
        Assert.Equal(0, ImpairmentOf([later], 0, D("2023-12-31")).TotalEarned);
    }

    [Fact(DisplayName = Prefix + "the result does not depend on the order the ledger rows are read in (review H14)")]
    public void ResultDoesNotDependOnReadOrder()
    {
        // 100 GEL for 2023, cancelled from 1 April, nothing paid: 90 days earned = 24.66.
        var issue = Tx(10_000, Currency.GEL, "2023-01-01", "2023-01-01", "2024-01-01");
        var cancel = CancellationOf(issue, D("2023-04-01"))!;
        List<LedgerRow> Rows() => [new("issue", null, issue), new("cancel", "issue", cancel with { Cancels = null })];
        var issueFirst = ImpairmentOf(LinkLedger(Rows()), 0, D("2023-12-31"));
        var reversed = Rows();
        reversed.Reverse();
        var cancelFirst = ImpairmentOf(LinkLedger(reversed), 0, D("2023-12-31"));
        Assert.Equal(2_466, issueFirst.TotalEarned);
        Assert.Equal(issueFirst, cancelFirst);
        Assert.Equal(0, cancelFirst.Earned.M0to3);
        // A cancellation whose original is missing is an error, not a plain negative premium.
        var e = Assert.Throws<InvalidOperationException>(() => LinkLedger([new LedgerRow("cancel", "issue", cancel with { Cancels = null })]));
        Assert.Contains("not among the rows", e.Message);
    }

    [Fact(DisplayName = Prefix + "premium booked late is aged by its cover days, not by the booking date")]
    public void LateBookingAgedByCoverDays()
    {
        // Booked in March 2023 for cover from January: the January days are still 9–12 months old.
        var late = year2023 with { BookedOn = D("2023-03-15") };
        Assert.Equal(EarnedByAge([year2023], D("2023-12-31")), EarnedByAge([late], D("2023-12-31")));
    }

    [Fact(DisplayName = Prefix + "a receivable from a broker is aged as any other but not impaired (owner, 19.10.2026)")]
    public void BrokerReceivableIsNotImpaired()
    {
        // Nothing paid on 31.12.2023: unpaid 90 000 / 91 000 / 92 000 / 92 000, reserved 45 000 + 27 300 + 9 200 = 81 500 otherwise.
        var aged = ImpairmentOf([year2023], 0, D("2023-12-31"));
        Assert.Equal(81_500, aged.TotalReserve);
        var broker = WithoutReserve(aged);
        Assert.Equal(aged with { Reserve = ByBand.Zero, TotalReserve = 0 }, broker);
        Assert.Equal(new ByBand(0, 0, 0, 0, 0), broker.Reserve);
        Assert.Equal(0, broker.TotalReserve);
        Assert.Equal(81_500, aged.TotalReserve); // the aged figures themselves are not changed
    }
}
