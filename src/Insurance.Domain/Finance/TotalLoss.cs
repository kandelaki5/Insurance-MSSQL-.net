namespace Insurance.Domain.Finance;

/// <summary>
/// What to deduct from the indemnity when the insured object is destroyed. Chosen per case:
/// - None: the policy is cancelled, nothing is deducted;
/// - EarnedUnpaid: the policy is cancelled and only premium earned up to the loss but not yet paid is deducted;
/// - AllUnpaid: the whole unpaid premium is deducted and the insurer keeps the full premium (no cancellation).
/// </summary>
public enum TotalLossDeduction
{
    None,
    EarnedUnpaid,
    AllUnpaid,
}

/// <param name="Cancellations">Ledger rows to post; empty when the premium is kept in full.</param>
/// <param name="Offset">Set-off against the claim, when a deduction was chosen.</param>
/// <param name="PremiumStillOwed">Premium the policyholder still owes after the settlement.</param>
/// <param name="PremiumRefundDue">Premium to return when more was paid than was earned.</param>
public sealed record TotalLossSettlement(IReadOnlyList<PremiumTransaction> Cancellations, ClaimOffset? Offset, Money NetPayable, Money PremiumStillOwed, Money PremiumRefundDue);

public sealed record TotalLossInput(IReadOnlyList<PremiumTransaction> Premium, ReceivablePosition Receivable, Money ClaimAmount, DateOnly LossOn, TotalLossDeduction Deduction);

/// <summary>Settlement of a total loss (total-loss.ts; docs/04 rule 15).</summary>
public static class TotalLoss
{
    public static TotalLossSettlement SettleTotalLoss(TotalLossInput input)
    {
        var (premium, receivable, claimAmount, lossOn, deduction) = input;
        var currency = claimAmount.Currency;
        Money Of(long minor) => new(minor, currency);

        if (deduction == TotalLossDeduction.AllUnpaid)
        {
            var allOffset = Receivable.OffsetClaim(claimAmount, receivable.Outstanding, lossOn);
            return new TotalLossSettlement(
                [],
                allOffset,
                allOffset.NetPayable,
                Of(receivable.Outstanding.Minor - allOffset.Credit.Amount.Minor),
                Of(0));
        }

        var cancellations = premium.SelectMany(tx => EarnedPremium.CancellationOf(tx, lossOn) is { } c ? [c] : Array.Empty<PremiumTransaction>()).ToList();
        var returned = -cancellations.Sum(tx => tx.Amount.Minor);
        // Debt left once the unearned premium is taken off; negative means the client overpaid.
        var owed = receivable.Outstanding.Minor - receivable.Overpaid.Minor - returned;
        var offset = deduction == TotalLossDeduction.EarnedUnpaid ? Receivable.OffsetClaim(claimAmount, Of(Math.Max(owed, 0)), lossOn) : null;
        var settled = offset?.Credit.Amount.Minor ?? 0;
        return new TotalLossSettlement(
            cancellations,
            offset,
            offset?.NetPayable ?? claimAmount,
            Of(Math.Max(owed, 0) - settled),
            Of(Math.Max(-owed, 0)));
    }
}
