using Insurance.Domain.Finance;
using static Insurance.Domain.Tests.Finance.ParityJson;

namespace Insurance.Domain.Tests.Finance;

/// <summary>Parity with the old code: reinsurance, regress (subrogation) and the regress report.</summary>
public class ParityReinsuranceSubrogationTests
{
    private const string Ri = "finance-reinsurance.json";

    [Fact] public void Reinsurance_PercentToUnits() => Parity.Check(Ri, "percentToUnits", c => Reinsurance.PercentToUnits(Dec(c.In["percent"]!)), 200);
    [Fact] public void Reinsurance_CededPercentOf() => Parity.Check(Ri, "cededPercentOf", c => Reinsurance.CededPercentOf(Read<TreatyTerms>(c.In["terms"]), c.In["sumInsuredMinor"] is { } s ? Long(s) : null), 1200);
    [Fact] public void Reinsurance_CededAmount() => Parity.Check(Ri, "cededAmount", c => Reinsurance.CededAmount(Long(c.In["minor"]!), Dec(c.In["sharePercent"]!)), 300);
    [Fact] public void Reinsurance_CededSlice() => Parity.Check(Ri, "cededSlice", c => Reinsurance.CededSlice(Long(c.In["minor"]!), Dec(c.In["alreadyPercent"]!), Dec(c.In["sharePercent"]!)), 300);
    [Fact] public void Reinsurance_PercentUnitsExact() => Parity.Check(Ri, "percentUnitsExact", c => Reinsurance.PercentUnitsExact(Str(c.In["text"]!)), 19);
    [Fact] public void Reinsurance_CededSliceExact() => Parity.Check(Ri, "cededSliceExact", c => Reinsurance.CededSliceExact(Big(c.In["minor"]!), Big(c.In["alreadyUnits"]!), Big(c.In["shareUnits"]!)), 300);
    [Fact] public void Reinsurance_ReinsurerShareOfClaim() => Parity.Check(Ri, "reinsurerShareOfClaim", c => Reinsurance.ReinsurerShareOfClaim(Read<ClaimTreatyTerms>(c.In["terms"]), Dec(c.In["sharePercent"]!), Long(c.In["amountMinor"]!)), 300);
    [Fact] public void Reinsurance_ReinsurerShareOfClaimExact() => Parity.Check(Ri, "reinsurerShareOfClaimExact", c => Reinsurance.ReinsurerShareOfClaimExact(Read<ClaimTreatyTermsExact>(c.In["terms"]), Big(c.In["shareUnits"]!), Big(c.In["amountMinor"]!)), 300);
    [Fact] public void Reinsurance_CheckCessionPlan() => Parity.Check(Ri, "checkCessionPlan", c => Reinsurance.CheckCessionPlan(Read<List<CessionShare>>(c.In["shares"]), Str(c.In["riskCurrency"]!)), 300);
    [Fact] public void Reinsurance_CessionOf() => Parity.Check(Ri, "cessionOf", c => Reinsurance.CessionOf(Read<PremiumTransaction>(c.In["tx"]), Dec(c.In["sharePercent"]!)), 300);
    [Fact] public void Reinsurance_CommissionOf() => Parity.Check(Ri, "commissionOf", c => Reinsurance.CommissionOf(Read<PremiumTransaction>(c.In["cession"]), Dec(c.In["commissionPercent"]!)), 250);

    [Fact]
    public void Reinsurance_TreatyPremiumInPeriod() => Parity.Check(Ri, "treatyPremiumInPeriod", c =>
        Reinsurance.TreatyPremiumInPeriod(Long(c.In["premiumMinor"]!), Date(c.In["treatyFrom"]!), Date(c.In["treatyTo"]!), Date(c.In["from"]!), Date(c.In["to"]!)), 300);

    private const string Sub = "finance-subrogation.json";

    private static List<Movement> Movements(ParityCase c) => Read<List<Movement>>(c.In["movements"]);

