using System.Numerics;
using Insurance.Domain.Finance;
using static Insurance.Domain.Finance.Commission;
using static Insurance.Domain.Finance.Dates;
using static Insurance.Domain.Finance.EarnedPremium;
using static Insurance.Domain.Finance.Receivable;
using static Insurance.Domain.Finance.Reserves;
using static Insurance.Domain.Finance.TotalLoss;
using static Insurance.Domain.Tests.Finance.TestSupport;

namespace Insurance.Domain.Tests.Finance;

/// <summary>finance.test.ts: the worked example of docs/04 and the core rules.</summary>
public class FinanceTests
{
    // Illustrative rates, not real NBG data.
    private static readonly RateProvider rates = Rates(Currency.USD, new()
    {
        ["2026-01-01"] = 2.7m,
        ["2026-01-05"] = 2.7m,
        ["2026-03-31"] = 2.75m,
        ["2026-04-10"] = 2.8m,
        ["2026-06-30"] = 2.65m,
    });

    // Worked example from docs/04: CASCO, 1 200 USD, 01.01.2026–01.01.2027 (365 days), four instalments of 300 USD.
    private static readonly PremiumTransaction policy = new(Money(1200, Currency.USD), D("2026-01-01"), D("2026-01-01"), D("2027-01-01"));

    private static readonly Installment[] schedule =
    [
        new("1", D("2026-01-01"), Money(300, Currency.USD)),
        new("2", D("2026-04-01"), Money(300, Currency.USD)),
        new("3", D("2026-07-01"), Money(300, Currency.USD)),
        new("4", D("2026-10-01"), Money(300, Currency.USD)),
    ];

    [Fact(DisplayName = "earned premium is daily pro rata over the cover period")]
    public void EarnedPremiumIsDailyProRata()
    {
        Assert.Equal(0, EarnedAsOf(policy, D("2025-12-31")));
        Assert.Equal(329, EarnedAsOf(policy, D("2026-01-01"))); // 1200 × 1/365
        Assert.Equal(29589, EarnedAsOf(policy, D("2026-03-31"))); // 1200 × 90/365
        Assert.Equal(120000, EarnedAsOf(policy, D("2026-12-31")));
        Assert.Equal(120000, EarnedAsOf(policy, D("2027-06-01")));
    }

    [Fact(DisplayName = "written = earned + unearned at any date")]
    public void WrittenIsEarnedPlusUnearned()
    {
        var pos = PremiumPositionOf([policy], D("2026-03-31"))[Currency.USD];
        Assert.Equal(new PremiumPosition(120000, 29589, 90411), pos);
    }

    [Fact(DisplayName = "cancellation is a negative transaction and leaves no unearned premium")]
    public void CancellationIsNegativeAndLeavesNoUnearned()
    {
        // Cancelled from 01.07: premium for 184 remaining days is returned.
        var refund = CancellationOf(policy, D("2026-07-01"))!;
        Assert.Equal(-60493, refund.Amount.Minor); // 1200 × 184/365
        var pos = PremiumPositionOf([policy, refund], D("2026-07-01"))[Currency.USD]; // booked on the cancel date
        Assert.Equal(59507, pos.Written);
        Assert.Equal(59507, pos.Earned); // 1200 × 181/365
        Assert.Equal(0, pos.Unearned);
    }

    [Fact(DisplayName = "earned in a period is the difference of two positions")]
    public void EarnedInPeriodIsDifference()
    {
        Assert.Equal(29589, EarnedInPeriod([policy], D("2026-01-01"), D("2026-03-31"))[Currency.USD]);
        Assert.Equal(59507 - 29589, EarnedInPeriod([policy], D("2026-04-01"), D("2026-06-30"))[Currency.USD]);
    }

    [Fact(DisplayName = "GEL payment settles the debt at the payment-day rate")]
    public void GelPaymentSettlesAtPaymentDayRate()
    {
        var credit = PaymentCredit(81000, Currency.USD, D("2026-01-05"), rates); // 810 GEL at 2.70
        Assert.Equal(M(30000, Currency.USD), credit.Amount);
    }

