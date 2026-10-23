using Insurance.Domain.Finance;

namespace Insurance.Domain.Tests.Finance;

/// <summary>
/// The premium set-off of a GEL claim on a foreign-currency policy (H46; old program docs/07 §4.5 and its example §6 გ):
/// offset_policy = min(unpaid, floor to the cent of GEL after the deductible ÷ rate), offset_gel = round(offset_policy × rate),
/// allocated in the policy's currency.
/// </summary>
public class CrossCurrencySetOffTests
{
    private static InstallmentBalance Balance(string id, int month, long outstanding) =>
        new(new Installment(id, new DateOnly(2041, month, 1), new Money(outstanding, Currency.USD)), outstanding);

    [Fact(DisplayName = "docs/07 §6 გ: 100 USD unpaid, 1 000 GEL after the deductible, rate 2.70 — set-off 100 USD = 270 GEL, net 730")]
    public void Example()
    {
        var s = CrossCurrencySetOff.Amounts(100_000, new Money(10_000, Currency.USD), 2.7m);
        Assert.Equal(new Money(10_000, Currency.USD), s.Policy);
        Assert.Equal(27_000, s.GelMinor);
        Assert.Equal(73_000, 100_000 - s.GelMinor);
        // The Codex counterexample (PR #33): the same GEL taken as USD would set off 100 and pay 900.
        Assert.NotEqual(10_000, 100_000 - s.Policy.Minor);
    }

    [Fact(DisplayName = "the set-off is rounded down to the cent: 100.00 GEL at 2.70 sets off 37.03 USD = 99.98 GEL, net 0.02, never negative")]
    public void Floor()
    {
        var s = CrossCurrencySetOff.Amounts(10_000, new Money(10_000, Currency.USD), 2.7m);
        Assert.Equal(3_703, s.Policy.Minor);
        Assert.Equal(9_998, s.GelMinor);
        // Rounding to the nearest cent instead (37.04 USD) would come to 100.01 GEL, more than the claim.
        Assert.Equal(10_001, Money.MulDiv(3_704, 2_700_000, 1_000_000));
    }

    [Fact(DisplayName = "a debt smaller than the claim is set off in full: 50 USD = 135 GEL of 1 000, net 865")]
    public void SmallerDebt()
    {
        var s = CrossCurrencySetOff.Amounts(100_000, new Money(5_000, Currency.USD), 2.7m);
        Assert.Equal((5_000L, 13_500L), (s.Policy.Minor, s.GelMinor));
    }

    [Fact(DisplayName = "nothing unpaid or nothing after the deductible sets nothing off")]
    public void Nothing()
    {
        Assert.Equal(0, CrossCurrencySetOff.Amounts(100_000, new Money(0, Currency.USD), 2.7m).Policy.Minor);
        Assert.Equal(0, CrossCurrencySetOff.Amounts(0, new Money(10_000, Currency.USD), 2.7m).GelMinor);
        Assert.Throws<ArgumentException>(() => CrossCurrencySetOff.Amounts(100, new Money(100, Currency.GEL), 1m));
    }

    [Fact(DisplayName = "a rate below 1 keeps the set-off exact in the policy's currency, where the GEL round trip of AllocatePayment loses a cent")]
    public void SmallRate()
    {
        // 30.01 unpaid at 0.3333: 10.01 GEL after the deductible covers it (30.03); 30.01 × 0.3333 = 10.0023 → 10.00 GEL.
        var balance = new InstallmentBalance(new Installment("i", new DateOnly(2041, 1, 1), new Money(3_001, Currency.EUR)), 3_001);
        var s = CrossCurrencySetOff.Amounts(1_001, new Money(3_001, Currency.EUR), 0.3333m);
        Assert.Equal((3_001L, 1_000L), (s.Policy.Minor, s.GelMinor));
        var a = CrossCurrencySetOff.Allocate(s, [balance], 0.3333m);
        Assert.Equal(3_001, a.Lines.Sum(l => l.Amount.Minor));
        Assert.Equal(1_000, a.Lines.Sum(l => l.AmountGel));
        // Negative control: allocating the 10.00 GEL back through the rate settles 30.00, leaving 0.01 of the debt unpaid.
        var roundTrip = Receivable.AllocatePayment(1_000, new DateOnly(2041, 1, 1), Currency.EUR, [balance], (_, _) => 0.3333m);
        Assert.Equal(3_000, roundTrip.Lines.Sum(l => l.Amount.Minor));
    }

    [Fact(DisplayName = "allocated oldest first in the policy's currency, each line with its GEL and the rate; the last absorbs the rounding")]
    public void Allocation()
    {
        var s = CrossCurrencySetOff.Amounts(10_000, new Money(10_000, Currency.USD), 2.7m);
        var a = CrossCurrencySetOff.Allocate(s, [Balance("late", 3, 5_000), Balance("early", 1, 1_001), Balance("paid", 2, 0)], 2.7m);
        Assert.Equal(["early", "late"], a.Lines.Select(l => l.InstallmentId));
        Assert.Equal([1_001L, 2_702L], a.Lines.Select(l => l.Amount.Minor));
        Assert.All(a.Lines, l => Assert.Equal(Currency.USD, l.Amount.Currency));
        Assert.All(a.Lines, l => Assert.Equal(2.7m, l.Rate));
        Assert.Equal(2_703, a.Lines[0].AmountGel);           // 10.01 × 2.7 = 27.027
        Assert.Equal(9_998 - 2_703, a.Lines[1].AmountGel);  // the rest of 99.98
        Assert.Equal(0, a.UnallocatedGel);
        Assert.Throws<InvalidOperationException>(() => CrossCurrencySetOff.Allocate(new SetOffAmounts(new Money(20_000, Currency.USD), 54_000), [Balance("x", 1, 100)], 2.7m));
    }
}
