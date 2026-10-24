namespace Insurance.Domain.Finance;

/// <summary>Deductible on claim payments (deductible.ts).</summary>
public static class Deductible
{
    /// <summary>
    /// Deductible to withhold from one claim payment: whatever part of the agreed deductible has
    /// not been withheld from earlier payments, up to this payment's gross amount. Over any
    /// number of partial payments the withheld total reaches exactly the agreed deductible and
    /// never more; the net paid is never negative (review 03.10.2026, H02).
    /// </summary>
    public static long DeductibleToWithhold(long agreedMinor, long alreadyWithheldMinor, long grossMinor) =>
        Math.Max(0, Math.Min(agreedMinor - alreadyWithheldMinor, grossMinor));

    /// <summary>
    /// The deductible of one class in one loss event (owner, 06.10.2026: "ფრანშიზა ერთხელ აკლდება";
    /// docs/07 §10, Codex H50): the coverages of a class that the event touches share one deductible,
    /// withheld once over all their payments. Given each coverage's agreed deductible, the shared one
    /// when they agree; null when they differ — which deductible then applies is not decided yet, so
    /// the payment is refused until it is.
    /// </summary>
    public static long? ClassDeductible(IReadOnlyList<long> agreedMinor)
    {
        if (agreedMinor.Count == 0) return 0;
        return agreedMinor.All(d => d == agreedMinor[0]) ? agreedMinor[0] : null;
    }
}
