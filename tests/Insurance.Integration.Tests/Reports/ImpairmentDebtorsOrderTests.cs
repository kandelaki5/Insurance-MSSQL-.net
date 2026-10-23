using Insurance.Application.Services;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The mixed policy's debtor kinds in every order (legacy impairment-debtors.test.ts, second describe; Codex
/// INS-FINANCE-IMPAIRMENT-DEBTOR-ORDER-OFFLINE-20261011): the totals of form N1-4 must not depend on the order in which a policy's debtor kinds
/// come back; the policy counts once, under the first of client → broker → agent (docs/05 13). The old test reordered the raw policies
/// read; here SQL Server returns the kinds sorted (string_agg … within group (order by …)), so the real report over the real rows is read
/// once, and its mixed row is then handed to the report's own totals (ImpairmentReport.TotalsOf) and receivable type (KindOf, which also
/// decides the row's reserve: a broker's receivable aged, not impaired) with its kinds in each of the eight orders. Worked by hand in the
/// old test: client 2 / 1775.00 / 382.50 … all 4 / 3300.00 / 527.00; the variant broker 2 / 2040.00 / 0 … all 4 / 3300.00 / 226.00.
/// </summary>
public sealed class ImpairmentDebtorsOrderTests
{
    private static readonly string[] Totals = ["client 2 177500 177500 38250", "broker 1 63000 63000 0", "agent 1 89500 89500 14450", "all 4 330000 330000 52700"];
    private static readonly string[] VariantTotals = ["client 1 36500 36500 8150", "broker 2 204000 204000 0", "agent 1 89500 89500 14450", "all 4 330000 330000 22600"];

    private static IEnumerable<string[]> Permutations(string[] xs) =>
        xs.Length <= 1 ? [xs] : xs.SelectMany((x, i) => Permutations([.. xs[..i], .. xs[(i + 1)..]]).Select(rest => (string[])[x, .. rest]));

    public static readonly IEnumerable<object[]> Orders =
        Permutations(["agent", "broker", "client"]).Select(o => new object[] { string.Join(",", o), false })
            .Concat(Permutations(["agent", "broker"]).Select(o => new object[] { string.Join(",", o), true })).ToList();

    [Fact]
    public void Eight_orders_in_all_the_six_of_three_kinds_and_the_two_of_two()
    {
        Assert.Equal(
            ["agent,broker,client", "agent,client,broker", "broker,agent,client", "broker,client,agent", "client,agent,broker", "client,broker,agent", "agent,broker", "broker,agent"],
            Orders.Select(o => (string)o[0]));
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task In_each_order_the_same_totals_the_mixed_policy_once_under_the_first_of_client_broker_agent(string order, bool variant)
    {
        await using var w = await CreateAsync();
        await ImpairmentWorld.DebtorsAsync(w, variant);
        var before = await w.FingerprintAsync();
        var report = await w.ImpairmentAsync("2026-12-31");
        Assert.Equal(before, await w.FingerprintAsync());
        var expected = variant ? VariantTotals : Totals;
        // As SQL Server reads them: sorted.
        Assert.Equal(["client", "broker", "agent", variant ? "agent,broker" : "agent,broker,client"], report.Rows.Select(r => string.Join(",", r.DebtorKinds)));
        Assert.Equal(expected, report.Totals.Select(ImpairmentReportTests.Total));

        // The mixed row's kinds handed back in this order: only the order changes, the same kinds each once.
        var kinds = order.Split(',');
        Assert.Equal(report.Rows[3].DebtorKinds.Order(StringComparer.Ordinal), kinds.Order(StringComparer.Ordinal));
        var rows = report.Rows.Select((r, i) => i == 3 ? r with { DebtorKinds = kinds } : r).ToList();
        var first = variant ? "broker" : "client";
        Assert.Equal(first, ImpairmentReport.KindOf(kinds));
        Assert.Equal(first, rows[3].PrimaryKind);
        // The row's reserve is the one of that first kind: none for a broker's receivable, the aged reserve for a client's.
        Assert.Equal(variant ? 0 : 30100, rows[3].ReserveGel);
        var totals = ImpairmentReport.TotalsOf(rows);
        Assert.Equal(expected, totals.Select(ImpairmentReportTests.Total));
        var by = totals.ToDictionary(t => t.Kind);
        foreach (var field in new Func<ImpairmentReport.Totals, long>[] { t => t.Policies, t => t.UnpaidPremiumGel, t => t.UnpaidEarnedGel, t => t.ReserveGel })
            Assert.Equal(field(by["all"]), field(by["client"]) + field(by["broker"]) + field(by["agent"]));
        Assert.Equal(rows.Count, by["all"].Policies);
        // The report's exact N1-4 totals (form 1) over the same rows agree.
        var exact = await w.ImpairmentExactAsync("2026-12-31");
        Assert.Equal(expected.Select(t => { var p = t.Split(' '); return $"{p[0]} {p[1]} {p[2]} {p[4]}"; }),
            exact.Totals.Select(t => $"{t.Kind} {t.Policies} {t.UnpaidPremiumGel} {t.ReserveGel}"));
    }
}