    [Fact(DisplayName = "receivable stays in policy currency and is revalued at the reporting date")]
    public void ReceivableStaysInPolicyCurrency()
    {
        Credit[] credits = [PaymentCredit(81000, Currency.USD, D("2026-01-05"), rates)];
        var pos = ReceivableAsOf(schedule, credits, D("2026-03-31"), rates);
        Assert.Equal(90000, pos.Outstanding.Minor);
        Assert.Equal(0, pos.Overdue.Minor);
        Assert.Equal(247500, pos.OutstandingGel); // 900 USD × 2.75
        Assert.Equal(243000, Insurance.Domain.Finance.Money.ToGel(pos.Outstanding, D("2026-01-01"), rates)); // booked at 2.70 → 45 GEL revaluation
    }

    [Fact(DisplayName = "unpaid instalment becomes overdue after its due date")]
    public void UnpaidInstalmentBecomesOverdue()
    {
        Credit[] credits = [PaymentCredit(81000, Currency.USD, D("2026-01-05"), rates)];
        var pos = ReceivableAsOf(schedule, credits, D("2026-04-10"), rates);
        Assert.Equal(30000, pos.Overdue.Minor);
        Assert.Equal(30000, pos.Balances[1].Outstanding);
    }

    [Fact(DisplayName = "total loss: remaining premium is set off against the claim payout")]
    public void TotalLossRemainingPremiumSetOff()
    {
        Credit[] credits = [PaymentCredit(81000, Currency.USD, D("2026-01-05"), rates)];
        var before = ReceivableAsOf(schedule, credits, D("2026-04-10"), rates);
        var (credit, netPayable) = OffsetClaim(Money(20000, Currency.USD), before.Outstanding, D("2026-04-10"));
        Assert.Equal(90000, credit.Amount.Minor);
        Assert.Equal(1910000, netPayable.Minor);

        var after = ReceivableAsOf(schedule, [.. credits, credit], D("2026-04-10"), rates);
        Assert.Equal(0, after.Outstanding.Minor);
    }

    [Fact(DisplayName = "set-off never exceeds the claim amount")]
    public void SetOffNeverExceedsClaim()
    {
        var (credit, netPayable) = OffsetClaim(Money(500, Currency.USD), Money(900, Currency.USD), D("2026-04-10"));
        Assert.Equal(50000, credit.Amount.Minor);
        Assert.Equal(0, netPayable.Minor);
    }

    [Fact(DisplayName = "statutory UPR in GEL uses the rate of the effective date, not the reporting date")]
    public void StatutoryUprUsesEffectiveDateRate()
    {
        // 1 200 USD × 2.70 = 3 240 GEL fixed; rate on 31.03 is 2.75 and must not matter.
        var pos = PremiumPositionGel([policy], D("2026-03-31"), rates);
        Assert.Equal(new PremiumPosition(324000, 79890, 244110), pos); // 3240 × 275/365 = 2441.10
    }

    [Fact(DisplayName = "statutory IBNR is 5% of net written premium, 2% for health")]
    public void StatutoryIbnrRates()
    {
        Assert.Equal(4000000, StatutoryIbnr(100000000, 20000000, IbnrLine.Other));
        Assert.Equal(2000000, StatutoryIbnr(100000000, 0, IbnrLine.Health));
    }

