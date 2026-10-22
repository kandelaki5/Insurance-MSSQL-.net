using Insurance.Domain.Finance;
using static Insurance.Domain.Tests.Finance.ParityJson;

namespace Insurance.Domain.Tests.Finance;

/// <summary>Parity with the old code: money, dates, earned premium, commission, reserves, deductible, plan, cancel preview.</summary>
public class ParityCoreTests
{
    private static readonly RateProvider Rates = ParityRates.Provider;

    private const string MoneyFile = "finance-money.json";
    private const string DatesFile = "finance-dates.json";
    private const string PremiumFile = "finance-earned-premium.json";

    [Fact] public void Money_Of() => Parity.Check(MoneyFile, "money", c => Money.Of(Dec(c.In["amount"]!), E<Currency>(c.In["currency"]!)), 300);
    [Fact] public void Money_MulDiv() => Parity.Check(MoneyFile, "mulDiv", c => Money.MulDiv(Long(c.In["minor"]!), Long(c.In["numerator"]!), Long(c.In["denominator"]!)), 400);
    [Fact] public void Money_ToGel() => Parity.Check(MoneyFile, "toGel", c => Money.ToGel(Read<Money>(c.In["amount"]), Date(c.In["date"]!), Rates), 300);
    [Fact] public void Money_FromGel() => Parity.Check(MoneyFile, "fromGel", c => Money.FromGel(Long(c.In["gelMinor"]!), E<Currency>(c.In["currency"]!), Date(c.In["date"]!), Rates), 300);

    [Fact] public void Dates_DayNumber() => Parity.Check(DatesFile, "dayNumber", c => Dates.DayNumber(Date(c.In["date"]!)), 300);
    [Fact] public void Dates_IsIsoDate() => Parity.Check(DatesFile, "isIsoDate", c => Dates.IsIsoDate(Str(c.In["value"]!)), 500);
    [Fact] public void Dates_IsPlausibleDate() => Parity.Check(DatesFile, "isPlausibleDate", c => Dates.IsPlausibleDate(Str(c.In["value"]!)), 500);
    [Fact] public void Dates_DaysBetween() => Parity.Check(DatesFile, "daysBetween", c => Dates.DaysBetween(Date(c.In["from"]!), Date(c.In["to"]!)), 300);
    [Fact] public void Dates_AddDays() => Parity.Check(DatesFile, "addDays", c => Dates.AddDays(Date(c.In["date"]!), (int)Long(c.In["days"]!)), 300);
    [Fact] public void Dates_AddMonths() => Parity.Check(DatesFile, "addMonths", c => Dates.AddMonths(Date(c.In["date"]!), (int)Long(c.In["months"]!)), 2000);

    private static PremiumTransaction TxOf(ParityCase c, string key = "tx") => Read<PremiumTransaction>(c.In[key]);
    private static List<PremiumTransaction> TxsOf(ParityCase c, string key = "txs") => Read<List<PremiumTransaction>>(c.In[key]);

