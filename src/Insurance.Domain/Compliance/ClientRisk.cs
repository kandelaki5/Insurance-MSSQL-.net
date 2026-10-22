// Explainable client (AML) risk assessment.
//
// The AML law (art. 8 p. 2, art. 18, art. 21, art. 24) requires a risk level for every client
// before a policy is issued, built from risk factors (client, activity, jurisdiction, product,
// channel, transaction), re-assessed periodically and on material change, with the result and
// its reasoning kept. Orders №54/№55 use the scale high / medium / low. The insurance
// supervisor's recommendation adds that the level must be explainable and manually
// correctable with a written reason, and that the review frequency follows the level.
//
// This module is pure: it turns known facts about a client into a level plus the list of
// factors that produced it. Thresholds live in `RiskPolicy` so an insurer can tune them.
// Ported from src/compliance/client-risk.ts.
using System.Text.RegularExpressions;
using Insurance.Domain.Common;

namespace Insurance.Domain.Compliance;

/// <summary>Codes 'low', 'medium', 'high' (EnumCode.ToSnakeCode).</summary>
public enum RiskLevel { Low, Medium, High }

/// <summary>Codes 'person', 'organization'.</summary>
public enum ClientKind { Person, Organization }

public sealed record ClientFacts(
    ClientKind Kind,
    bool IsResident,
    string CountryCode,
    bool IsPep,
    bool IsPublicEntity,
    decimal? StateSharePercent,
    DateOnly? VerifiedOn,
    DateOnly? SanctionsCheckedOn,
    // Written premium in GEL on the client's policies in the current calendar year.
    decimal AnnualPremiumGel,
    // Written premium in GEL in the previous calendar year, to notice a sharp increase.
    decimal PreviousYearPremiumGel);

/// <summary>Review interval by level, in months.</summary>
public sealed record ReviewMonths(decimal High, decimal Medium, decimal Low)
{
    public decimal For(RiskLevel level) => level switch
    {
        RiskLevel.High => High,
        RiskLevel.Medium => Medium,
        _ => Low,
    };
}

public sealed record RiskPolicy(
    // ISO country codes treated as high-risk jurisdictions (law art. 19).
    IReadOnlyList<string> HighRiskCountries,
    // Annual premium above which a client gets one point, and above which two.
    decimal PremiumPointOneGel,
    decimal PremiumPointTwoGel,
    // Days after which a sanctions check counts as stale.
    decimal SanctionsCheckMaxAgeDays,
    // Score thresholds: at or above `High` → high, at or above `Medium` → medium.
    decimal MediumScore,
    decimal HighScore,
    ReviewMonths ReviewMonths);

/// <summary>Codes 'pep', 'high_risk_country', … (EnumCode.ToSnakeCode).</summary>
public enum FactorCode
{
    Pep,
    HighRiskCountry,
    NonResident,
    NotVerified,
    SanctionsNotChecked,
    SanctionsCheckStale,
    PremiumHigh,
    PremiumVeryHigh,
    PremiumJump,
    StateEntity,
}

/// <param name="Decisive">True when the factor alone sets the level to high, whatever the score.</param>
public sealed record RiskFactor(FactorCode Code, int Points, bool Decisive);

public sealed record RiskAssessment(RiskLevel Level, int Score, IReadOnlyList<RiskFactor> Factors, DateOnly ReviewDueOn);

public static partial class ClientRisk
{
    public static readonly RiskPolicy DefaultRiskPolicy = new(
        HighRiskCountries: [],
        PremiumPointOneGel: 15_000m,
        PremiumPointTwoGel: 50_000m,
        SanctionsCheckMaxAgeDays: 365m,
        MediumScore: 2m,
        HighScore: 5m,
        // Supervisor's recommendation: high — yearly; medium — every 2–3 years; low — every 4–5 years.
        ReviewMonths: new ReviewMonths(High: 12m, Medium: 24m, Low: 48m));