    [Fact] public void Subrogation_ToTetri() => Parity.Check(Sub, "toTetri", c => Subrogation.ToTetri(Str(c.In["text"]!)), 150);
    [Fact] public void Subrogation_FromTetri() => Parity.Check(Sub, "fromTetri", c => Subrogation.FromTetri(Big(c.In["tetri"]!)), 150);
    [Fact] public void Subrogation_Totals() => Parity.Check(Sub, "totals", c => Subrogation.Totals(Movements(c), c.In["asOf"] is { } d ? Date(d) : null), 400);
    [Fact] public void Subrogation_Reimbursed() => Parity.Check(Sub, "reimbursed", c => Subrogation.Reimbursed(Read<List<ClaimPayment>>(c.In["payments"]), Date(c.In["asOf"]!)), 100);
    [Fact] public void Subrogation_Room() => Parity.Check(Sub, "room", c => Subrogation.Room(Read<ClaimMoney>(c.In["claim"]), Date(c.In["asOf"]!)), 100);
    [Fact] public void Subrogation_Ineligibility() => Parity.Check(Sub, "ineligibility", c => Subrogation.Ineligibility(Read<ClaimFacts>(c.In["claim"])), 10);
    [Fact] public void Subrogation_AsMovement() => Parity.Check(Sub, "asMovement", c => Subrogation.AsMovement(Str(c.In["id"]!), Read<NewMovement>(c.In["next"]), Movements(c)), 100);
    [Fact] public void Subrogation_CheckMovement() => Parity.Check(Sub, "checkMovement", c => Subrogation.CheckMovement(Movements(c), Read<NewMovement>(c.In["next"]), Read<ClaimMoney>(c.In["claim"])), 1000);
    [Fact] public void Subrogation_StatusAfterMoney() => Parity.Check(Sub, "statusAfterMoney", c => Subrogation.StatusAfterMoney(E<CaseStatus>(c.In["current"]!), Read<CaseTotals>(c.In["totals"])), 900);
    [Fact] public void Subrogation_ExpectedShares() => Parity.Check(Sub, "expectedShares", c => Subrogation.ExpectedShares(Big(c.In["confirmed"]!), Read<InsurerShare?>(c.In["insurer"])), 200);

    private const string Rep = "finance-subrogation-report.json";

    [Fact] public void SubrogationReport_TbilisiDay() => Parity.Check(Rep, "tbilisiDay", c => SubrogationReport.TbilisiDay(Str(c.In["at"]!)), 200);
    [Fact] public void SubrogationReport_TodayInTbilisi() => Parity.Check(Rep, "todayInTbilisi", c => SubrogationReport.TodayInTbilisi(DateTimeOffset.Parse(Str(c.In["now"]!), System.Globalization.CultureInfo.InvariantCulture)), 40);
    [Fact] public void SubrogationReport_OnDate() => Parity.Check(Rep, "onDate", c => SubrogationReport.OnDate(Read<List<Recorded<CaseStatus>>?>(c.In["history"]), Date(c.In["asOf"]!)), 200);
    [Fact] public void SubrogationReport_StatusOn() => Parity.Check(Rep, "statusOn", c => SubrogationReport.StatusOn(Read<List<Recorded<CaseStatus>>?>(c.In["history"]), Movements(c), Date(c.In["asOf"]!), Date(c.In["openedOn"]!)), 200);
    [Fact] public void SubrogationReport_TermsOn() => Parity.Check(Rep, "termsOn", c => SubrogationReport.TermsOn(Read<List<Recorded<PartyTerms>>?>(c.In["history"]), Date(c.In["asOf"]!)), 400);
    [Fact] public void SubrogationReport_InShowOrder() => Parity.Check(Rep, "inShowOrder", c => SubrogationReport.InShowOrder(Read<List<MovementRecord>>(c.In["movements"]), Date(c.In["asOf"]!)), 250);

    [Fact]
    public void SubrogationReport_CaseCard() => Parity.Check(Rep, "caseCard", c => SubrogationReport.CaseCardOf(
        Read<CaseRecord>(c.In["c"]), Read<List<PartyRecord>>(c.In["parties"]), Read<List<MovementRecord>>(c.In["movements"]), Date(c.In["asOf"]!), Str(c.In["today"]!)), 250);

    [Fact] public void SubrogationReport_ReportOnDate() => Parity.Check(Rep, "reportOnDate", c => SubrogationReport.ReportOnDate(Read<List<ReportCase>>(c.In["cases"]), Date(c.In["asOf"]!), Read<ReportOptions>(c.In["options"])), 150);
    [Fact] public void SubrogationReport_PeriodOf() => Parity.Check(Rep, "periodOf", c => SubrogationReport.PeriodOf(E<PeriodKind>(c.In["kind"]!), Str(c.In["day"]!)), 300);
    [Fact] public void SubrogationReport_CheckPeriod() => Parity.Check(Rep, "checkPeriod", c => SubrogationReport.CheckPeriod(Str(c.In["from"]!), Str(c.In["to"]!)), 150);
    [Fact] public void SubrogationReport_MovedIn() => Parity.Check(Rep, "movedIn", c => SubrogationReport.MovedIn(Movements(c), Date(c.In["from"]!), Date(c.In["to"]!)), 250);
}