    [Fact] public void EarnedPremium_EarnedAsOf() => Parity.Check(PremiumFile, "earnedAsOf", c => EarnedPremium.EarnedAsOf(TxOf(c), Date(c.In["asOf"]!)), 400);
    [Fact] public void EarnedPremium_EarnedAsOfExact() => Parity.Check(PremiumFile, "earnedAsOfExact", c => EarnedPremium.EarnedAsOfExact(Read<ExactPremiumTx>(c.In["tx"]), Date(c.In["asOf"]!)), 400);
    [Fact] public void EarnedPremium_CancellationOf() => Parity.Check(PremiumFile, "cancellationOf", c => EarnedPremium.CancellationOf(TxOf(c), Date(c.In["cancelOn"]!)), 300);
    [Fact] public void EarnedPremium_ReversalOf() => Parity.Check(PremiumFile, "reversalOf", c => EarnedPremium.ReversalOf(TxOf(c), Date(c.In["bookedOn"]!)), 100);
    [Fact] public void EarnedPremium_PremiumPosition() => Parity.Check(PremiumFile, "premiumPosition", c => EarnedPremium.PremiumPositionOf(TxsOf(c), Date(c.In["asOf"]!)), 300);
    [Fact] public void EarnedPremium_EarnedInPeriod() => Parity.Check(PremiumFile, "earnedInPeriod", c => EarnedPremium.EarnedInPeriod(TxsOf(c), Date(c.In["from"]!), Date(c.In["to"]!)), 300);
    [Fact] public void EarnedPremium_InGel() => Parity.Check(PremiumFile, "inGel", c => EarnedPremium.InGel(TxOf(c), Rates), 300);
    [Fact] public void EarnedPremium_PremiumPositionGel() => Parity.Check(PremiumFile, "premiumPositionGel", c => EarnedPremium.PremiumPositionGel(TxsOf(c), Date(c.In["asOf"]!), Rates), 200);
    [Fact] public void EarnedPremium_LinkLedger() => Parity.Check(PremiumFile, "linkLedger", c => EarnedPremium.LinkLedger(Read<List<LedgerRow>>(c.In["rows"])), 100);

    [Fact] public void Commission_CommissionTransaction() => Parity.Check("finance-commission.json", "commissionTransaction", c => Commission.CommissionTransaction(TxOf(c), Dec(c.In["percent"]!)), 300);
    [Fact] public void Commission_CommissionPayable() => Parity.Check("finance-commission.json", "commissionPayable", c => Commission.CommissionPayable(Long(c.In["commissionMinor"]!), Long(c.In["premiumMinor"]!), Long(c.In["paidMinor"]!)), 300);

    [Fact] public void Reserves_StatutoryIbnr() => Parity.Check("finance-reserves.json", "statutoryIbnr", c => Reserves.StatutoryIbnr(Long(c.In["grossWrittenGel"]!), Long(c.In["cededGel"]!), E<IbnrLine>(c.In["line"]!)), 600);
    [Fact] public void Reserves_StatutoryIbnrExact() => Parity.Check("finance-reserves.json", "statutoryIbnrExact", c => Reserves.StatutoryIbnrExact(Big(c.In["grossWrittenGel"]!), Big(c.In["cededGel"]!), E<IbnrLine>(c.In["line"]!)), 400);

    [Fact] public void Deductible_DeductibleToWithhold() => Parity.Check("finance-deductible.json", "deductibleToWithhold", c => Deductible.DeductibleToWithhold(Long(c.In["agreedMinor"]!), Long(c.In["alreadyWithheldMinor"]!), Long(c.In["grossMinor"]!)), 400);
    [Fact] public void Deductible_ClassDeductible() => Parity.Check("finance-deductible.json", "classDeductible", c => Deductible.ClassDeductible(Read<List<long>>(c.In["agreedMinor"])), 100);

    [Fact] public void Plan_IsPaymentPlan() => Parity.Check("finance-plan.json", "isPaymentPlan", c => Plan.IsPaymentPlan((int)Long(c.In["value"]!)), 16);
    [Fact] public void Plan_FitsPlan() => Parity.Check("finance-plan.json", "fitsPlan", c => Plan.FitsPlan((int)Long(c.In["plan"]!), Date(c.In["from"]!), Date(c.In["to"]!)), 200);
    [Fact] public void Plan_PlanInstallments() => Parity.Check("finance-plan.json", "planInstallments", c => Plan.PlanInstallments((int)Long(c.In["plan"]!), Date(c.In["from"]!), Date(c.In["to"]!), Long(c.In["totalMinor"]!)), 400);

    [Fact]
    public void CancelPreview_CancellationPreview() => Parity.Check("finance-cancel-preview.json", "cancellationPreview",
        c => CancelPreview.CancellationPreview(TxsOf(c, "rows"), Date(c.In["on"]!), Long(c.In["paidMinor"]!), Read<DateRange>(c.In["cover"])), 300);
}