    [Fact(DisplayName = "statutory IBNR exactly on bigints: the same rule as the number version on ordinary amounts, and exact where a JS number is not")]
    public void StatutoryIbnrExactOnBigInts()
    {
        // Ordinary amounts — every one the same as StatutoryIbnr: halves, near-equal, fully ceded, ceded above gross.
        foreach (var (gross, ceded) in new (long, long)[] { (100000000, 20000000), (36500, 0), (10, 0), (30, 0), (25, 0), (75, 0), (101, 100), (100, 100), (100, 101), (0, 0) })
        {
            foreach (var line in new[] { IbnrLine.Other, IbnrLine.Health }) Assert.Equal(new BigInteger(StatutoryIbnr(gross, ceded, line)), StatutoryIbnrExact(gross, ceded, line));
        }
        // A half tetri goes up: 5% of 0.10 = 0.005 → 0.01; 2% of 0.25 = 0.005 → 0.01; 5% of 0.09 = 0.0045 → 0.00.
        Assert.Equal(new BigInteger[] { 1, 1, 0 }, new[] { StatutoryIbnrExact(10, 0, IbnrLine.Other), StatutoryIbnrExact(25, 0, IbnrLine.Health), StatutoryIbnrExact(9, 0, IbnrLine.Other) });
        // Beyond 2^53 tetri: 5% of 9999999999999999.89 is 499999999999999.9945 → …999.99; 2% of …999.74 is …999.9948 → …999.99.
        Assert.Equal(BigInteger.Parse("49999999999999999"), StatutoryIbnrExact(BigInteger.Parse("999999999999999989"), 0, IbnrLine.Other));
        Assert.Equal(BigInteger.Parse("19999999999999999"), StatutoryIbnrExact(BigInteger.Parse("999999999999999974"), 0, IbnrLine.Health));
        // The net to its last tetri: 90071992547408.10 less 90071992547408.00 is 0.10 — 5% of it 0.01 (through numbers 0).
        Assert.Equal(BigInteger.One, StatutoryIbnrExact(BigInteger.Parse("9007199254740810"), BigInteger.Parse("9007199254740800"), IbnrLine.Other));
    }

    // Cases below reproduce the day-count rules of the user's supervisory premium report
    // (period 01.01.2017–30.06.2017), with amounts in GEL.
    private static PremiumTransaction Gel(decimal amount, string coverFrom, string coverTo, string? bookedOn = null) =>
        new(Money(amount, Currency.GEL), D(bookedOn ?? coverFrom), D(coverFrom), D(coverTo));

    [Fact(DisplayName = "report rule: the end date is not counted and a leap year gives 366 days")]
    public void ReportRuleEndDateNotCounted()
    {
        var tx = Gel(120.88m, "2016-01-07", "2017-01-07");
        Assert.Equal(198, EarnedInPeriod([tx], D("2017-01-01"), D("2017-06-30"))[Currency.GEL]); // 120.88 × 6/366
        Assert.Equal(0, PremiumPositionOf([tx], D("2017-06-30"))[Currency.GEL].Unearned);
    }

    [Fact(DisplayName = "report rule: a policy that starts near period end earns only its elapsed days")]
    public void ReportRuleStartsNearPeriodEnd()
    {
        var tx = Gel(100, "2017-06-29", "2017-12-12");
        var pos = PremiumPositionOf([tx], D("2017-06-30"))[Currency.GEL];
        Assert.Equal(new PremiumPosition(10000, 120, 9880), pos); // 100 × 2/166
    }

    [Fact(DisplayName = "report rule: an endorsement earns from its change date to the policy end")]
    public void ReportRuleEndorsement()
    {
        var change = Gel(-468, "2016-11-24", "2017-10-31");
        Assert.Equal(-24841, EarnedInPeriod([change], D("2017-01-01"), D("2017-06-30"))[Currency.GEL]); // −468 × 181/341
        Assert.Equal(-16744, PremiumPositionOf([change], D("2017-06-30"))[Currency.GEL].Unearned); // −468 × 122/341
    }

    [Fact(DisplayName = "report rule: cancellation returns the pro rata unearned part and stops earning")]
    public void ReportRuleCancellation()
    {
        var policy2017 = Gel(1260, "2017-01-05", "2018-01-04");
        var refund = CancellationOf(policy2017, D("2017-02-05"))!;
        Assert.Equal(-115269, refund.Amount.Minor); // 1260 × 333/364
        var pos = PremiumPositionOf([policy2017, refund], D("2017-06-30"))[Currency.GEL];
        Assert.Equal(new PremiumPosition(10731, 10731, 0), pos); // 1260 × 31/364 retained
    }

    [Fact(DisplayName = "commission is accrued at issue and earned together with the premium")]
    public void CommissionAccruedAtIssue()
    {
        var commission = CommissionTransaction(policy, 17.5m);
        Assert.Equal(21000, commission.Amount.Minor); // 1 200 × 17.5%
        var pos = PremiumPositionOf([commission], D("2026-03-31"))[Currency.USD];
        Assert.Equal(new PremiumPosition(21000, 5178, 15822), pos); // 210 × 90/365 expensed, rest deferred
    }

