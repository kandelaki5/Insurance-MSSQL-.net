using System.Globalization;
using System.Numerics;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The policy card's premium position and commission to the tetri (legacy payment-shared-page.test.ts,
/// POLICY_CARD_PREMIUM_POSITION_DECIMAL_PRECISION_20261019 and POLICY_CARD_COMMISSION_*_20261019), through
/// PolicyCard.ExactPremiumPosition — the calculation the card page runs over the policy's ledger as of today (the page
/// itself: GapsCardDisplayTests). The legacy ledgers and "today" 03.11.2026 are kept: the calculation takes the day.
/// Expected figures by hand, as in the legacy test (the arithmetic in the comments). No database.
/// </summary>
public sealed class GapsCardPositionTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static DateOnly D(string s) => DateOnly.Parse(s, CultureInfo.InvariantCulture);
    private static readonly Guid[] Ids = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())];

    private static PolicyCard.LedgerRow Tx(int n, string amount, string booked, string from, string to, int? cancels = null) =>
        new(Ids[n], cancels is { } c ? Ids[c] : null, decimal.Parse(amount, CultureInfo.InvariantCulture), D(booked), D(from), D(to));

    private static string[] Shown(PolicyCard.Position p) => [PolicyCard.Grouped(p.Written), PolicyCard.Grouped(p.Earned), PolicyCard.Grouped(p.Unearned)];

    private static (string[] Currency, string[] Gel) Position(PolicyCard.LedgerRow[] rows, string currency = "GEL", decimal rate = 1m)
    {
        var (p, g) = PolicyCard.ExactPremiumPosition(rows, Today, currency, rate);
        return (Shown(p), Shown(g));
    }

    [Theory]
    [InlineData("90071992547408.01", "90,071,992,547,408.01")]
    [InlineData("9999999999999999.99", "9,999,999,999,999,999.99")]
    public void A_one_day_GEL_movement_of_the_largest_amounts_all_earned_to_the_tetri(string amount, string shown) =>
        Assert.Equal([shown, shown, "0.00"], Position([Tx(0, amount, "2026-10-01", "2026-10-01", "2026-10-02")]).Gel);

    [Fact]
    public void USD_at_the_fixed_rate_33359997239780_01_at_2_7_is_90071992547406_03()
    {
        var (usd, gel) = Position([Tx(0, "33359997239780.01", "2026-10-01", "2026-10-01", "2026-10-02")], "USD", 2.7m);
        Assert.Equal(["33,359,997,239,780.01", "33,359,997,239,780.01", "0.00"], usd);
        Assert.Equal(["90,071,992,547,406.03", "90,071,992,547,406.03", "0.00"], gel);
    }

    [Fact]
    public void Pro_rata_over_a_year_as_of_the_34th_day_of_365_the_largest_amounts_in_the_currency_and_in_GEL_each_from_the_whole_movement()
    {
        // 9007199254740801 × 34 / 365 = 839026779893663.65… → 839026779893664.
        Assert.Equal(["90,071,992,547,408.01", "8,390,267,798,936.64", "81,681,724,748,471.37"], Position([Tx(0, "90071992547408.01", "2026-10-01", "2026-10-01", "2027-10-01")]).Gel);
        // USD: 3335999723978001 × 34 / 365 → 310750659219869; GEL 9007199254740603 × 34 / 365 → 839026779893645 (the GEL movement first).
        var (usd, gel) = Position([Tx(0, "33359997239780.01", "2026-10-01", "2026-10-01", "2027-10-01")], "USD", 2.7m);
        Assert.Equal(["33,359,997,239,780.01", "3,107,506,592,198.69", "30,252,490,647,581.32"], usd);
        Assert.Equal(["90,071,992,547,406.03", "8,390,267,798,936.45", "81,681,724,748,469.58"], gel);
    }

    [Fact]
    public void Each_movement_rounded_on_its_own_half_away_from_zero_three_0_01_and_one_minus_0_01_over_two_days()
    {
        // Each: 1 × 1 / 2 = 0.5 → 1; −0.5 → −1. Earned 1 + 1 + 1 − 1 = 2 → 0.02 of the 0.02 written.
        Assert.Equal(["0.02", "0.02", "0.00"], Position([Tx(0, "0.01", "2026-11-03", "2026-11-03", "2026-11-05"), Tx(1, "0.01", "2026-11-03", "2026-11-03", "2026-11-05"),
            Tx(2, "0.01", "2026-11-03", "2026-11-03", "2026-11-05"), Tx(3, "-0.01", "2026-11-03", "2026-11-03", "2026-11-05")]).Gel);
    }

    [Fact]
    public void A_cancellation_read_before_its_original_linked_one_booked_after_today_not_yet_its_GEL_from_the_originals_GEL()
    {
        // USD 100.00 for 365 days from 01.10 at 2.7 (270.00 GEL); cancelled from 21.10: −94.52; in GEL 270.00 − 14.79 = −255.21.
        var (usd, gel) = Position([Tx(1, "-94.52", "2026-10-21", "2026-10-21", "2027-10-01", 0), Tx(0, "100.00", "2026-10-01", "2026-10-01", "2027-10-01"),
            Tx(2, "-50.00", "2026-11-10", "2026-11-10", "2027-10-01", 0)], "USD", 2.7m);
        Assert.Equal(["5.48", "5.48", "0.00"], usd);
        Assert.Equal(["14.79", "14.79", "0.00"], gel);
    }

    [Fact]
    public void A_cancellation_undone_the_same_day_nets_to_nothing_written_is_earned_plus_unearned() =>
        Assert.Equal(["365.00", "34.00", "331.00"], Position([Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01"),
            Tx(1, "-345.00", "2026-10-21", "2026-10-21", "2027-10-01", 0), Tx(2, "345.00", "2026-10-25", "2026-10-21", "2027-10-01", 1)]).Gel);

    [Fact]
    public void A_cancellation_whose_original_is_not_read_is_refused_never_taken_for_a_plain_negative_premium()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Position([Tx(1, "-94.52", "2026-10-21", "2026-10-21", "2027-10-01", 4)]));
        Assert.Contains("which is not among the rows read", e.Message);
    }

    // ---------------------------------------------------------------- the commission

    private static string[] Commission(PolicyCard.LedgerRow[] rows, string[] percents, string currency = "GEL", decimal rate = 1m)
    {
        var bp = percents.Aggregate(BigInteger.Zero, (s, p) => s + PaymentActions.Exact(decimal.Parse(p, CultureInfo.InvariantCulture)));
        var g = PolicyCard.ExactPremiumPosition(rows, Today, currency, rate, (long)bp).PositionGel;
        return [PolicyCard.Grouped(g.Written), PolicyCard.Grouped(g.Unearned)];
    }

    [Theory]
    [InlineData("90071992547408.01", "90,071,992,547,408.01")]
    [InlineData("9999999999999999.99", "9,999,999,999,999,999.99")]
    public void Commission_a_one_day_movement_of_the_largest_amounts_at_100_percent_to_the_tetri(string amount, string shown) =>
        Assert.Equal([shown, "0.00"], Commission([Tx(0, amount, "2026-10-01", "2026-10-01", "2026-10-02")], ["100.00"]));

    [Fact]
    public void Commission_a_cancellation_cancels_the_commissions_original_not_the_gross_premium_365_at_10_percent_cancelled_from_01_11_is_3_10_and_0_00() =>
        // Commission 36.50 over 365 days; cancelled from 01.11 (−334.00 → −33.40): written 3.10, earned 3.40 − 0.30, nothing deferred.
        Assert.Equal(["3.10", "0.00"], Commission([Tx(1, "-334.00", "2026-11-01", "2026-11-01", "2027-10-01", 0), Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01")], ["10.00"]));

    [Fact]
    public void Commission_a_cancellation_booked_later_and_a_cancellation_undone_leave_the_original_commission_alone()
    {
        Assert.Equal(["36.50", "33.10"], Commission([Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01"), Tx(1, "-334.00", "2026-11-10", "2026-11-01", "2027-10-01", 0)], ["10.00"]));
        Assert.Equal(["36.50", "33.10"], Commission([Tx(0, "365.00", "2026-10-01", "2026-10-01", "2027-10-01"), Tx(1, "-345.00", "2026-10-21", "2026-10-21", "2027-10-01", 0),
            Tx(2, "345.00", "2026-10-25", "2026-10-21", "2027-10-01", 1)], ["10.00"]));
    }

    [Fact]
    public void Commission_USD_at_the_policys_fixed_rate_the_cancellations_GEL_from_the_commission_originals_GEL() =>
        // 10.00 USD = 27.00 GEL; cancelled from 21.10: 27.00 − 1.48 = 25.52 → written 1.48; deferred 0.00.
        Assert.Equal(["1.48", "0.00"], Commission([Tx(0, "100.00", "2026-10-01", "2026-10-01", "2027-10-01"), Tx(1, "-94.52", "2026-10-21", "2026-10-21", "2027-10-01", 0)], ["10.00"], "USD", 2.7m));

    [Fact]
    public void Commission_several_intermediaries_the_percents_summed_exactly_each_movement_rounded_on_its_own()
    {
        Assert.Equal(["132.50", "0.00"], Commission([Tx(0, "1000.00", "2026-10-01", "2026-10-01", "2026-10-02")], ["12.50", "0.75"]));
        Assert.Equal("0.01", Commission([Tx(0, "0.03", "2026-10-01", "2026-10-01", "2026-10-02")], ["33.33"])[0]);
        // Three 0.01 at 50 %: 0.005 → 0.01 each, 0.03 in all (rounded once on the 0.03 it would be 0.02).
        Assert.Equal("0.03", Commission([Tx(0, "0.01", "2026-10-01", "2026-10-01", "2026-10-02"), Tx(1, "0.01", "2026-10-01", "2026-10-01", "2026-10-02"),
            Tx(2, "0.01", "2026-10-01", "2026-10-01", "2026-10-02")], ["50.00"])[0]);
    }

    [Fact]
    public void Commission_linked_not_a_plain_negative_0_12_over_7_days_at_12_50_percent_cancelled_from_02_11_is_0_00_and_0_00() =>
        Assert.Equal(["0.00", "0.00"], Commission([Tx(0, "0.12", "2026-11-01", "2026-11-01", "2026-11-08"), Tx(1, "-0.10", "2026-11-02", "2026-11-02", "2026-11-08", 0)], ["12.50"]));
    // ---------------------------------------------------------------- the receivable (POLICY_CARD_RECEIVABLE_DECIMAL_PRECISION_20261019)

    private static readonly Guid T1 = Guid.NewGuid(), T2 = Guid.NewGuid(), T3 = Guid.NewGuid();

    private static Billing.InstallmentRow I(Guid id, string due, string amount) => new(id, D(due), decimal.Parse(amount, CultureInfo.InvariantCulture));

    private static Billing.AllocationRow C(string amount, string received) => new(decimal.Parse(amount, CultureInfo.InvariantCulture), D(received), "bank");

    /// <summary>What the card shows: the debt, what is paid beyond, the schedule [due, amount, unpaid, overdue], cancellable from (30 days' grace).</summary>
    private static (string Owed, string Beyond, string[] Schedule, DateOnly? Cancel) Receivable(Billing.InstallmentRow[] schedule, Billing.AllocationRow[] credits, int? grace = 30)
    {
        var r = PolicyCard.ExactReceivable(schedule, credits, Today, "GEL", 1m);
        return (PolicyCard.Grouped(r.Outstanding), PolicyCard.Grouped(r.Overpaid),
            [.. r.Balances.Select(b => $"{b.DueOn:dd.MM.yyyy}|{PolicyCard.Grouped(b.Amount)}|{PolicyCard.Grouped(b.Outstanding)}|{(b.Outstanding > 0 && b.DueOn < Today ? "overdue" : "")}")],
            PolicyCard.ExactCancellableFrom(r.Balances, grace));
    }

    [Fact]
    public void Receivable_Codexs_four_cases_0_01_owed_or_paid_beyond_never_0_00_or_0_02()
    {
        var a = Receivable([I(T1, "2026-10-01", "90071992547408.02")], [C("90071992547408.01", "2026-10-02")]);
        Assert.Equal(("0.01", "0.00", (DateOnly?)D("2026-10-31")), (a.Owed, a.Beyond, a.Cancel));
        Assert.Equal(["01.10.2026|90,071,992,547,408.02|0.01|overdue"], a.Schedule);
        Assert.Equal("0.01", Receivable([I(T1, "2026-10-01", "90071992547408.01")], [C("90071992547408.00", "2026-10-02")]).Owed);
        var beyond = Receivable([I(T1, "2026-10-01", "90071992547408.00")], [C("90071992547408.01", "2026-10-02")]);
        Assert.Equal(("0.00", "0.01", (DateOnly?)null), (beyond.Owed, beyond.Beyond, beyond.Cancel));
        Assert.Equal(["01.10.2026|90,071,992,547,408.00|0.00|"], beyond.Schedule);
        Assert.Equal("0.01", Receivable([I(T1, "2026-10-01", "90071992547408.01")], [C("90071992547408.02", "2026-10-02")]).Beyond);
    }

    [Fact]
    public void Receivable_USD_revalued_at_the_reporting_days_rate_exactly_33359997239780_01_at_2_7_and_0_01_beyond_is_0_03_GEL()
    {
        var r = PolicyCard.ExactReceivable([I(T1, "2026-10-01", "33359997239780.01")], [], Today, "USD", 2.7m);
        Assert.Equal(("33,359,997,239,780.01", "90,071,992,547,406.03"), (PolicyCard.Grouped(r.Outstanding), PolicyCard.Grouped(r.OutstandingGel)));
        var b = PolicyCard.ExactReceivable([I(T1, "2026-10-01", "100.00")], [C("100.01", "2026-10-02")], Today, "USD", 2.7m);
        Assert.Equal(("0.00", "0.00", "0.01", "0.03"), (PolicyCard.Grouped(b.Outstanding), PolicyCard.Grouped(b.OutstandingGel), PolicyCard.Grouped(b.Overpaid), PolicyCard.Grouped(b.InGel(b.Overpaid))));
    }

    [Fact]
    public void Receivable_oldest_due_first_a_credit_received_after_today_not_yet_the_schedule_as_it_stands_today_the_largest_instalment()
    {
        // 120.00 by today settles T1 (100.00) and 20.00 of T2 (40.00 after its reduction booked 01.11); T3's reduction of 20.11 not in force yet.
        var r = Receivable([I(T1, "2026-10-01", "100.00"), I(T2, "2026-11-01", "40.00"), I(T3, "2026-12-01", "50.00")], [C("120.00", "2026-10-05"), C("30.00", "2026-11-10")]);
        Assert.Equal(["01.10.2026|100.00|0.00|", "01.11.2026|40.00|20.00|overdue", "01.12.2026|50.00|50.00|"], r.Schedule);
        Assert.Equal(("70.00", (DateOnly?)D("2026-12-01")), (r.Owed, r.Cancel));
        Assert.Equal(["01.10.2026|9,999,999,999,999,999.99|9,999,999,999,999,999.98|overdue"], Receivable([I(T1, "2026-10-01", "9999999999999999.99")], [C("0.01", "2026-10-02")]).Schedule);
        Assert.Equal("90,071,992,547,408.01", Receivable([I(T1, "2026-10-01", "100.00")], [C("90071992547508.01", "2026-10-02")]).Beyond);
    }

    [Fact]
    public void Receivable_a_credit_and_a_refund_net_off_nothing_owed_nothing_beyond()
    {
        var r = Receivable([I(T1, "2026-10-01", "100.00")], [C("100.01", "2026-10-02"), C("-0.01", "2026-10-20")]);
        Assert.Equal(("0.00", "0.00"), (r.Owed, r.Beyond));
    }

    [Fact]
    public void Receivable_the_grace_none_no_date_0_the_due_date_itself()
    {
        Billing.InstallmentRow[] schedule = [I(T1, "2026-10-01", "90071992547408.02")];
        Billing.AllocationRow[] credits = [C("90071992547408.01", "2026-10-02")];
        Assert.Null(Receivable(schedule, credits, null).Cancel);
        Assert.Equal(D("2026-10-01"), Receivable(schedule, credits, 0).Cancel);
    }
}
