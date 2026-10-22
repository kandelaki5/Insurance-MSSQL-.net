using Insurance.Domain.Finance;
using static Insurance.Domain.Tests.Finance.ParityJson;

namespace Insurance.Domain.Tests.Finance;

/// <summary>Parity with the old code: receivable, payment allocation, mirror order, schedule changes, total loss, impairment, payment authorisation.</summary>
public class ParityReceivableTests
{
    private static readonly RateProvider Rates = ParityRates.Provider;
    private const string File = "finance-receivable.json";

    private static List<Installment> Installments(ParityCase c) => Read<List<Installment>>(c.In["installments"]);

    [Fact] public void Receivable_PaymentCredit() => Parity.Check(File, "paymentCredit", c => Receivable.PaymentCredit(Long(c.In["gelMinor"]!), E<Currency>(c.In["policyCurrency"]!), Date(c.In["paidOn"]!), Rates), 200);
    [Fact] public void Receivable_ReceivableAsOf() => Parity.Check(File, "receivableAsOf", c => Receivable.ReceivableAsOf(Installments(c), Read<List<Credit>>(c.In["credits"]), Date(c.In["asOf"]!), Rates), 300);

    [Fact]
    public void Receivable_CancellableFrom() => Parity.Check(File, "cancellableFrom", c =>
        Receivable.CancellableFrom(Receivable.ReceivableAsOf(Installments(c), Read<List<Credit>>(c.In["credits"]), Date(c.In["asOf"]!), Rates), c.In["graceDays"] is { } g ? (int)Long(g) : null), 300);

    [Fact] public void Receivable_OffsetClaim() => Parity.Check(File, "offsetClaim", c => Receivable.OffsetClaim(Read<Money>(c.In["claimAmount"]), Read<Money>(c.In["outstandingPremium"]), Date(c.In["on"]!)), 200);

    [Fact]
    public void Receivable_AllocatePayment() => Parity.Check(File, "allocatePayment", c =>
        Receivable.AllocatePayment(Long(c.In["gelMinor"]!), Date(c.In["paidOn"]!), E<Currency>(c.In["currency"]!), Read<List<InstallmentBalance>>(c.In["balances"]), Rates), 250);

    private static MirrorOrder Mirror(ParityCase c) => Receivable.OrderMirrorLines(
        BigInts(c.In["lines"]!), Big(c.In["limit"]!), c.In["hint"] is { } h ? Read<List<int>>(h) : null,
        c.In["maxStates"] is { } m ? (int)Long(m) : Receivable.MirrorOrderMaxStates);

    [Fact] public void Receivable_OrderMirrorLines() => Parity.Check(File, "orderMirrorLines", Mirror, 200);
    [Fact] public void Receivable_OrderMirrorLines_OldTestRandom() => Parity.Check(File, "orderMirrorLines.testRandom", Mirror, 300);
    [Fact] public void Receivable_OrderMirrorLines_OldTestPractical() => Parity.Check(File, "orderMirrorLines.testPractical", Mirror, 500);

    [Fact]
    public void Receivable_ReduceInstallments() => Parity.Check(File, "reduceInstallments", c =>
        Receivable.ReduceInstallments(Installments(c), Read<Dictionary<string, long>>(c.In["settled"]), Long(c.In["reductionMinor"]!), E<ReductionOrder>(c.In["order"]!)), 300);

    [Fact] public void Receivable_RescheduleInstallments() => Parity.Check(File, "rescheduleInstallments", c => Receivable.RescheduleInstallments(Installments(c), Long(c.In["targetMinor"]!)), 200);

    [Fact] public void TotalLoss_SettleTotalLoss() => Parity.Check("finance-total-loss.json", "settleTotalLoss", c => TotalLoss.SettleTotalLoss(Read<TotalLossInput>(c.In)), 800);

    private const string ImpairmentFile = "finance-impairment.json";

    private static List<PremiumTransaction> Txs(ParityCase c) => Read<List<PremiumTransaction>>(c.In["txs"]);
    private static ByBand? Percent(ParityCase c) => c.In["percent"] is { } p ? Read<ByBand>(p) : null;

    [Fact] public void Impairment_BandCutoffs() => Parity.Check(ImpairmentFile, "bandCutoffs", c => Impairment.BandCutoffsOf(Date(c.In["asOf"]!)), 1000);
    [Fact] public void Impairment_EarnedByAge() => Parity.Check(ImpairmentFile, "earnedByAge", c => Impairment.EarnedByAge(Txs(c), Date(c.In["asOf"]!)), 300);
    [Fact] public void Impairment_ImpairmentOf() => Parity.Check(ImpairmentFile, "impairmentOf", c => Impairment.ImpairmentOf(Txs(c), Long(c.In["paidMinor"]!), Date(c.In["asOf"]!), Percent(c)), 300);
    [Fact] public void Impairment_WithoutReserve() => Parity.Check(ImpairmentFile, "withoutReserve", c => Impairment.WithoutReserve(Impairment.ImpairmentOf(Txs(c), Long(c.In["paidMinor"]!), Date(c.In["asOf"]!))), 300);
    [Fact] public void Impairment_EarnedByAgeExact() => Parity.Check(ImpairmentFile, "earnedByAgeExact", c => Impairment.EarnedByAgeExact(Read<List<ExactPremiumTx>>(c.In["txs"]), Date(c.In["asOf"]!)), 300);
    [Fact] public void Impairment_ImpairmentOfExact() => Parity.Check(ImpairmentFile, "impairmentOfExact", c => Impairment.ImpairmentOfExact(Read<List<ExactPremiumTx>>(c.In["txs"]), Big(c.In["paid"]!), Date(c.In["asOf"]!), Percent(c)), 300);

    /// <summary>The old test's rates (fictional), for the cases marked 'test'.</summary>
    private static readonly Dictionary<string, decimal> TestTable = new()
    {
        ["USD 2026-10-15"] = 2.7m,
        ["USD 2026-10-16"] = 2.8m,
        ["EUR 2026-10-15"] = 3.1234m,
        ["EUR 2026-10-16"] = 2.000001m,
    };

    [Fact]
    public void PaymentAuthorization_Check() => Parity.Check("finance-payment-authorization.json", "paymentAuthorization", c =>
    {
        var calls = new List<string>();
        var table = Str(c.In["rates"]!) == "test";
        RateProvider provider = (currency, date) =>
        {
            calls.Add($"{currency} {Dates.ToIso(date)}");
            return table ? (TestTable.TryGetValue($"{currency} {Dates.ToIso(date)}", out var r) ? r : 0m) : Rates(currency, date);
        };
        void SameCalls()
        {
            var expected = c.Raw["calls"]!.AsArray().Select(x => $"{(string)x![0]!} {(string)x[1]!}").ToList();
            Assert.Equal(expected, calls);
        }
        try
        {
            var result = PaymentAuthorization.Check(Read<Money>(c.In["payment"]), Str(c.In["paidOn"]!), provider, Long(c.In["thresholdGelMinor"]!));
            SameCalls();
            return result;
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            SameCalls();
            throw;
        }
    }, 300);
}
