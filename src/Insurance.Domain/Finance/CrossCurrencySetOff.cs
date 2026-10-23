namespace Insurance.Domain.Finance;

/// <summary>The set-off in the policy's currency and what it comes to in GEL.</summary>
/// <param name="Policy">Set off in the policy's currency (minor units).</param>
/// <param name="GelMinor">The same in GEL, tetri: what the set-off receipt records and the claim payment withholds.</param>
public sealed record SetOffAmounts(Money Policy, long GelMinor);

/// <summary>
/// The premium set-off of a GEL claim on a foreign-currency policy (H46; old program docs/07 §4.5, owner 06.10.2026).
/// The claim's money is in GEL; the premium debt stays in the policy's currency (docs/04 rule 3), and the set-off is a
/// money payment converted at the payment day's official rate (rule 2):
///
///   offset_policy = min(unpaid in the policy's currency, GEL after the deductible ÷ rate, rounded down to the cent)
///   offset_gel    = round(offset_policy × rate)
///
/// Rounding down keeps offset_gel at or below the GEL after the deductible, so the net is never negative. All in whole
/// minor units and the rate in millionths, exactly; never in binary floating point.
/// </summary>
public static class CrossCurrencySetOff
{
    private const long RateScale = 1_000_000;

    private static long Scaled(decimal rate)
    {
        if (!(rate > 0)) throw new ArgumentException("the set-off rate must be positive");
        return JsMath.Round(rate * RateScale);
    }

    /// <summary>The set-off for <paramref name="afterDeductibleGel"/> tetri against <paramref name="outstanding"/> unpaid premium, at <paramref name="rate"/>.</summary>
    public static SetOffAmounts Amounts(long afterDeductibleGel, Money outstanding, decimal rate)
    {
        if (outstanding.Currency == Currency.GEL) throw new ArgumentException("a GEL policy needs no conversion");
        var scaled = Scaled(rate);
        // Floor: both are positive, so integer division rounds down.
        var most = afterDeductibleGel <= 0 ? 0L : checked((long)((Int128)afterDeductibleGel * RateScale / scaled));
        var policy = Math.Max(0, Math.Min(outstanding.Minor, most));
        return new SetOffAmounts(new Money(policy, outstanding.Currency), Money.MulDiv(policy, scaled, RateScale));
    }

    /// <summary>
    /// The set-off allocated to the oldest unpaid instalments in the policy's currency, exactly <paramref name="amounts"/>.Policy;
    /// each line carries its GEL at the rate and the rate itself, the last line absorbing the rounding difference so the
    /// lines' GEL add up to the receipt.
    /// </summary>
    public static PaymentAllocation Allocate(SetOffAmounts amounts, IReadOnlyList<InstallmentBalance> balances, decimal rate)
    {
        if (!(amounts.Policy.Minor > 0)) throw new ArgumentException("the set-off must be positive");
        var scaled = Scaled(rate);
        var currency = amounts.Policy.Currency;
        var left = amounts.Policy.Minor;
        var parts = new List<(string InstallmentId, long Minor)>();
        foreach (var b in balances.Where(b => b.Outstanding > 0).OrderBy(b => b.Installment.DueOn.DayNumber))
        {
            if (left <= 0) break;
            var minor = Math.Min(left, b.Outstanding);
            left -= minor;
            parts.Add((b.Installment.Id, minor));
        }
        if (left > 0) throw new InvalidOperationException("the set-off exceeds the unpaid premium");
        var gels = parts.Select(p => Money.MulDiv(p.Minor, scaled, RateScale)).ToList();
        gels[^1] = amounts.GelMinor - gels.Take(gels.Count - 1).Sum();
        return new PaymentAllocation(parts.Select((p, i) => new AllocationLine(p.InstallmentId, new Money(p.Minor, currency), gels[i], rate)).ToList(), 0);
    }
}
