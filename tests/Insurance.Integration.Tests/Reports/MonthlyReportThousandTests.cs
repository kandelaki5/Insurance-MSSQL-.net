using System.Numerics;
using System.Text.Json;
using Dapper;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The monthly report over a thousand policies (legacy monthly-report-1000.test.ts, report-bulk-reads.test.ts over report-1000-world.ts):
/// the old policy Q (GEL 120.00 from 01.10.2026, paid on 05.10; October closed) and a thousand new policies P from 01.11.2026, each with its
/// own client and contract, two 5.1 lines (300.00 + 100.00), four instalments of 100.00 and the first paid. Two coverages of one class in
/// one policy are one policy issued; the receivable comes from the instalments and their allocations; a closed month's report is kept.
/// The old test counted the statements its model was sent (one bulk read, not two per policy); SQL Server's plans are not asserted here.
/// </summary>
public sealed class MonthlyReportThousandTests
{
    private const int N = ThousandWorld.N;

    private static Task<ReportWorld> WorldAsync(bool reverse = false) => ThousandWorld.CreateAsync(reverse);

    private static readonly object[] NovemberClass = [1000, new BigInteger(40_000_000), new BigInteger(40_000_000), new BigInteger(3_288_986), new BigInteger(36_721_995)];

    private static object[] Pick(int issued, BigInteger stat, BigInteger fin, BigInteger earned, BigInteger upr) => [issued, stat, fin, earned, upr];

    private static string Json(object report) => JsonSerializer.Serialize(report, new JsonSerializerOptions { Converters = { new BigIntegerText() } });

    private sealed class BigIntegerText : System.Text.Json.Serialization.JsonConverter<BigInteger>
    {
        public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    [Fact]
    public async Task November_over_a_thousand_policies_by_hand_read_twice_alike_and_October_as_it_was()
    {
        // Per P: November earns 30 of 365 days, 300 × 30/365 = 24.66 and 100 × 30/365 = 8.22; UPR 275.34 + 91.78; owed 300.00, nothing
        // overdue. Q: earned 120 × 30/365 = 9.86, UPR 120 − 20.05 = 99.95. 1 000 × 3 288 + 986 and 1 000 × 36 712 + 9 995 tetri.
        await using var w = await WorldAsync();
        await using (var c = await w.Database.OpenAdminAsync())
            Assert.Equal((N + 1, 2 * N + 1, 4 * N + 1, N + 1), await c.QuerySingleAsync<(int, int, int, int)>(
                "select (select count(*) from core.policy), (select count(*) from core.policy_line), (select count(*) from core.installment), (select count(*) from core.payment)"));
        var fingerprint = await w.FingerprintAsync();
        var november = await w.MonthAsync("2026-11");
        var c51 = Assert.Single(november.Classes);
        Assert.Equal(NovemberClass, Pick(c51.PoliciesIssued, c51.StatisticalWrittenGel, c51.FinancialWrittenGel, c51.EarnedGel, c51.UnearnedGel));
        var t = november.Totals;
        Assert.Equal(NovemberClass, Pick(t.PoliciesIssued, t.StatisticalWrittenGel, t.FinancialWrittenGel, t.EarnedGel, t.UnearnedGel));
        var gel = Assert.Single(november.Receivables);
        Assert.Equal(("GEL", new BigInteger(30_000_000), BigInteger.Zero, new BigInteger(30_000_000)), (gel.Currency, gel.Outstanding, gel.Overdue, gel.OutstandingGel));
        Assert.Equal(Json(november), Json(await w.MonthAsync("2026-11")));
        Assert.Equal(fingerprint, await w.FingerprintAsync());
        // October: only Q (issued, 120.00 written, paid), as it is without the thousand.
        var october = await w.MonthAsync("2026-10");
        var oc = Assert.Single(october.Classes);
        Assert.Equal(Pick(1, 12_000, 12_000, 1019, 10_981), Pick(oc.PoliciesIssued, oc.StatisticalWrittenGel, oc.FinancialWrittenGel, oc.EarnedGel, oc.UnearnedGel));
        Assert.Equal((BigInteger.Zero, BigInteger.Zero), (october.Receivables.Single().Outstanding, october.Receivables.Single().Overdue));
    }

    [Fact]
    public async Task The_same_thousand_written_in_the_reverse_order_give_the_same_whole_November_report()
    {
        await using var forward = await WorldAsync();
        await using var reverse = await WorldAsync(reverse: true);
        var a = await forward.MonthAsync("2026-11");
        var b = await reverse.MonthAsync("2026-11");
        var c51 = Assert.Single(b.Classes);
        Assert.Equal(NovemberClass, Pick(c51.PoliciesIssued, c51.StatisticalWrittenGel, c51.FinancialWrittenGel, c51.EarnedGel, c51.UnearnedGel));
        Assert.Equal(Json(a), Json(b));
    }
}
