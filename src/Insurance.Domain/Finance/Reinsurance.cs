using System.Numerics;
using static Insurance.Domain.Finance.Dates;

namespace Insurance.Domain.Finance;

public enum TreatyKind
{
    QuotaShare,
    Surplus,
    ExcessOfLoss,
    Facultative,
}

/// <summary>Terms that decide the ceded share; amounts are in minor units of the treaty currency.</summary>
/// <param name="CommissionPercent">Reinsurance commission on ceded premium, percent.</param>
/// <param name="CededPercent">Quota share and facultative: the ceded percentage.</param>
/// <param name="Retention">Surplus: the insurer's retention per risk.</param>
/// <param name="Lines">Surplus: how many lines the treaty takes.</param>
/// <param name="Priority">Excess of loss: the insurer's priority per claim.</param>
/// <param name="Limit">Excess of loss: the treaty's limit above the priority.</param>
public sealed record TreatyTerms(TreatyKind Kind, decimal CommissionPercent, decimal? CededPercent = null, long? Retention = null, int? Lines = null, long? Priority = null, long? Limit = null);

/// <summary>The part of the terms a claim share needs (Pick&lt;TreatyTerms, 'kind' | 'priority' | 'limit'&gt;).</summary>
public sealed record ClaimTreatyTerms(TreatyKind Kind, long? Priority = null, long? Limit = null);

/// <summary>ClaimTreatyTerms on big integers.</summary>
public sealed record ClaimTreatyTermsExact(TreatyKind Kind, BigInteger? Priority = null, BigInteger? Limit = null);

/// <summary>One treaty's share of a risk, for <see cref="Reinsurance.CheckCessionPlan"/>. The currency is the treaty's code as stored.</summary>
public sealed record CessionShare(TreatyKind Kind, decimal SharePercent, string Currency);

public enum CessionProblem
{
    OverCeded,
    XolWithProportional,
    CurrencyMismatch,
    XolLayers,
}

/// <summary>
/// Outward reinsurance: what part of a policy's premium and of a claim belongs to the reinsurer. (reinsurance.ts)
///
/// Supervisory rules (reserves order №01/2013 art. 5–7; financial reporting order №02/2013
/// annex 2 art. 2.11, 4.2, 4.6): the reinsurer's share in UPR is computed per policy with the
/// same day-count and the same fixed GEL rate as the premium, gross of reinsurance commission;
/// the share in RBNS is computed per claim under the treaty; there is no share in IBNR; the
/// reinsurer's share in a paid claim is recognised when the claim is paid, whether or not the
/// reinsurer has settled.
/// </summary>
public static class Reinsurance
{
    /// <summary>Share percent is kept to four decimals; this is the exact scale used for arithmetic.</summary>
    private const long PercentScale = 10_000;

    public static long PercentToUnits(decimal percent) => JsMath.Round(percent * PercentScale);