    [Fact(DisplayName = "commission becomes payable to the agent in proportion to paid premium")]
    public void CommissionPayableInProportion()
    {
        Assert.Equal(5250, CommissionPayable(21000, 120000, 30000)); // one of four instalments paid
        Assert.Equal(0, CommissionPayable(21000, 120000, 0));
        Assert.Equal(21000, CommissionPayable(21000, 120000, 150000)); // overpayment does not raise it
    }

    [Fact(DisplayName = "a policy becomes cancellable after the grace period on the oldest unpaid instalment")]
    public void CancellableAfterGracePeriod()
    {
        Credit[] credits = [PaymentCredit(81000, Currency.USD, D("2026-01-05"), rates)];
        var pos = ReceivableAsOf(schedule, credits, D("2026-04-10"), rates);
        Assert.Equal(D("2026-05-01"), CancellableFrom(pos, 30)); // second instalment due 01.04 + 30 days
        Assert.Null(CancellableFrom(pos, null)); // no automatic cancellation agreed
    }

    // Total loss on the 100th day of a 1 200 GEL policy with 300 paid: earned 328.77, unearned 871.23.
    private static readonly PremiumTransaction lossPolicy = Gel(1200, "2026-01-01", "2027-01-01");
    private static readonly Installment[] lossSchedule = schedule.Select(i => i with { Amount = Money(300, Currency.GEL) }).ToArray();
    private static readonly ReceivablePosition lossReceivable = ReceivableAsOf(
        lossSchedule,
        [new Credit(D("2026-01-05"), Money(300, Currency.GEL), CreditKind.Payment)],
        D("2026-04-11"),
        rates);

    private static TotalLossSettlement Loss(TotalLossDeduction deduction) =>
        SettleTotalLoss(new TotalLossInput([lossPolicy], lossReceivable, Money(20000, Currency.GEL), D("2026-04-11"), deduction));

    [Fact(DisplayName = "total loss, option A: policy is cancelled and only earned unpaid premium is deducted")]
    public void TotalLossOptionA()
    {
        var result = Loss(TotalLossDeduction.EarnedUnpaid);
        Assert.Equal(-87123, result.Cancellations[0].Amount.Minor); // 1 200 × 265/365 returned
        Assert.Equal(2877, result.Offset!.Credit.Amount.Minor); // 328.77 earned − 300 paid
        Assert.Equal(1997123, result.NetPayable.Minor);
        Assert.Equal(0, result.PremiumStillOwed.Minor);
        var after = PremiumPositionOf([lossPolicy, .. result.Cancellations], D("2026-12-31"))[Currency.GEL];
        Assert.Equal(new PremiumPosition(32877, 32877, 0), after);
    }

    [Fact(DisplayName = "total loss, option B: whole unpaid premium is deducted and the premium is kept")]
    public void TotalLossOptionB()
    {
        var result = Loss(TotalLossDeduction.AllUnpaid);
        Assert.Empty(result.Cancellations);
        Assert.Equal(90000, result.Offset!.Credit.Amount.Minor);
        Assert.Equal(1910000, result.NetPayable.Minor);
        Assert.Equal(0, result.PremiumStillOwed.Minor);
    }

    [Fact(DisplayName = "total loss without deduction: policy is cancelled and the earned unpaid premium stays owed")]
    public void TotalLossWithoutDeduction()
    {
        var result = Loss(TotalLossDeduction.None);
        Assert.Null(result.Offset);
        Assert.Equal(2000000, result.NetPayable.Minor);
        Assert.Equal(2877, result.PremiumStillOwed.Minor);
    }

    [Fact(DisplayName = "total loss after full payment: the unearned premium is due back to the client")]
    public void TotalLossAfterFullPayment()
    {
        var paidInFull = ReceivableAsOf(lossSchedule, [new Credit(D("2026-01-05"), Money(1200, Currency.GEL), CreditKind.Payment)], D("2026-04-11"), rates);
        var result = SettleTotalLoss(new TotalLossInput([lossPolicy], paidInFull, Money(20000, Currency.GEL), D("2026-04-11"), TotalLossDeduction.EarnedUnpaid));
        Assert.Equal(87123, result.PremiumRefundDue.Minor);
        Assert.Equal(2000000, result.NetPayable.Minor);
    }

