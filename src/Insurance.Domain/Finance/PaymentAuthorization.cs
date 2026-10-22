namespace Insurance.Domain.Finance;

/// <param name="GelMinor">The payment's GEL equivalent in tetri on the payment day.</param>
public sealed record PaymentAuthorizationResult(long GelMinor, bool RequiresAuthorization);

/// <summary>
/// Whether one outgoing payment needs the financial director's authorisation (owner, 17.10.2026, docs/05 ვ8: "10 000
/// ლარზე ზევით გადარიცხვას ფინანსური დირექტორის ავტორიზაცია სჭირდება"; claim payments, premium refunds and premium
/// receipt reversals; "ერთი გადახდა, დღის კურსით"). Codex INS-FD-PAYMENT-THRESHOLD-FOUNDATION-20261017.
/// (payment-authorization.ts; its function paymentAuthorization is <see cref="Check(Money, DateOnly, RateProvider, long)"/>.)
///
/// - One payment at a time: nothing is summed across payments; each call sees only its own amount.
/// - Its GEL equivalent is the payment's amount at the official rate of the payment day, by the same ToGel as every other
///   conversion (tetri, half away from zero); a GEL payment needs no rate.
/// - "ზევით" is read as strictly above: the threshold itself does not need it, one tetri more does (docs/05 ვ8, the
///   technical reading noted there).
/// - The threshold is the company's setting, passed in; this does not fix its value.
/// - A missing rate is an error, never "not needed".
///
/// Which amount of an action is "the payment" (a claim payment's gross, net or net in GEL) is decided where the action
/// calls this, not here. Nothing is gated by this yet.
/// </summary>
public static class PaymentAuthorization
{
    public static PaymentAuthorizationResult Check(Money payment, DateOnly paidOn, RateProvider rates, long thresholdGelMinor)
    {
        CheckAmounts(payment, thresholdGelMinor);
        var gelMinor = Money.ToGel(payment, paidOn, rates);
        return new PaymentAuthorizationResult(gelMinor, gelMinor > thresholdGelMinor);
    }

    /// <summary>The same check for a payment day given as text: a text that is not a real 'YYYY-MM-DD' date is refused.</summary>
    public static PaymentAuthorizationResult Check(Money payment, string paidOn, RateProvider rates, long thresholdGelMinor)
    {
        CheckAmounts(payment, thresholdGelMinor);
        if (!Dates.IsIsoDate(paidOn) || paidOn.StartsWith("0000", StringComparison.Ordinal)) throw new ArgumentException($"Not a payment day: {paidOn}");
        return Check(payment, Dates.ParseIsoDate(paidOn), rates, thresholdGelMinor);
    }

    private static void CheckAmounts(Money payment, long thresholdGelMinor)
    {
        if (!JsMath.IsSafeInteger(payment.Minor) || payment.Minor <= 0) throw new ArgumentException($"A payment amount must be a positive whole number of minor units: {JsMath.Text(payment.Minor)}");
        if (!JsMath.IsSafeInteger(thresholdGelMinor) || thresholdGelMinor < 0) throw new ArgumentException($"The authorisation threshold must be a whole number of tetri: {JsMath.Text(thresholdGelMinor)}");
    }
}
