// Ported from src/compliance/kyc.test.ts ("KYC depth by annual premium").
using Insurance.Domain.Compliance;

namespace Insurance.Domain.Tests.Compliance;

public class KycTests
{
    private static readonly DateOnly Sep1 = new(2026, 9, 1);

    private static readonly KycInput Person = new(
        Kind: ClientKind.Person,
        AnnualPremiumGel: 800m,
        HasLifePolicy: false,
        IdentifiedOn: null,
        VerifiedOn: null,
        RelationshipFrom: Sep1,
        Owners: [],
        SelfDeclarationOn: null);

    private static readonly KycInput Org = Person with
    {
        Kind = ClientKind.Organization,
        AnnualPremiumGel = 4_000m,
        IdentifiedOn = Sep1,
        SelfDeclarationOn = Sep1,
        Owners = [new KycOwner(VerifiedOn: null, IdentifiedOn: Sep1)],
    };

    private static readonly DateOnly AsOf = new(2026, 10, 3);

    [Fact(DisplayName = "up to 1 000 GEL a person needs only the reduced set")]
    public void ReducedSet()
    {
        var s = Kyc.KycStatus(Person, AsOf);
        Assert.Equal(KycTier.Reduced, s.Tier);
        Assert.Empty(s.Issues);
        Assert.Equal(1_000m, s.NextThresholdGel);
    }

    [Fact(DisplayName = "between 1 000 and 3 000 GEL a person must be identified, verification is optional")]
    public void IdentificationTier()
    {
        var s = Kyc.KycStatus(Person with { AnnualPremiumGel = 2_500m }, AsOf);
        Assert.Equal(KycTier.Identification, s.Tier);
        Assert.Equal([KycIssue.NotIdentified], s.Issues);
        Assert.Empty(Kyc.KycStatus(Person with { AnnualPremiumGel = 2_500m, IdentifiedOn = Sep1 }, AsOf).Issues);
    }

    [Fact(DisplayName = "above 3 000 GEL a person must be verified; a life policy requires it at any premium")]
    public void VerificationTier()
    {
        Assert.Equal([KycIssue.ClientVerificationRequired], Kyc.KycStatus(Person with { AnnualPremiumGel = 3_000.01m, IdentifiedOn = Sep1 }, AsOf).Issues);
        Assert.Equal(KycTier.Verification, Kyc.KycStatus(Person with { HasLifePolicy = true }, AsOf).Tier);
    }

    [Fact(DisplayName = "an organization up to 5 000 GEL needs identified owners and a self-declaration, not verification")]
    public void OrganizationIdentification()
    {
        var s = Kyc.KycStatus(Org, AsOf);
        Assert.Equal(KycTier.Identification, s.Tier);
        Assert.Empty(s.Issues);
        Assert.Equal([KycIssue.NoBeneficialOwner], Kyc.KycStatus(Org with { Owners = [] }, AsOf).Issues);
        Assert.Equal([KycIssue.NoSelfDeclaration], Kyc.KycStatus(Org with { SelfDeclarationOn = null }, AsOf).Issues);
    }

    [Fact(DisplayName = "above 5 000 GEL owner verification is due 30 days after the relationship started")]
    public void OwnerVerificationDue()
    {
        var s = Kyc.KycStatus(Org with { AnnualPremiumGel = 6_000m, VerifiedOn = Sep1 }, new DateOnly(2026, 9, 20));
        Assert.Equal(new DateOnly(2026, 10, 1), s.OwnerVerificationDueOn);
        Assert.Equal([KycIssue.OwnerVerificationRequired], s.Issues);
        Assert.Equal([KycIssue.OwnerVerificationOverdue], Kyc.KycStatus(Org with { AnnualPremiumGel = 6_000m, VerifiedOn = Sep1 }, AsOf).Issues);
        Assert.Empty(Kyc.KycStatus(Org with { AnnualPremiumGel = 6_000m, VerifiedOn = Sep1, Owners = [new KycOwner(VerifiedOn: new DateOnly(2026, 9, 15), IdentifiedOn: Sep1)] }, AsOf).Issues);
    }
}
