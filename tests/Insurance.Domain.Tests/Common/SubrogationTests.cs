// The checks of src/lib/subrogation.ts that need neither the store nor the money rules, taken from the cases of
// src/lib/subrogation.test.ts that exercise them (the operations themselves are ported with the store).
using Insurance.Domain.Common;

namespace Insurance.Domain.Tests.Common;

public class SubrogationTests
{
    [Fact(DisplayName = "G3 / G4 / G5: a liable person carries no insurer details; an insurer may")]
    public void PartyDetails()
    {
        Assert.Null(Subrogation.PartyProblem(new NewParty("case-1", "person-1", PartyRole.LiablePerson, null, null, null)));
        Assert.Null(Subrogation.PartyProblem(new NewParty("case-1", "insurer-1", PartyRole.LiabilityInsurer, "MTPL-1", "TPL", 70_000)));
        Assert.Null(Subrogation.PartyProblem(new NewParty("case-1", "insurer-1", PartyRole.LiabilityInsurer, null, null, null)));
        Assert.Equal("kind", Subrogation.PartyProblem(new NewParty("case-1", "person-2", PartyRole.LiablePerson, "X", null, null)));
        Assert.Equal("kind", Subrogation.PartyProblem(new NewParty("case-1", "person-2", PartyRole.LiablePerson, null, "TPL", null)));
        Assert.Equal("kind", Subrogation.PartyProblem(new NewParty("case-1", "person-2", PartyRole.LiablePerson, null, null, 100)));
    }

    [Fact(DisplayName = "an insurer's limit, when given, is more than zero")]
    public void LimitPositive()
    {
        Assert.Equal("amount", Subrogation.PartyProblem(new NewParty("case-1", "insurer-1", PartyRole.LiabilityInsurer, null, null, 0)));
        Assert.Equal("amount", Subrogation.PartyProblem(new NewParty("case-1", "insurer-1", PartyRole.LiabilityInsurer, null, null, -1)));
        Assert.Null(Subrogation.PartyProblem(new NewParty("case-1", "insurer-1", PartyRole.LiabilityInsurer, null, null, 1)));
    }

    [Fact(DisplayName = "G13 / G14 / G16: a claim rests on a demand letter, a confirmation on an acceptance or a court, a cancellation on a court only")]
    public void Documents()
    {
        Assert.Equal([DocumentKind.DemandLetter], Subrogation.DocumentsFor(DocumentedMovement.Claimed));
        Assert.Equal([DocumentKind.DebtorAcceptance, DocumentKind.CourtDecision], Subrogation.DocumentsFor(DocumentedMovement.Confirmed));
        Assert.Equal([DocumentKind.CourtDecision], Subrogation.DocumentsFor(DocumentedMovement.Cancelled));
        Assert.Equal("document", Subrogation.DocumentProblem(DocumentedMovement.Claimed, DocumentKind.DebtorAcceptance));
        foreach (var kind in new[] { DocumentKind.Correction, DocumentKind.DebtorAcceptance }) Assert.Equal("document", Subrogation.DocumentProblem(DocumentedMovement.Cancelled, kind));
        Assert.Null(Subrogation.DocumentProblem(DocumentedMovement.Cancelled, DocumentKind.CourtDecision));
        Assert.Null(Subrogation.DocumentProblem(DocumentedMovement.Confirmed, DocumentKind.CourtDecision));
    }

    [Fact(DisplayName = "G2 / G27: a bank reference is compared trimmed; a blank one is none")]
    public void BankReference()
    {
        Assert.Equal("BANK-1", Subrogation.BankReference(" BANK-1 "));
        Assert.Null(Subrogation.BankReference("   "));
        Assert.Null(Subrogation.BankReference(null));
        Assert.True(Subrogation.IsBlank(" \u00a0"));
        Assert.False(Subrogation.IsBlank("ოქმი 77"));
    }

    [Fact(DisplayName = "the codes are the old ones")]
    public void Codes()
    {
        Assert.Equal(["police_report", "court_decision", "admission"], Enum.GetValues<BasisKind>().Select(x => x.ToSnakeCode()));
        Assert.Equal(["liable_person", "liability_insurer"], Enum.GetValues<PartyRole>().Select(x => x.ToSnakeCode()));
        Assert.Equal(["accepted", "disputed", "rejected"], Enum.GetValues<SubrogationResponse>().Select(x => x.ToSnakeCode()));
        Assert.Equal(["demand_letter", "debtor_acceptance", "court_decision", "bank_statement", "correction"], Enum.GetValues<DocumentKind>().Select(x => x.ToSnakeCode()));
    }
}
