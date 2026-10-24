// Depth of customer due diligence for non-life insurance, from the client's calendar-year premium.
//
// FMS order №2/2020 (art. 4 p. 3 "გ", art. 5 p. 3¹ "გ", art. 7 p. 6¹ "ბ") and order №55/2022 art. 9:
// up to 1 000 GEL a year a reduced identification set is enough; up to 3 000 GEL (person) or
// 5 000 GEL (organization) identification is mandatory but verification — of the client and of
// its beneficial owners — may be skipped; above that, verification is mandatory. Beneficial
// owners of an organization must always be identified (order №55 art. 1 p. 3), and a deferred
// verification must finish within 30 calendar days of the relationship (art. 1 p. 4). Life
// insurance never benefits from the relief.
// Ported from src/compliance/kyc.ts.
using Insurance.Domain.Common;

namespace Insurance.Domain.Compliance;

/// <summary>Codes 'reduced', 'identification', 'verification'.</summary>
public enum KycTier { Reduced, Identification, Verification }

/// <summary>A beneficial owner currently on record.</summary>
public sealed record KycOwner(DateOnly? VerifiedOn, DateOnly? IdentifiedOn);

/// <param name="AnnualPremiumGel">Written premium in GEL on the client's non-life policies in the current calendar year.</param>
/// <param name="RelationshipFrom">Start of the business relationship: the first policy's effective date.</param>
/// <param name="Owners">Beneficial owners currently on record (organizations).</param>
/// <param name="SelfDeclarationOn">Written self-declaration of the ownership structure received (order №55 art. 7 p. 1).</param>
public sealed record KycInput(
    ClientKind Kind,
    decimal AnnualPremiumGel,
    bool HasLifePolicy,
    DateOnly? IdentifiedOn,
    DateOnly? VerifiedOn,
    DateOnly? RelationshipFrom,
    IReadOnlyList<KycOwner> Owners,
    DateOnly? SelfDeclarationOn);

public sealed record VerificationFreeMaxGel(decimal Person, decimal Organization)
{
    public decimal For(ClientKind kind) => kind == ClientKind.Person ? Person : Organization;
}

public sealed record KycThresholds(decimal ReducedMaxGel, VerificationFreeMaxGel VerificationFreeMaxGel, int OwnerVerificationDeferralDays);

/// <summary>Codes 'not_identified', 'client_verification_required', … (EnumCode.ToSnakeCode).</summary>
public enum KycIssue
{
    NotIdentified,
    ClientVerificationRequired,
    NoBeneficialOwner,
    NoSelfDeclaration,
    OwnerNotIdentified,
    OwnerVerificationRequired,
    OwnerVerificationOverdue,
}

/// <param name="NextThresholdGel">Premium above which the next tier starts, for the card.</param>
/// <param name="OwnerVerificationDueOn">Deadline of a deferred owner verification, when one is pending.</param>
public sealed record KycStatus(
    KycTier Tier,
    decimal? NextThresholdGel,
    bool ClientVerificationRequired,
    bool OwnerVerificationRequired,
    DateOnly? OwnerVerificationDueOn,
    IReadOnlyList<KycIssue> Issues);

public static class Kyc
{
    public static readonly KycThresholds KycThresholds = new(
        ReducedMaxGel: 1_000m,
        VerificationFreeMaxGel: new VerificationFreeMaxGel(Person: 3_000m, Organization: 5_000m),
        OwnerVerificationDeferralDays: 30);

    public static KycStatus KycStatus(KycInput input, DateOnly asOf, KycThresholds? thresholds = null)
    {
        thresholds ??= KycThresholds;
        var free = thresholds.VerificationFreeMaxGel.For(input.Kind);
        var premium = input.AnnualPremiumGel;
        var tier = input.HasLifePolicy || premium > free ? KycTier.Verification : premium > thresholds.ReducedMaxGel ? KycTier.Identification : KycTier.Reduced;
        decimal? nextThresholdGel = tier == KycTier.Reduced ? thresholds.ReducedMaxGel : tier == KycTier.Identification ? free : null;
        var issues = new List<KycIssue>();

        if (tier != KycTier.Reduced && input.IdentifiedOn is null) issues.Add(KycIssue.NotIdentified);
        var clientVerificationRequired = tier == KycTier.Verification;
        if (clientVerificationRequired && input.VerifiedOn is null) issues.Add(KycIssue.ClientVerificationRequired);

        var ownerVerificationRequired = false;
        DateOnly? ownerVerificationDueOn = null;
        if (input.Kind == ClientKind.Organization)
        {
            if (input.Owners.Count == 0) issues.Add(KycIssue.NoBeneficialOwner);
            if (input.SelfDeclarationOn is null && tier != KycTier.Reduced) issues.Add(KycIssue.NoSelfDeclaration);
            if (input.Owners.Any(o => o.IdentifiedOn is null)) issues.Add(KycIssue.OwnerNotIdentified);
            ownerVerificationRequired = tier == KycTier.Verification;
            if (ownerVerificationRequired && input.Owners.Any(o => o.VerifiedOn is null))
            {
                var due = DateMath.AddDays(input.RelationshipFrom ?? asOf, thresholds.OwnerVerificationDeferralDays);
                ownerVerificationDueOn = due;
                issues.Add(due < asOf ? KycIssue.OwnerVerificationOverdue : KycIssue.OwnerVerificationRequired);
            }
        }

        return new KycStatus(tier, nextThresholdGel, clientVerificationRequired, ownerVerificationRequired, ownerVerificationDueOn, issues);
    }
}
