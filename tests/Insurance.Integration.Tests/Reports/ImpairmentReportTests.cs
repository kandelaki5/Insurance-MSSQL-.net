using System.Numerics;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Finance;
using static Insurance.Integration.Tests.ReportWorld;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The impairment reserve on insurance receivables (legacy impairment-debtors.test.ts, impairment-half-tetri.test.ts,
/// impairment-exact-n14.test.ts, form1-broker-unimpaired.test.ts, impairment-reversal.test.ts, impairment-usd-xlsx.test.ts) over the same
/// fictional rows in SQL Server: four GEL policies of one policyholder owed by a client, a broker, an agent and all three; cover
/// 01.01.2026 → 01.01.2027; paid on 15.01.2026. Worked by hand in the old tests (docs/04 rule 20; a broker's receivable aged, not impaired).
/// </summary>
public sealed class ImpairmentReportTests
{
    public sealed class Debtors
    {
        public required PolicyRef Client, Broker, Agent, Mixed;
        public required Guid Holder;
        public required Guid MixedClientInstallment;
    }

    /// <summary>The four policies (impairment-debtors.test.ts world()); <paramref name="variant"/>: the mixed client instalment owed by an agent.</summary>
    public static async Task<Debtors> DebtorsAsync(ReportWorld w, bool variant = false)
    {
        var holder = await w.PartyAsync("ტესტაძე ნინო", "person", taxId: "01001000400");
        async Task<PolicyRef> P(string number, decimal premium, params (string, decimal, string)[] insts) =>
            await w.PolicyAsync(number, "2026-01-01", "2027-01-01", [new LineSpec("5.1", premium, premium)], insts.ToList(), holder: holder);
        var client = await P("P-A-CLIENT", 365m, ("2026-01-01", 365m, "client"));
        var broker = await P("P-B-BROKER", 730m, ("2026-01-01", 730m, "broker"));
        var agent = await P("P-C-AGENT", 1095m, ("2026-01-01", 1095m, "agent"));
        var mixed = await P("P-D-MIXED", 1460m, ("2026-01-01", 486.67m, "agent"), ("2026-01-01", 486.67m, "broker"), ("2026-01-01", 486.66m, variant ? "agent" : "client"));
        await w.PayAsync(broker.Installments[0], "2026-01-15", 100m);
        await w.PayAsync(agent.Installments[0], "2026-01-15", 200m);
        await w.PayAsync(mixed.Installments[2], "2026-01-15", 50m);
        return new Debtors { Client = client, Broker = broker, Agent = agent, Mixed = mixed, Holder = holder, MixedClientInstallment = mixed.Installments[2] };
    }

    private static string Bands(ByBand b) => $"{b.Over12},{b.M9to12},{b.M6to9},{b.M3to6},{b.M0to3}";

    /// <summary>A row as the old test's row(): number, kinds, premium, paid, unpaid, earned, unpaid bands, reserve bands, total reserve.</summary>
    public static string Row(ImpairmentReport.Row r) =>
        $"{r.PolicyNumber}|{string.Join(",", r.DebtorKinds)}|{r.Premium}|{r.Impairment.Paid}|{r.UnpaidPremium}|{Bands(r.Impairment.Earned)}|{Bands(r.Impairment.Unpaid)}|{Bands(r.Impairment.Reserve)}|{r.Impairment.TotalReserve}|{r.ReserveGel}|{r.UnpaidPremiumGel}";

    public static string Total(ImpairmentReport.Totals t) => $"{t.Kind} {t.Policies} {t.UnpaidPremiumGel} {t.UnpaidEarnedGel} {t.ReserveGel}";

    private static readonly string[] DecemberRows =
    [
        "P-A-CLIENT|client|36500|0|36500|0,9000,9100,9200,9200|0,9000,9100,9200,9200|0,4500,2730,920,0|8150|8150|36500",
        "P-B-BROKER|broker|73000|10000|63000|0,18000,18200,18400,18400|0,8000,18200,18400,18400|0,0,0,0,0|0|0|63000",
        "P-C-AGENT|agent|109500|20000|89500|0,27000,27300,27600,27600|0,7000,27300,27600,27600|0,3500,8190,2760,0|14450|14450|89500",
        "P-D-MIXED|agent,broker,client|146000|5000|141000|0,36000,36400,36800,36800|0,31000,36400,36800,36800|0,15500,10920,3680,0|30100|30100|141000",
    ];
    private static readonly string[] DecemberTotals = ["client 2 177500 177500 38250", "broker 1 63000 63000 0", "agent 1 89500 89500 14450", "all 4 330000 330000 52700"];

    [Fact]
    public async Task Four_policies_four_debtors_each_row_on_its_own_and_each_policy_once_in_the_totals()
    {
        await using var w = await CreateAsync();
        await DebtorsAsync(w);
        var before = await w.FingerprintAsync();
        var r = await w.ImpairmentAsync("2026-12-31");
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Equal(DecemberRows, r.Rows.Select(Row));
        Assert.Equal(DecemberTotals, r.Totals.Select(Total));
        Assert.Empty(r.Rates);
        Assert.All(r.Rows, x => Assert.Equal(("ტესტაძე ნინო", "retail", "5.1", D("2026-01-01"), D("2027-01-01"), (DateOnly?)null, (string?)null, (string?)null, (string?)null),
            (x.Policyholder, x.Segment, x.Classes, x.CoverFrom, x.CoverTo, x.CancelledOn, x.Intermediaries, x.Insured, x.Beneficiary)));
        // The totals add up: client + broker + agent = all.
        var by = r.Totals.ToDictionary(t => t.Kind);
        Assert.Equal(by["all"].ReserveGel, by["client"].ReserveGel + by["broker"].ReserveGel + by["agent"].ReserveGel);
        Assert.Equal(by["all"].Policies, by["client"].Policies + by["broker"].Policies + by["agent"].Policies);
    }

    [Fact]
    public async Task The_variant_puts_the_mixed_policy_under_broker_once_with_no_reserve()
    {
        await using var w = await CreateAsync();
        await DebtorsAsync(w, variant: true);
        var r = await w.ImpairmentAsync("2026-12-31");
        Assert.Equal("P-D-MIXED|agent,broker|146000|5000|141000|0,36000,36400,36800,36800|0,31000,36400,36800,36800|0,0,0,0,0|0|0|141000", Row(r.Rows[3]));
        Assert.Equal(["client 1 36500 36500 8150", "broker 2 204000 204000 0", "agent 1 89500 89500 14450", "all 4 330000 330000 22600"], r.Totals.Select(Total));
    }

    [Fact]
    public async Task An_advance_changes_no_report_until_allocated_and_its_late_allocation_changes_January_never_December()
    {
        await using var w = await CreateAsync();
        var d = await DebtorsAsync(w);
        var advance = await w.ReceiptAsync("2026-01-15", 150m, d.Holder);
        Assert.Equal(DecemberRows, (await w.ImpairmentAsync("2026-12-31")).Rows.Select(Row));
        var januaryBefore = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([
            "P-A-CLIENT|client|36500|0|36500|3100,8900,9200,9200,6100|3100,8900,9200,9200,6100|3100,4450,2760,920,0|11230|11230|36500",
            "P-B-BROKER|broker|73000|10000|63000|6200,17800,18400,18400,12200|0,14000,18400,18400,12200|0,0,0,0,0|0|0|63000",
            "P-C-AGENT|agent|109500|20000|89500|9300,26700,27600,27600,18300|0,16000,27600,27600,18300|0,8000,8280,2760,0|19040|19040|89500",
            "P-D-MIXED|agent,broker,client|146000|5000|141000|12400,35600,36800,36800,24400|7400,35600,36800,36800,24400|7400,17800,11040,3680,0|39920|39920|141000",
        ], januaryBefore.Rows.Select(Row));
        Assert.Equal(["client 2 177500 177500 51150", "broker 1 63000 63000 0", "agent 1 89500 89500 19040", "all 4 330000 330000 70190"], januaryBefore.Totals.Select(Total));
        await w.AllocateAsync(advance, d.Broker.Installments[0], 150m, 150m, 1m, "2027-01-10");
        Assert.Equal(DecemberRows, (await w.ImpairmentAsync("2026-12-31")).Rows.Select(Row));
        var after = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal("P-B-BROKER|broker|73000|25000|48000|6200,17800,18400,18400,12200|0,0,17400,18400,12200|0,0,0,0,0|0|0|48000", Row(after.Rows[1]));
        Assert.Equal(["broker 1 48000 48000 0", "all 4 315000 315000 70190"], after.Totals.Where(t => t.Kind is "broker" or "all").Select(Total));
        foreach (var i in new[] { 0, 2, 3 }) Assert.Equal(Row(januaryBefore.Rows[i]), Row(after.Rows[i]));
    }

    [Fact]
    public async Task A_reduction_issued_as_a_change_policy_lowers_the_policy_it_changes_and_has_no_row_of_its_own()
    {
        await using var w = await CreateAsync();
        var d = await DebtorsAsync(w);
        var june = (await w.ImpairmentAsync("2026-06-30")).Rows.Select(Row).ToList();
        await w.PolicyAsync("P-B-BROKER-1", "2026-07-01", "2027-01-01", [new LineSpec("5.1", -184m, -184m)], holder: d.Holder, replaces: d.Broker.Id);
        Assert.Equal(june, (await w.ImpairmentAsync("2026-06-30")).Rows.Select(Row));
        var dec = await w.ImpairmentAsync("2026-12-31");
        Assert.Equal(4, dec.Rows.Count);
        var broker = dec.Rows[1];
        Assert.Equal(("P-B-BROKER", 54600L, 44600L, 0L), (broker.PolicyNumber, broker.Premium, broker.UnpaidPremium, broker.ReserveGel));
        foreach (var i in new[] { 0, 2, 3 }) Assert.Equal(DecemberRows[i], Row(dec.Rows[i]));
        // Booked late, on 15.10.2026: out of the report of 14.10, aged by its cover days on 15.10.
        await using var late = await CreateAsync();
        var l = await DebtorsAsync(late);
        var oct14 = (await late.ImpairmentAsync("2026-10-14")).Rows.Select(Row).ToList();
        var oct15 = await late.ImpairmentAsync("2026-10-15");
        await late.PolicyAsync("P-B-BROKER-2", "2026-07-01", "2027-01-01", [new LineSpec("5.1", -184m, -184m)], holder: l.Holder, replaces: l.Broker.Id, bookedOn: "2026-10-15");
        Assert.Equal(oct14, (await late.ImpairmentAsync("2026-10-14")).Rows.Select(Row));
        var after = (await late.ImpairmentAsync("2026-10-15")).Rows[1];
        Assert.Equal((47600L, 36900L), (oct15.Rows[1].Impairment.UnpaidEarned, after.Impairment.UnpaidEarned));
        Assert.Equal(oct15.Rows[1].Impairment.Unpaid.M3to6 - 1500, after.Impairment.Unpaid.M3to6);
        Assert.Equal(oct15.Rows[1].Impairment.Unpaid.M0to3 - 9200, after.Impairment.Unpaid.M0to3);
        Assert.Equal(0L, after.ReserveGel);
    }

    [Fact]
    public async Task The_broker_paying_its_whole_schedule_lowers_no_other_policys_debt()
    {
        await using var w = await CreateAsync();
        var d = await DebtorsAsync(w);
        var june30 = await w.ImpairmentAsync("2026-06-30");
        await w.PayAsync(d.Broker.Installments[0], "2026-06-30", 630m);
        var after = await w.ImpairmentAsync("2026-06-30");
        foreach (var i in new[] { 0, 2, 3 }) Assert.Equal(Row(june30.Rows[i]), Row(after.Rows[i]));
        Assert.Equal((73000L, 0L), (after.Rows[1].Impairment.Paid, after.Rows[1].UnpaidPremium));
        Assert.Equal(0L, after.Rows[1].Impairment.UnpaidEarned);
        // The broker's prepaid (paid beyond earned) is not counted for anyone: unpaid premium 0, never below in the totals.
        var by = after.Totals.ToDictionary(t => t.Kind);
        Assert.Equal(by["all"].UnpaidPremiumGel, by["client"].UnpaidPremiumGel + by["broker"].UnpaidPremiumGel + by["agent"].UnpaidPremiumGel);
        Assert.NotEqual(Row(june30.Rows[1]), Row(after.Rows[1]));
    }

    [Fact]
    public async Task A_receipt_reversed_in_full_leaves_December_and_January_sees_nothing_paid()
    {
        await using var w = await CreateAsync();
        var p = await w.PolicyAsync("P-REV", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 365m)], [("2026-01-01", 365m, "broker")]);
        var receipt = await w.PayAsync(p.Installments[0], "2026-01-15", 100m);
        var december = (await w.ImpairmentAsync("2026-12-31")).Rows.Select(Row).ToList();
        Assert.Equal("P-REV|broker|36500|10000|26500|0,9000,9100,9200,9200|0,0,8100,9200,9200|0,0,0,0,0|0|0|26500", december.Single());
        var reversal = await w.ReversalAsync(receipt, "2027-01-31", -100m);
        await w.AllocateAsync(reversal, p.Installments[0], -100m, -100m, 1m, "2027-01-31");
        Assert.Equal(december, (await w.ImpairmentAsync("2026-12-31")).Rows.Select(Row));
        var jan = (await w.ImpairmentAsync("2027-01-31")).Rows.Single();
        Assert.Equal((0L, 36500L, 0L), (jan.Impairment.Paid, jan.UnpaidPremium, jan.ReserveGel));
        Assert.Equal(10000L, (await w.ImpairmentAsync("2027-01-30")).Rows.Single().Impairment.Paid);
    }

    [Fact]
    public async Task A_USD_receivable_at_2_7015_an_exact_half_tetri_rounds_away_from_zero()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-12-31", 2.7015m);
        // Owed 50.00 USD; another overpaid by 50.00 USD.
        var owed = await w.PolicyAsync("P-USD-OWED", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 50m, 135m)], [("2026-01-01", 50m, "client")], "USD", 2.7m);
        var paid = await w.PolicyAsync("P-USD-OVER", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 50m, 135m)], [("2026-01-01", 50m, "client")], "USD", 2.7m);
        var receipt = await w.ReceiptAsync("2026-01-15", 270m);
        await w.AllocateAsync(receipt, paid.Installments[0], 100m, 270m, 2.7m, "2026-01-15");
        var r = await w.ImpairmentAsync("2026-12-31");
        Assert.Equal([-13508L, 13508L], r.Rows.Select(x => x.UnpaidPremiumGel)); // P-USD-OVER, P-USD-OWED
        Assert.Equal(13508L, r.Totals.Single(t => t.Kind == "all").UnpaidPremiumGel);
        Assert.Equal([new ImpairmentReport.RateUsed("USD", 2.7015m, D("2026-12-31"))], r.Rates);
        var exact = await w.ImpairmentExactAsync("2026-12-31");
        Assert.Equal(new BigInteger(13508), exact.Totals.Single(t => t.Kind == "all").UnpaidPremiumGel);
        _ = owed;
    }

    [Fact]
    public async Task N1_4_exactly_to_the_last_tetri_and_four_zero_totals_when_nothing_is_owed()
    {
        await using var w = await CreateAsync();
        await w.PolicyAsync("P-C1", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 365m)], [("2026-01-01", 365m, "client")]);
        await w.PolicyAsync("P-C2", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 90071992547308.01m, 90071992547308.01m)], [("2026-01-01", 90071992547308.01m, "client")]);
        await w.PolicyAsync("P-B1", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 9999999999999999.99m, 9999999999999999.99m)], [("2026-01-01", 9999999999999999.99m, "broker")]);
        await w.PolicyAsync("P-B2", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 1095m, 1095m)], [("2026-01-01", 1095m, "broker")]);
        var agent = await w.PolicyAsync("P-A1", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 100m, 100m)], [("2026-01-01", 100m, "agent")]);
        var receipt = await w.ReceiptAsync("2026-01-15", 150m);
        await w.AllocateAsync(receipt, agent.Installments[0], 150m, 150m, 1m, "2026-01-15");
        var exact = await w.ImpairmentExactAsync("2026-12-31");
        var by = exact.Totals.ToDictionary(t => t.Kind);
        Assert.Equal(PremiumReportPrecisionTests.T("365.00") + PremiumReportPrecisionTests.T("90071992547308.01"), by["client"].UnpaidPremiumGel);
        Assert.Equal(PremiumReportPrecisionTests.T("9999999999999999.99") + PremiumReportPrecisionTests.T("1095.00"), by["broker"].UnpaidPremiumGel);
        Assert.Equal((1, BigInteger.Zero), (by["agent"].Policies, by["agent"].UnpaidPremiumGel));
        Assert.Equal(BigInteger.Zero, by["broker"].ReserveGel);
        await using var empty = await CreateAsync();
        Assert.Equal(["client 0 0 0", "broker 0 0 0", "agent 0 0 0", "all 0 0 0"],
            (await empty.ImpairmentExactAsync("2026-12-31")).Totals.Select(t => $"{t.Kind} {t.Policies} {t.UnpaidPremiumGel} {t.ReserveGel}"));
    }

    [Fact]
    public async Task The_workbook_has_the_templates_36_columns_one_row_per_policy_and_the_N1_4_totals()
    {
        await using var w = await CreateAsync();
        await DebtorsAsync(w);
        var report = await w.ImpairmentAsync("2026-12-31");
        var book = XlsxSheet.Read(ReportWorkbooks.Impairment(report));
        Assert.Equal(["ფორმა", "N1-4"], book.SheetNames);
        var form = XlsxSheet.Rows(book, "ფორმა");
        var c = ImpairmentTexts.Columns;
        var bands = Impairment.AgeBands.Select(b => ImpairmentTexts.Bands.ByKey[char.ToLowerInvariant(b.ToString()[0]) + b.ToString()[1..]]).ToList();
        string[] header =
        [
            c["number"], c["policyNumber"], c["coverFrom"], c["coverTo"], c["debtorKind"], c["intermediary"], c["policyholder"], c["classes"], c["segment"], c["insured"], c["beneficiary"],
            c["premium"], c["currency"], .. bands.Select(b => $"{c["earned"]}: {b}"), c["earnedTotal"], c["paid"], c["unpaidPremium"], c["rate"], c["unpaidPremiumGel"],
            c["unpaidEarned"], .. bands.Select(b => $"{c["unpaid"]}: {b}"), c["reserve"], .. bands.Select(b => $"{c["reserve"]}: {b}"), c["reserveGel"],
        ];
        Assert.Equal(36, header.Length);
        Assert.Equal(header, form[0]);
        Assert.Equal(["1", "P-A-CLIENT", "2026-01-01", "2027-01-01", ImpairmentTexts.Kinds["client"], "", "ტესტაძე ნინო", "5.1", Ka.Party.Segment["retail"], "", "",
            "365", "GEL", "0", "90", "91", "92", "92", "365", "0", "365", "1", "365", "365", "0", "90", "91", "92", "92", "81.5", "0", "45", "27.3", "9.2", "0", "81.5"], form[1]);
        Assert.Equal(5, form.Count);
        Assert.Equal([
            [c["debtorKind"], c["policies"], c["unpaidPremiumGel"], c["unpaidEarnedGel"], c["reserveGel"]],
            [ImpairmentTexts.Kinds["client"], "2", "1775", "1775", "382.5"],
            [ImpairmentTexts.Kinds["broker"], "1", "630", "630", "0"],
            [ImpairmentTexts.Kinds["agent"], "1", "895", "895", "144.5"],
            [ImpairmentTexts.Kinds["all"], "4", "3300", "3300", "527"],
        ], XlsxSheet.Rows(book, "N1-4"));
    }

    [Fact]
    public async Task A_brokers_receivable_is_aged_not_impaired_on_the_rows_and_in_form_1s_N1_4()
    {
        foreach (var (debtor, e10, e11, e13, e14) in new[] { ("broker", "0.00", "0.00", "265.00", "0.00"), ("client", "265.00", "33.50", "0.00", "0.00") })
        {
            await using var w = await CreateAsync();
            var p = await w.PolicyAsync("P-N14", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 365m)], [("2026-01-01", 365m, debtor)]);
            await w.PayAsync(p.Installments[0], "2026-01-15", 100m);
            var row = (await w.ImpairmentAsync("2026-12-31")).Rows.Single();
            Assert.Equal(debtor == "broker" ? 0L : 3350L, row.ReserveGel);
            var report = await w.MonthAsync("2026-12");
            var exact = await w.ImpairmentExactAsync("2026-12-31");
            var n1 = RegulatoryForms.Form1(report, exact, new Dictionary<string, string>());
            decimal V(string reference) => decimal.Parse(Insurance.Domain.Regulatory.ReservesPlacement.Shown(n1.ValueOf(Insurance.Domain.Regulatory.FinancialAnnex.ReceivablesSheet, reference)) ?? "0",
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal((decimal.Parse(e10), decimal.Parse(e11), decimal.Parse(e13), decimal.Parse(e14)), (V("E10"), V("E11"), V("E13"), V("E14")));
        }
    }
}