    // Checks reproduced from the independent review of 03.10.2026 (Codex/review-finance-checks.mjs).
    [Fact(DisplayName = "review: cancellation keeps the original GEL valuation when official rates change")]
    public void ReviewCancellationKeepsGelValuation()
    {
        var issued = new PremiumTransaction(Money(1200, Currency.USD), D("2026-01-01"), D("2026-01-01"), D("2027-01-01"));
        var cancellation = CancellationOf(issued, D("2026-04-11"))!;
        RateProvider officialRates = (_, date) => date == D("2026-01-01") ? 2.7m : 3m;
        var retained = PremiumPositionGel([issued], D("2026-04-10"), officialRates).Earned;
        var final = PremiumPositionGel([issued, cancellation], D("2026-12-31"), officialRates);
        Assert.Equal(new PremiumPosition(retained, retained, 0), final); // 3 240 × 100/365 = 887.67
    }

    [Fact(DisplayName = "review: after cancellation the position is frozen on every remaining day")]
    public void ReviewPositionFrozenAfterCancellation()
    {
        var issued = Gel(1200, "2026-01-01", "2027-01-01");
        var cancellation = CancellationOf(issued, D("2026-04-11"))!;
        var retained = issued.Amount.Minor + cancellation.Amount.Minor;
        Assert.Equal(32877, retained);
        for (var offset = 0; offset < 265; offset++)
        {
            var date = AddDays(D("2026-04-11"), offset);
            var pos = PremiumPositionOf([issued, cancellation], date);
            Assert.Equal([Currency.GEL], pos.Keys);
            Assert.Equal(new PremiumPosition(retained, retained, 0), pos[Currency.GEL]);
        }
    }

    [Fact(DisplayName = "review: a nonexistent calendar date is rejected")]
    public void ReviewNonexistentDateRejected()
    {
        Assert.False(IsIsoDate("2026-02-30"));
        Assert.True(IsIsoDate("2024-02-29"));
        Assert.False(IsIsoDate("2026-02-29"));
        // A DateOnly cannot be 30 February: the text is refused where it is read (earnedAsOf threw on it).
        Assert.Throws<ArgumentException>(() => EarnedAsOf(policy, ParseIsoDate("2026-02-30")));
    }

    [Fact(DisplayName = "instalment dates fall on calendar months")]
    public void InstalmentDatesFallOnCalendarMonths()
    {
        Assert.Equal(D("2026-04-01"), AddMonths(D("2026-01-01"), 3));
        Assert.Equal(D("2026-02-28"), AddMonths(D("2026-01-31"), 1));
        Assert.Equal(D("2027-01-15"), AddMonths(D("2026-11-15"), 2));
    }

    [Fact(DisplayName = "the cancellation day itself is not earned (confirmed by the product owner)")]
    public void CancellationDayNotEarned()
    {
        var issued = Gel(3650, "2026-01-01", "2027-01-01"); // 10 GEL per day
        var cancellation = CancellationOf(issued, D("2026-01-11"))!;
        var pos = PremiumPositionOf([issued, cancellation], D("2026-01-11"))[Currency.GEL];
        Assert.Equal(new PremiumPosition(10000, 10000, 0), pos); // days 1–10 only
        Assert.Equal(0, EarnedInPeriod([issued, cancellation], D("2026-01-11"), D("2026-01-11"))[Currency.GEL]);
    }

