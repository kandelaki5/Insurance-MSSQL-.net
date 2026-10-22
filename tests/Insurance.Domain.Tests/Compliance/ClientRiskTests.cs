// Ported from src/compliance/client-risk.test.ts ("client risk assessment").
using Insurance.Domain.Compliance;

namespace Insurance.Domain.Tests.Compliance;

public class ClientRiskTests
{
    private static readonly ClientFacts Base = new(
        Kind: ClientKind.Person,
        IsResident: true,
        CountryCode: "GE",
        IsPep: false,
        IsPublicEntity: false,
        StateSharePercent: null,
        VerifiedOn: new DateOnly(2026, 1, 10),
        SanctionsCheckedOn: new DateOnly(2026, 1, 10),
        AnnualPremiumGel: 1_200m,
        PreviousYearPremiumGel: 1_000m);

    private static readonly DateOnly AsOf = new(2026, 10, 3);

    private static List<FactorCode> Codes(RiskAssessment r) => r.Factors.Select(f => f.Code).ToList();

    [Fact(DisplayName = "a verified, screened resident with ordinary premium is low risk and reviewed in 4 years")]
    public void VerifiedScreenedResidentIsLow()
    {
        var r = ClientRisk.AssessClientRisk(Base, AsOf);
        Assert.Equal(RiskLevel.Low, r.Level);
        Assert.Equal(0, r.Score);
        Assert.Empty(r.Factors);
        Assert.Equal(new DateOnly(2030, 10, 3), r.ReviewDueOn);
    }

    [Fact(DisplayName = "a politically exposed person is high risk whatever else is known, reviewed yearly")]
    public void PepIsHigh()
    {
        var r = ClientRisk.AssessClientRisk(Base with { IsPep = true }, AsOf);
        Assert.Equal(RiskLevel.High, r.Level);
        Assert.Contains(r.Factors, f => f.Code == FactorCode.Pep && f.Decisive);
        Assert.Equal(new DateOnly(2027, 10, 3), r.ReviewDueOn);
    }

    [Fact(DisplayName = "a high-risk jurisdiction is decisive; other non-residents only add points")]
    public void HighRiskJurisdictionIsDecisive()
    {
        var policy = ClientRisk.DefaultRiskPolicy with { HighRiskCountries = ["IR"] };
        Assert.Equal(RiskLevel.High, ClientRisk.AssessClientRisk(Base with { IsResident = false, CountryCode = "ir" }, AsOf, policy).Level);
        var other = ClientRisk.AssessClientRisk(Base with { IsResident = false, CountryCode = "DE" }, AsOf, policy);
        Assert.Equal(RiskLevel.Medium, other.Level);
        Assert.Equal([FactorCode.NonResident], Codes(other));
    }

    [Fact(DisplayName = "incomplete due diligence raises the level: no sanctions check and no verification")]
    public void IncompleteDueDiligence()
    {
        var r = ClientRisk.AssessClientRisk(Base with { VerifiedOn = null, SanctionsCheckedOn = null }, AsOf);
        Assert.Equal([FactorCode.NotVerified, FactorCode.SanctionsNotChecked], Codes(r));
        Assert.Equal(3, r.Score);
        Assert.Equal(RiskLevel.Medium, r.Level);
    }

    [Fact(DisplayName = "a sanctions check older than a year is stale")]
    public void StaleSanctionsCheck()
    {
        var r = ClientRisk.AssessClientRisk(Base with { SanctionsCheckedOn = new DateOnly(2025, 9, 1) }, AsOf);
        Assert.Equal([FactorCode.SanctionsCheckStale], Codes(r));
    }

    [Fact(DisplayName = "premium volume adds points and a threefold jump adds one more")]
    public void PremiumVolume()
    {
        Assert.Equal([FactorCode.PremiumHigh], Codes(ClientRisk.AssessClientRisk(Base with { AnnualPremiumGel = 20_000m, PreviousYearPremiumGel = 18_000m }, AsOf)));
        var jump = ClientRisk.AssessClientRisk(Base with { AnnualPremiumGel = 60_000m, PreviousYearPremiumGel = 10_000m }, AsOf);
        Assert.Equal([FactorCode.PremiumVeryHigh, FactorCode.PremiumJump], Codes(jump));
        Assert.Equal(RiskLevel.Medium, jump.Level);
    }

    [Fact(DisplayName = "several factors together reach high without any decisive one")]
    public void SeveralFactorsReachHigh()
    {
        var r = ClientRisk.AssessClientRisk(Base with { IsResident = false, CountryCode = "DE", SanctionsCheckedOn = null, AnnualPremiumGel = 60_000m, PreviousYearPremiumGel = 55_000m }, AsOf);
        Assert.Equal(6, r.Score);
        Assert.Equal(RiskLevel.High, r.Level);
    }

    [Fact(DisplayName = "a state entity lowers the score")]
    public void StateEntityLowersScore()
    {
        var r = ClientRisk.AssessClientRisk(Base with { Kind = ClientKind.Organization, IsPublicEntity = true, SanctionsCheckedOn = null }, AsOf);
        Assert.Equal(0, r.Score);
        Assert.Equal(RiskLevel.Low, r.Level);
    }

    [Fact(DisplayName = "a manual level keeps the same review interval rule")]
    public void ManualLevelReview()
    {
        Assert.Equal(new DateOnly(2028, 1, 31), ClientRisk.ReviewDueFor(RiskLevel.Medium, new DateOnly(2026, 1, 31)));
    }

    [Fact(DisplayName = "settings override thresholds and the country list; blanks keep defaults")]
    public void SettingsOverride()
    {
        var p = ClientRisk.RiskPolicyFromSettings(new Dictionary<string, string> { ["aml.high_risk_countries"] = "ir, kp;af", ["aml.review_months_high"] = "6", ["aml.high_score"] = "" });
        Assert.Equal(["IR", "KP", "AF"], p.HighRiskCountries);
        Assert.Equal(6m, p.ReviewMonths.High);
        Assert.Equal(ClientRisk.DefaultRiskPolicy.HighScore, p.HighScore);
    }
}
