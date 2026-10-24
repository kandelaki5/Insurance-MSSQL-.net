using static Insurance.Domain.Finance.Dates;

namespace Insurance.Domain.Finance;

/// <summary>
/// What cancelling on a day would mean (the old CancelPreview).
/// </summary>
/// <param name="On">The day the preview is for, kept inside the cover (from its first to its last day).</param>
/// <param name="Balance">Positive: the client still owes this much; negative: this much is to be returned.</param>
public sealed record CancelPreviewResult(DateOnly On, long Written, long Refund, long Retained, long Paid, long Balance);

/// <summary>
/// What cancelling a policy on a given day would mean, before it is done: the premium written,
/// the unearned part returned (pure pro rata, no fee — docs/04 rule 9), what is retained, and
/// whether the client still owes or is owed. The same rule as the cancellation itself
/// (CancellationOf), evaluated on the date the employee chose rather than today (UAT H22). (cancel-preview.ts)
/// </summary>
public static class CancelPreview
{
    /// <summary>
    /// `rows` are the policy's ledger rows not yet cancelled, in minor units of one currency;
    /// `paidMinor` is what has been allocated to its schedule. A date outside the cover is moved to
    /// its nearest day, as the form allows only those. `cover.To` is the first day after the cover.
    /// </summary>
    public static CancelPreviewResult CancellationPreview(IEnumerable<PremiumTransaction> rows, DateOnly on, long paidMinor, DateRange cover)
    {
        var last = AddDays(cover.To, -1);
        var day = DayNumber(on) < DayNumber(cover.From) ? cover.From : DayNumber(on) > DayNumber(last) ? last : on;
        long written = 0;
        long refund = 0;
        foreach (var tx in rows)
        {
            written += tx.Amount.Minor;
            refund -= EarnedPremium.CancellationOf(tx, day)?.Amount.Minor ?? 0;
        }
        var retained = written - refund;
        return new CancelPreviewResult(day, written, refund, retained, paidMinor, retained - paidMinor);
    }
}