    [Fact(DisplayName = "payment is allocated oldest instalment first at the payment-day rate")]
    public void PaymentAllocatedOldestFirst()
    {
        // docs/04 example: 810 GEL at 2.70 covers 300 USD; here 05.01 pays the first instalment fully.
        var balances = ReceivableAsOf(schedule, [], D("2026-01-05"), rates).Balances;
        var one = AllocatePayment(81000, D("2026-01-05"), Currency.USD, balances, rates);
        Assert.Equal([("1", 30000L, 81000L)], one.Lines.Select(l => (l.InstallmentId, l.Amount.Minor, l.AmountGel)));
        Assert.Equal(0, one.UnallocatedGel);

        // 1 700 GEL at 2.80 = 607.14 USD: instalment 1 and 2 in full, 7.14 USD of instalment 3.
        var two = AllocatePayment(170000, D("2026-04-10"), Currency.USD, balances, rates);
        Assert.Equal([("1", 30000L), ("2", 30000L), ("3", 714L)], two.Lines.Select(l => (l.InstallmentId, l.Amount.Minor)));
        Assert.Equal(170000, two.Lines.Sum(l => l.AmountGel) + two.UnallocatedGel);
        Assert.Equal(0, two.UnallocatedGel);
    }

    [Fact(DisplayName = "payment beyond the schedule stays as an unallocated advance")]
    public void PaymentBeyondScheduleStaysAsAdvance()
    {
        Credit[] paid = [new(D("2026-01-05"), Money(1100, Currency.USD), CreditKind.Payment)];
        var balances = ReceivableAsOf(schedule, paid, D("2026-04-10"), rates).Balances;
        var result = AllocatePayment(100000, D("2026-04-10"), Currency.USD, balances, rates); // 357.14 USD, 100 USD owed
        Assert.Equal([("4", 10000L)], result.Lines.Select(l => (l.InstallmentId, l.Amount.Minor)));
        Assert.Equal(28000, result.Lines[0].AmountGel);
        Assert.Equal(72000, result.UnallocatedGel);
    }

    [Fact(DisplayName = "payment in GEL on a GEL policy uses rate 1 and rejects non-positive amounts")]
    public void PaymentInGelUsesRateOne()
    {
        Installment[] gelSchedule = [new("a", D("2026-01-01"), Money(100, Currency.GEL))];
        var balances = ReceivableAsOf(gelSchedule, [], D("2026-01-02"), rates).Balances;
        var result = AllocatePayment(4000, D("2026-01-02"), Currency.GEL, balances, rates);
        Assert.Equal([new AllocationLine("a", Money(40, Currency.GEL), 4000, 1)], result.Lines);
        Assert.Equal(0, result.UnallocatedGel);
        Assert.Throws<ArgumentException>(() => AllocatePayment(0, D("2026-01-02"), Currency.GEL, balances, rates));
    }

    /// <summary>The old test's check(): an order exists, uses every line once, and keeps every running sum within [−limit, 0].</summary>
    private static IReadOnlyList<int> CheckMirror(BigInteger[] lines, BigInteger limit, int[]? hint = null)
    {
        var found = OrderMirrorLines(lines, limit, hint);
        Assert.True(found.Order is not null, $"no order for {string.Join(", ", lines)} within {limit}: {found.Reason}");
        var order = found.Order!;
        Assert.Equal(Enumerable.Range(0, lines.Length), order.Order());
        var sum = BigInteger.Zero;
        foreach (var i in order)
        {
            sum -= lines[i];
            Assert.True(sum <= 0 && sum >= -limit, $"running sum {sum} of {string.Join(", ", lines)} leaves [−{limit}, 0]");
        }
        return order;
    }

    private static BigInteger[] L(params long[] values) => values.Select(v => new BigInteger(v)).ToArray();