    public static RiskAssessment AssessClientRisk(ClientFacts facts, DateOnly asOf, RiskPolicy? policy = null)
    {
        policy ??= DefaultRiskPolicy;
        var factors = new List<RiskFactor>();
        void Add(FactorCode code, int points, bool decisive = false) => factors.Add(new RiskFactor(code, points, decisive));

        if (facts.IsPep) Add(FactorCode.Pep, 3, true);
        if (policy.HighRiskCountries.Contains(JsText.ToUpperCase(facts.CountryCode), StringComparer.Ordinal)) Add(FactorCode.HighRiskCountry, 3, true);
        else if (!facts.IsResident) Add(FactorCode.NonResident, 2);

        if (facts.VerifiedOn is null) Add(FactorCode.NotVerified, 1);
        if (facts.SanctionsCheckedOn is not { } checkedOn) Add(FactorCode.SanctionsNotChecked, 2);
        else if (asOf.DayNumber - checkedOn.DayNumber > policy.SanctionsCheckMaxAgeDays) Add(FactorCode.SanctionsCheckStale, 1);

        if (facts.AnnualPremiumGel >= policy.PremiumPointTwoGel) Add(FactorCode.PremiumVeryHigh, 2);
        else if (facts.AnnualPremiumGel >= policy.PremiumPointOneGel) Add(FactorCode.PremiumHigh, 1);
        if (facts.PreviousYearPremiumGel > 0 && facts.AnnualPremiumGel >= 3 * facts.PreviousYearPremiumGel && facts.AnnualPremiumGel >= policy.PremiumPointOneGel)
        {
            Add(FactorCode.PremiumJump, 1);
        }

        var isState = facts.Kind == ClientKind.Organization && (facts.IsPublicEntity || (facts.StateSharePercent ?? 0) >= 50);
        if (isState) Add(FactorCode.StateEntity, -2);

        var score = factors.Sum(f => f.Points);
        var level = factors.Any(f => f.Decisive) || score >= policy.HighScore
            ? RiskLevel.High
            : score >= policy.MediumScore ? RiskLevel.Medium : RiskLevel.Low;

        return new RiskAssessment(level, score, factors, AddMonths(asOf, policy.ReviewMonths.For(level)));
    }

    /// <summary>Review date for a level chosen by hand: the same interval rule applies.</summary>
    public static DateOnly ReviewDueFor(RiskLevel level, DateOnly assessedOn, RiskPolicy? policy = null) =>
        AddMonths(assessedOn, (policy ?? DefaultRiskPolicy).ReviewMonths.For(level));

    [GeneratedRegex("[" + JsText.WhiteSpaceClass + ",;]+", RegexOptions.CultureInvariant)]
    private static partial Regex CountrySeparators();

    [GeneratedRegex(@"^[A-Z]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CountryCode();

    /// <summary>Reads the policy overrides stored as settings (`aml.*` keys); missing keys keep defaults.</summary>
    public static RiskPolicy RiskPolicyFromSettings(IReadOnlyDictionary<string, string> settings)
    {
        // A setting counts when it is a finite number as JavaScript's Number() reads it; an empty text keeps the default.
        decimal Number(string key, decimal fallback) =>
            settings.TryGetValue(key, out var text) && text != "" && JsNumber.ParseFiniteDecimal(text) is { } value ? value : fallback;
        var d = DefaultRiskPolicy;
        return new RiskPolicy(
            HighRiskCountries: CountrySeparators().Split(settings.TryGetValue("aml.high_risk_countries", out var list) ? list : "")
                .Select(c => JsText.ToUpperCase(JsText.Trim(c)))
                .Where(c => CountryCode().IsMatch(c))
                .ToList(),
            PremiumPointOneGel: Number("aml.premium_point_one_gel", d.PremiumPointOneGel),
            PremiumPointTwoGel: Number("aml.premium_point_two_gel", d.PremiumPointTwoGel),
            SanctionsCheckMaxAgeDays: Number("aml.sanctions_check_max_age_days", d.SanctionsCheckMaxAgeDays),
            MediumScore: Number("aml.medium_score", d.MediumScore),
            HighScore: Number("aml.high_score", d.HighScore),
            ReviewMonths: new ReviewMonths(
                High: Number("aml.review_months_high", d.ReviewMonths.High),
                Medium: Number("aml.review_months_medium", d.ReviewMonths.Medium),
                Low: Number("aml.review_months_low", d.ReviewMonths.Low)));
    }

    /// <summary>
    /// The old addMonths with a month count read from settings, which may be fractional: JavaScript's Date.UTC drops the
    /// fraction of (month index + months), toward zero, before it clamps the day to the target month.
    /// </summary>
    private static DateOnly AddMonths(DateOnly date, decimal months)
    {
        var index = decimal.Truncate(date.Month - 1 + months);
        return DateMath.AddMonths(date, checked((int)(index - (date.Month - 1))));
    }
}