    /// <summary>
    /// Percent of a risk ceded under the treaty, from the sum insured (minor units, treaty currency).
    /// Excess-of-loss treaties cede no proportional share: their premium is a cost of the treaty.
    /// </summary>
    public static decimal CededPercentOf(TreatyTerms terms, long? sumInsuredMinor)
    {
        switch (terms.Kind)
        {
            case TreatyKind.QuotaShare:
            case TreatyKind.Facultative:
                return ClampPercent(terms.CededPercent ?? 0);
            case TreatyKind.Surplus:
                {
                    var retention = terms.Retention ?? 0;
                    var capacity = retention * (terms.Lines ?? 0);
                    if (sumInsuredMinor is not { } sum || sum == 0 || sum <= retention || retention <= 0) return 0;
                    var ceded = Math.Min(sum - retention, capacity);
                    return Money.MulDiv(ceded, 100 * PercentScale, sum) / (decimal)PercentScale;
                }
            case TreatyKind.ExcessOfLoss:
                return 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(terms));
        }
    }

    /// <summary>The percent to four decimals (Number(percent.toFixed(4))), within 0–100.</summary>
    private static decimal ClampPercent(decimal percent) =>
        Math.Min(100, Math.Max(0, Math.Round(percent, 4, MidpointRounding.AwayFromZero)));

    /// <summary>Part of an amount that is ceded at a share percent, rounded to the minor unit.</summary>
    public static long CededAmount(long minor, decimal sharePercent) =>
        Money.MulDiv(minor, PercentToUnits(sharePercent), 100 * PercentScale);

    /// <summary>
    /// The slice of an amount ceded to one more treaty when `alreadyPercent` of the same amount is
    /// ceded to others. Slicing the cumulative share rounds the total once, so the cessions of one
    /// row never add up to more than the ceded total, and never to more than the row at 100 %
    /// (review H08): 50 % + 50 % of 0.01 is 0.01, not 0.02.
    /// </summary>
    public static long CededSlice(long minor, decimal alreadyPercent, decimal sharePercent)
    {
        var before = PercentToUnits(alreadyPercent);
        var after = before + PercentToUnits(sharePercent);
        return Money.MulDiv(minor, after, 100 * PercentScale) - Money.MulDiv(minor, before, 100 * PercentScale);
    }

    /// <summary>n / d rounded half away from zero on big integers — MulDiv's rounding.</summary>
    private static BigInteger DivRoundExact(BigInteger n, BigInteger d)
    {
        var neg = n < 0 != d < 0;
        var a = BigInteger.Abs(n);
        var b = BigInteger.Abs(d);
        var q = (a * 2 + b) / (b * 2);
        return neg ? -q : q;
    }

    /// <summary>A stored share percent (numeric(7, 4) text) in exact units of 1/10 000 percent: '33.3333' → 333333.</summary>
    public static BigInteger PercentUnitsExact(string text)
    {
        // /^(\d+)(?:\.(\d{1,4}))?$/ with ASCII digits.
        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? "" : text[(dot + 1)..];
        if (whole.Length == 0 || !whole.All(JsMath.IsDigit) || (dot >= 0 && (fraction.Length is < 1 or > 4 || !fraction.All(JsMath.IsDigit))))
        {
            throw new ArgumentException($"not a share percent: {text}");
        }
        return BigInteger.Parse(whole, System.Globalization.CultureInfo.InvariantCulture) * PercentScale
            + BigInteger.Parse(fraction.PadRight(4, '0'), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// CededSlice on big integers (MONTH_REPORT_CLAIM_RI_SHARES_DECIMAL_PRECISION_20261019): the slice of the cumulative share, so the
    /// slices of one amount are rounded once together (50 % + 50 % of 0.01 is 0.01). Percent in exact units.
    /// </summary>
    public static BigInteger CededSliceExact(BigInteger minor, BigInteger alreadyUnits, BigInteger shareUnits)
    {
        var whole = new BigInteger(100 * PercentScale);
        return DivRoundExact(minor * (alreadyUnits + shareUnits), whole) - DivRoundExact(minor * alreadyUnits, whole);
    }

    /// <summary>ReinsurerShareOfClaim on big integers: excess of loss what exceeds the priority up to the limit; proportional the share.</summary>
    public static BigInteger ReinsurerShareOfClaimExact(ClaimTreatyTermsExact terms, BigInteger shareUnits, BigInteger amountMinor)
    {
        if (amountMinor <= 0) return BigInteger.Zero;
        if (terms.Kind == TreatyKind.ExcessOfLoss)
        {
            var above = amountMinor - (terms.Priority ?? BigInteger.Zero);
            if (above <= 0) return BigInteger.Zero;
            return terms.Limit is { } limit && limit > 0 && limit < above ? limit : above;
        }
        return CededSliceExact(amountMinor, BigInteger.Zero, shareUnits);
    }

    /// <summary>
    /// Checks the treaties applied to one risk before anything is booked (review H07, H08):
    /// proportional shares together may not exceed 100 %; an excess-of-loss treaty may not be
    /// combined with proportional ones until the owner confirms whether it works on the gross or
    /// the retained loss; an excess-of-loss priority and limit can only be compared with a loss in
    /// the same currency until a conversion rule (rate date, rounding) is confirmed; and only one
    /// excess-of-loss treaty may share a loss until the owner confirms how layers stack (each
    /// computed on the gross loss, two of them could recover more than the loss).
    /// </summary>
    public static CessionProblem? CheckCessionPlan(IReadOnlyList<CessionShare> shares, string riskCurrency)
    {
        var xol = shares.Where(s => s.Kind == TreatyKind.ExcessOfLoss).ToList();
        if (xol.Any(s => s.Currency != riskCurrency)) return CessionProblem.CurrencyMismatch;
        if (xol.Count > 1) return CessionProblem.XolLayers;
        var proportional = shares.Where(s => s.Kind != TreatyKind.ExcessOfLoss && PercentToUnits(s.SharePercent) > 0).ToList();
        var total = proportional.Sum(s => PercentToUnits(s.SharePercent));
        if (total > 100 * PercentScale) return CessionProblem.OverCeded;
        if (xol.Count > 0 && proportional.Count > 0) return CessionProblem.XolWithProportional;
        return null;
    }

    /// <summary>
    /// The premium ledger row's mirror for the reinsurer: the same cover and booking dates, the
    /// ceded part of the amount. A cancellation's cession is derived from the cession of the row
    /// it cancels, so the pair earns exactly the ceded part of the retained premium.
    /// </summary>
    public static PremiumTransaction CessionOf(PremiumTransaction tx, decimal sharePercent)
    {
        if (tx.Cancels is not null)
        {
            var cession = EarnedPremium.CancellationOf(CessionOf(tx.Cancels, sharePercent), tx.CoverFrom)
                ?? throw new InvalidOperationException($"Cancellation on {ToIso(tx.CoverFrom)} is outside the cover it reverses");
            return cession with { BookedOn = tx.BookedOn };
        }
        return tx with { Amount = new Money(CededAmount(tx.Amount.Minor, sharePercent), tx.Amount.Currency) };
    }

    /// <summary>Reinsurance commission earned on a cession, deferred like the premium it belongs to.</summary>
    public static PremiumTransaction CommissionOf(PremiumTransaction cession, decimal commissionPercent) =>
        cession with { Amount = new Money(CededAmount(cession.Amount.Minor, commissionPercent), cession.Amount.Currency), Cancels = null };

    /// <summary>
    /// Reinsurer's share of a claim amount (reserve level or cumulative payments, minor units):
    /// proportional treaties take their share percent; excess of loss takes what exceeds the
    /// priority up to the limit.
    /// </summary>
    public static long ReinsurerShareOfClaim(ClaimTreatyTerms terms, decimal sharePercent, long amountMinor)
    {
        if (amountMinor <= 0) return 0;
        if (terms.Kind == TreatyKind.ExcessOfLoss)
        {
            var above = amountMinor - (terms.Priority ?? 0);
            if (above <= 0) return 0;
            return terms.Limit is { } limit && limit != 0 && limit > 0 ? Math.Min(above, limit) : above;
        }
        return CededAmount(amountMinor, sharePercent);
    }

    /// <summary>
    /// Premium of a treaty that is not tied to policies (an excess-of-loss or minimum/deposit
    /// premium) is a cost spread pro rata over the treaty period (order №02 annex 2 art. 4.2):
    /// the part that falls in a reporting period, both dates inclusive.
    /// </summary>
    public static long TreatyPremiumInPeriod(long premiumMinor, DateOnly treatyFrom, DateOnly treatyTo, DateOnly from, DateOnly to)
    {
        var tx = new PremiumTransaction(new Money(premiumMinor, Currency.GEL), treatyFrom, treatyFrom, AddDays(treatyTo, 1));
        var before = DayNumber(from) <= DayNumber(treatyFrom) ? 0 : EarnedPremium.EarnedAsOf(tx, AddDays(from, -1));
        return EarnedPremium.EarnedAsOf(tx, to) - before;
    }
}
