namespace Insurance.Domain.Finance;

/// <summary>Commission on premium (commission.ts).</summary>
public static class Commission
{
    /// <summary>
    /// Commission on a premium movement. It is accrued when the policy is issued and then
    /// follows the premium: feeding the result to the earned-premium functions gives the
    /// commission expense of a period and the deferred commission at a date.
    /// The percent is taken to two decimals (Math.round(percent × 100)); the row keeps its other fields, its
    /// <see cref="PremiumTransaction.Cancels"/> link included.
    /// </summary>
    public static PremiumTransaction CommissionTransaction(PremiumTransaction tx, decimal percent) =>
        tx with { Amount = tx.Amount with { Minor = Money.MulDiv(tx.Amount.Minor, JsMath.Round(percent * 100), 10_000) } };

    /// <summary>
    /// Commission the insurer owes the intermediary so far: it becomes due in proportion
    /// to the premium actually paid.
    /// </summary>
    public static long CommissionPayable(long commissionMinor, long premiumMinor, long paidMinor)
    {
        if (premiumMinor <= 0) return 0;
        return Money.MulDiv(commissionMinor, Math.Min(Math.Max(paidMinor, 0), premiumMinor), premiumMinor);
    }
}