    [Fact(DisplayName = "the mirror lines of a reversal are ordered so that every running sum stays within the receipt (Codex H60, H61)")]
    public void MirrorLinesAreOrdered()
    {
        // Codex H60: receipt 100; +100 A, −80 A, +80 B, −60 B, +20 C. "A negative while one fits, else
        // the smallest positive" wrote −100, +60, −20 and was stuck; the search finds −100, +80, −80, +60, −20.
        Assert.Equal([0, 1, 2, 3, 4], CheckMirror(L(10000, -8000, 8000, -6000, 2000), 10000));
        // The same lines handed in another order: the search still finds a valid order.
        CheckMirror(L(2000, -6000, 8000, -8000, 10000), 10000);
        CheckMirror(L(8000, 2000, 10000, -6000, -8000), 10000, [0, 1, 2, 3, 4]);
        // C15: 365 allocated, 100 released, 60 and then 40 allocated; the hint (by created_at) puts the 40 before the release.
        CheckMirror(L(36500, 4000, -10000, 6000), 36500, [0, 1, 2, 3]);
        // A release and a full re-allocation; a receipt partly unallocated; a single line.
        CheckMirror(L(10000, -10000, 10000), 10000);
        CheckMirror(L(30000, -5000, 2000), 36500);
        CheckMirror(L(500), 500);
        // Lines handed release first are still a valid history (allocate, then release): ordered.
        Assert.Equal([1, 0], CheckMirror(L(-5000, 5000), 10000));
        // Not a valid history: more allocated than the receipt — the whole search ran ('impossible').
        var invalid = OrderMirrorLines(L(10000, 5000), 10000);
        Assert.Null(invalid.Order);
        Assert.Equal(MirrorOrderReason.Impossible, invalid.Reason);

        // Codex H61: receipt 100; +100 A, −80 A, +80 B, −60 B, +20 C, then seven times −1 C, +1 to another
        // policy — handed in the order the query returns it, [+100, −60, +20, −80, +80, (−1, +1) × 7]. The
        // per-line search ran out of its 200 000 steps on it; per amount it is 2 048 states at most.
        var sevenPairs = Enumerable.Range(0, 7).SelectMany(_ => new long[] { -100, 100 });
        var h61 = L([10000, -6000, 2000, -8000, 8000, .. sevenPairs]);
        var h61Order = CheckMirror(h61, 10000);
        var h61Found = OrderMirrorLines(h61, 10000);
        Assert.True(h61Found.Order is not null && h61Found.States <= 2048, $"H61 took {h61Found.States} states");
        Assert.Equal(19, h61Order.Count);
        // The same lines in their chronological order.
        CheckMirror(L([10000, -8000, 8000, -6000, 2000, .. sevenPairs]), 10000);
        // A search stopped at its limit says so ('budget'), which is not a verdict on the history.
        var stopped = OrderMirrorLines(h61, 10000, null, 3);
        Assert.Null(stopped.Order);
        Assert.Equal(MirrorOrderReason.Budget, stopped.Reason);

        // Whole tetri beyond 2^53 (Codex PR264_OFFSET_RECEIPT_MIRROR_DECIMAL_LOSS_20261017): a receipt of 90 071 992 547 409.93
        // (2^53 + 1 tetri) in lines [+receipt, +0.01, −0.01] — a valid history (+0.01, −0.01, +receipt). Mirroring the receipt
        // first and then +0.01 would take the running sum one tetri past −receipt; in floating point that tetri is lost and
        // the order would be taken. In big integers it is refused and an order within the receipt is found.
        var huge = BigInteger.Parse("9007199254740993");
        BigInteger[] hugeLines = [huge, 1, -1];
        var hugeFound = OrderMirrorLines(hugeLines, huge);
        Assert.NotNull(hugeFound.Order);
        var hugeSum = BigInteger.Zero;
        foreach (var i in hugeFound.Order!)
        {
            hugeSum -= hugeLines[i];
            Assert.True(hugeSum <= 0 && hugeSum >= -huge, $"the running sum {hugeSum} left [−receipt, 0]");
        }

        // Random valid histories (allocations and releases that never left [0, receipt]) are always ordered. The old test
        // drew them with its own generator and shuffled them with V8's sort; the exact histories it drew are kept in the
        // parity cases (finance-receivable.json, orderMirrorLines.testRandom / testPractical) and checked here the same way.
        var random = ParityCases.Load("finance-receivable.json").Cases("orderMirrorLines.testRandom").ToList();
        Assert.Equal(300, random.Count);
        foreach (var c in random) CheckMirror(ParityJson.BigInts(c.In["lines"]!), ParityJson.Big(c.In["limit"]!));
        // Practical size (H61): histories of up to 30 lines, many amounts repeated, handed in random order,
        // stay far inside the default limit; the worst of 500 is recorded against a bound of 1% of it.
        var practical = ParityCases.Load("finance-receivable.json").Cases("orderMirrorLines.testPractical").ToList();
        Assert.Equal(500, practical.Count);
        var worst = 0;
        foreach (var c in practical)
        {
            var lines = ParityJson.BigInts(c.In["lines"]!);
            var limit = ParityJson.Big(c.In["limit"]!);
            CheckMirror(lines, limit);
            worst = Math.Max(worst, OrderMirrorLines(lines, limit).States);
        }
        Assert.True(worst <= MirrorOrderMaxStates / 100, $"worst {worst} states");
    }

    [Fact(DisplayName = "an approved reduction is spread over the schedule in the chosen order (owner, 07.10.2026)")]
    public void ApprovedReductionIsSpread()
    {
        // 365 GEL in four instalments of 91.25; the first one paid.
        var four = new[] { "2026-04-01", "2026-07-01", "2026-10-01", "2027-01-01" }.Select((dueOn, n) => new Installment((n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), D(dueOn), Money(91.25m, Currency.GEL))).ToArray();
        var firstPaid = new Dictionary<string, long> { ["1"] = 9125 };
        long[] Amounts(Dictionary<string, long> m) => four.Select(i => m[i.Id]).ToArray();
        // Last first: the last instalment goes, the third shrinks (as the browser showed on 07.10.2026).
        Assert.Equal([9125, 9125, 8250, 0], Amounts(ReduceInstallments(four, firstPaid, 10000, ReductionOrder.LastFirst)));
        // Equally over the three unpaid ones: 33.34 + 33.33 + 33.33, the odd unit to the earliest.
        Assert.Equal([9125, 5791, 5792, 5792], Amounts(ReduceInstallments(four, firstPaid, 10000, ReductionOrder.EqualUnpaid)));
        // Three instalments paid, 100 off: the one unpaid instalment goes, the remaining 8.75 comes off
        // the last paid one — what was paid on it now exceeds it and stays to the client's credit.
        var threePaid = new Dictionary<string, long> { ["1"] = 9125, ["2"] = 9125, ["3"] = 9125 };
        Assert.Equal([9125, 9125, 8250, 0], Amounts(ReduceInstallments(four, threePaid, 10000, ReductionOrder.EqualUnpaid)));
        Assert.Equal([9125, 9125, 8250, 0], Amounts(ReduceInstallments(four, threePaid, 10000, ReductionOrder.LastFirst)));
        // Nothing to reduce leaves the schedule; more than the whole schedule is refused.
        Assert.Equal([9125, 9125, 9125, 9125], Amounts(ReduceInstallments(four, firstPaid, 0, ReductionOrder.EqualUnpaid)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReduceInstallments(four, firstPaid, 36501, ReductionOrder.EqualUnpaid));
        // An unpaid instalment smaller than its share takes what it can; the rest is shared again.
        Installment[] uneven = [new("a", D("2026-04-01"), Money(10, Currency.GEL)), new("b", D("2026-07-01"), Money(100, Currency.GEL)), new("c", D("2026-10-01"), Money(100, Currency.GEL))];
        var r = ReduceInstallments(uneven, new Dictionary<string, long>(), 9000, ReductionOrder.EqualUnpaid);
        Assert.Equal([0, 6000, 6000], new[] { "a", "b", "c" }.Select(id => r[id]));
    }

    [Fact(DisplayName = "after cancellation the schedule shrinks from the end to the retained premium")]
    public void ScheduleShrinksAfterCancellation()
    {
        // 1 200 USD in four, 328.77 retained: first instalment stays, second is cut, the rest go.
        var kept = RescheduleInstallments(schedule, 32877);
        Assert.Equal([("1", 30000L), ("2", 2877L)], kept.Select(i => (i.Id, i.Amount.Minor)));
        Assert.Empty(RescheduleInstallments(schedule, 0));
        Assert.Equal(120000, RescheduleInstallments(schedule, 500000).Sum(i => i.Amount.Minor));

        // With 600 USD already paid, the retained 328.77 leaves 271.23 to return to the client.
        Credit[] paid = [new(D("2026-01-05"), Money(600, Currency.USD), CreditKind.Payment)];
        var position = ReceivableAsOf(kept, paid, D("2026-04-10"), rates);
        Assert.Equal(0, position.Outstanding.Minor);
        Assert.Equal(27123, position.Overpaid.Minor);
    }
}
