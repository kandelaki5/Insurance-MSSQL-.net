using Dapper;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using static Insurance.Integration.Tests.ReportWorld;
using static Insurance.Integration.Tests.Reports.ImpairmentReportTests;

namespace Insurance.Integration.Tests.Reports;

/// <summary>
/// The impairment report on foreign receivables (legacy impairment-usd, -usd-dated, -usd-full-payment, -usd-full-reversal,
/// -usd-reduction-surplus, -usd-eur, -usd-eur-get-failure, -eur-cent, -eur-cent-settlement, -eur-cent-reversal, -history and
/// -fx-form1 tests) over the same fictional rows in SQL Server instead of the old strict read model: each policy worked in its own
/// currency, then valued in GEL at the report date's rate, each row rounded to the tetri; N1-4 adds the rows' GEL values; the GEL
/// amounts fixed in history are not rewritten; a broker's receivable is aged but not impaired (owner, 19.10.2026). The made-up rates
/// are stored rows (USD 3.0000 / 3.2000 and EUR 3.5000 / 3.6000 on 31.12.2026 / 31.01.2027); the National Bank is never asked.
/// Every expected value is the old test's, worked by hand.
/// </summary>
public sealed class ImpairmentCurrencyTests
{
    private const string Client = "P-A-CLIENT|client|36500|0|36500|0,9000,9100,9200,9200|0,9000,9100,9200,9200|0,4500,2730,920,0|8150|8150|36500";
    private const string Agent = "P-C-AGENT|agent|109500|20000|89500|0,27000,27300,27600,27600|0,7000,27300,27600,27600|0,3500,8190,2760,0|14450|14450|89500";
    private const string Mixed = "P-D-MIXED|agent,broker,client|146000|5000|141000|0,36000,36400,36800,36800|0,31000,36400,36800,36800|0,15500,10920,3680,0|30100|30100|141000";
    private const string BrokerDecember = "P-B-BROKER|broker|73000|10000|63000|0,18000,18200,18400,18400|0,8000,18200,18400,18400|0,0,0,0,0|0|0|189000";
    private const string ClientJanuary = "P-A-CLIENT|client|36500|0|36500|3100,8900,9200,9200,6100|3100,8900,9200,9200,6100|3100,4450,2760,920,0|11230|11230|36500";
    private const string AgentJanuary = "P-C-AGENT|agent|109500|20000|89500|9300,26700,27600,27600,18300|0,16000,27600,27600,18300|0,8000,8280,2760,0|19040|19040|89500";
    private const string MixedJanuary = "P-D-MIXED|agent,broker,client|146000|5000|141000|12400,35600,36800,36800,24400|7400,35600,36800,36800,24400|7400,17800,11040,3680,0|39920|39920|141000";
    private const string BrokerJanuaryBefore = "P-B-BROKER|broker|73000|10000|63000|6200,17800,18400,18400,12200|0,14000,18400,18400,12200|0,0,0,0,0|0|0|201600";
    private const string BrokerJanuaryPaid250 = "P-B-BROKER|broker|73000|25000|48000|6200,17800,18400,18400,12200|0,0,17400,18400,12200|0,0,0,0,0|0|0|153600";
    private const string BrokerJanuaryPaidInFull = "P-B-BROKER|broker|73000|73000|0|6200,17800,18400,18400,12200|0,0,0,0,0|0,0,0,0,0|0|0|0";

    private static async Task<(ReportWorld W, ImpairmentWorld.Debtors D)> UsdAsync()
    {
        var w = await CreateAsync();
        await w.RateAsync("USD", "2026-12-31", 3m);
        await w.RateAsync("USD", "2027-01-31", 3.2m);
        await w.RateAsync("EUR", "2026-12-31", 3.5m);
        await w.RateAsync("EUR", "2027-01-31", 3.6m);
        return (w, await ImpairmentWorld.DebtorsAsync(w, usdBroker: true));
    }

    private static async Task<List<string>> RowsAsync(ReportWorld w, string asOf) => (await w.ImpairmentAsync(asOf)).Rows.Select(Row).ToList();
    private static async Task<List<string>> TotalsAsync(ReportWorld w, string asOf) => (await w.ImpairmentAsync(asOf)).Totals.Select(Total).ToList();

    private static async Task<string> HistoryAsync(ReportWorld w)
    {
        await using var c = await w.Database.OpenAdminAsync();
        var rows = await c.QueryAsync<string>("""
            select concat(p.number, '|', p.premium_currency, '|', p.premium_rate, '|', l.premium_gel, '|', t.amount_gel)
            from core.policy p join core.policy_line l on l.policy_id = p.id join core.premium_transaction t on t.policy_line_id = l.id order by p.number, t.created_at
            """);
        var paid = await c.QueryAsync<string>("""
            select concat(y.received_on, '|', y.amount_gel, '|', a.amount, '|', a.amount_gel, '|', a.rate, '|', a.booked_on)
            from core.payment y left join core.payment_allocation a on a.payment_id = y.id order by y.created_at, a.created_at
            """);
        return string.Join("\n", rows.Concat(paid));
    }

    [Fact]
    public async Task The_USD_broker_is_worked_in_USD_and_valued_at_the_reporting_dates_rate_its_fixed_GEL_history_untouched()
    {
        var (w, _) = await UsdAsync();
        await using var owner = w;
        var history = await HistoryAsync(w);
        var before = await w.FingerprintAsync();
        var rates = w.Rates();
        var first = await w.ImpairmentAsync("2026-12-31", rates);
        var second = await w.ImpairmentAsync("2026-12-31", rates);
        Assert.Equal([Client, BrokerDecember, Agent, Mixed], first.Rows.Select(Row));
        Assert.Equal(["client 2 177500 177500 38250", "broker 1 189000 189000 0", "agent 1 89500 89500 14450", "all 4 456000 456000 52700"], first.Totals.Select(Total));
        Assert.Equal([new ImpairmentReport.RateUsed("USD", 3m, D("2026-12-31"))], first.Rates);
        Assert.Equal([1m, 3m, 1m, 1m], first.Rows.Select(r => r.Rate));
        foreach (var r in first.Rows)
            Assert.Equal((r.Impairment.TotalEarned, r.Impairment.UnpaidEarned, r.Impairment.TotalReserve),
                (Sum(r.Impairment.Earned), Sum(r.Impairment.Unpaid), Sum(r.Impairment.Reserve)));
        Assert.Equal(first.Rows.Select(Row), second.Rows.Select(Row));
        // One USD lookup per report, none for GEL; the reads wrote nothing; 1971.00 / 270.00 / 2.7000 as written.
        Assert.Equal([("USD", D("2026-12-31")), ("USD", D("2026-12-31"))], rates.Asked);
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Equal(history, await HistoryAsync(w));
        Assert.Contains("P-B-BROKER|USD|2.700000|1971.00|1971.00", history);
        Assert.Contains("2026-01-15|270.00|100.00|270.00|2.700000|2026-01-15", history);
    }

    private static long Sum(Insurance.Domain.Finance.ByBand b) => b.Over12 + b.M9to12 + b.M6to9 + b.M3to6 + b.M0to3;

    [Fact]
    public async Task A_payment_dated_in_January_at_its_own_rate_counts_in_January_only_and_is_valued_at_each_reports_rate()
    {
        var (w, d) = await UsdAsync();
        await using var owner = w;
        var januaryBefore = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([ClientJanuary, BrokerJanuaryBefore, AgentJanuary, MixedJanuary], januaryBefore.Rows.Select(Row));
        Assert.Equal(["client 2 177500 177500 51150", "broker 1 201600 201600 0", "agent 1 89500 89500 19040", "all 4 468600 468600 70190"], januaryBefore.Totals.Select(Total));
        Assert.Equal([new ImpairmentReport.RateUsed("USD", 3.2m, D("2027-01-31"))], januaryBefore.Rates);
        // 465.00 GEL received on 10.01.2027 and allocated as 150.00 USD at that day's 3.1000.
        await w.PayAsync(d.Broker.Installments[0], "2027-01-10", 150m, 3.1m);
        Assert.Equal([Client, BrokerDecember, Agent, Mixed], await RowsAsync(w, "2026-12-31"));
        Assert.Equal("all 4 456000 456000 52700", (await TotalsAsync(w, "2026-12-31"))[3]);
        var after = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([ClientJanuary, BrokerJanuaryPaid250, AgentJanuary, MixedJanuary], after.Rows.Select(Row));
        Assert.Equal(["client 2 177500 177500 51150", "broker 1 153600 153600 0", "agent 1 89500 89500 19040", "all 4 420600 420600 70190"], after.Totals.Select(Total));
        Assert.Contains("2027-01-10|465.00|150.00|465.00|3.100000|2027-01-10", await HistoryAsync(w));
    }

    [Fact]
    public async Task Paid_in_full_it_owes_nothing_and_the_full_reversal_of_the_last_receipt_brings_its_480_USD_back()
    {
        var (w, d) = await UsdAsync();
        await using var owner = w;
        await w.PayAsync(d.Broker.Installments[0], "2027-01-10", 150m, 3.1m);
        var receipt = await w.PayAsync(d.Broker.Installments[0], "2027-01-20", 480m, 3.2m);
        var paid = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([ClientJanuary, BrokerJanuaryPaidInFull, AgentJanuary, MixedJanuary], paid.Rows.Select(Row));
        Assert.Equal(["client 2 177500 177500 51150", "broker 1 0 0 0", "agent 1 89500 89500 19040", "all 4 267000 267000 70190"], paid.Totals.Select(Total));
        Assert.Equal(3.2m, paid.Rows[1].Rate);
        Assert.Equal([Client, BrokerDecember, Agent, Mixed], await RowsAsync(w, "2026-12-31"));
        // The reversal as reversePayment writes it: −1536.00 GEL on the day, the allocation's mirror at the receipt's own 3.2000.
        var reversal = await w.ReversalAsync(receipt, "2027-01-31", -1536m);
        await w.AllocateAsync(reversal, d.Broker.Installments[0], -480m, -1536m, 3.2m, "2027-01-31");
        var reversed = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([ClientJanuary, BrokerJanuaryPaid250, AgentJanuary, MixedJanuary], reversed.Rows.Select(Row));
        Assert.Equal(["broker 1 153600 153600 0", "all 4 420600 420600 70190"], reversed.Totals.Where(t => t.Kind is "broker" or "all").Select(Total));
        Assert.Equal([Client, BrokerDecember, Agent, Mixed], await RowsAsync(w, "2026-12-31"));
    }

    [Fact]
    public async Task A_reduction_larger_than_what_is_unpaid_shows_its_surplus_on_its_row_and_takes_nothing_from_any_total()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-06-30", 3m);
        await w.RateAsync("USD", "2026-07-01", 3.2m);
        var d = await ImpairmentWorld.DebtorsAsync(w, usdBroker: true);
        await w.PayAsync(d.Broker.Installments[0], "2026-06-30", 630m, 3.1m);
        string[] june =
        [
            "P-A-CLIENT|client|36500|0|36500|0,0,0,9000,9100|0,0,0,9000,9100|0,0,0,900,0|900|900|36500",
            "P-B-BROKER|broker|73000|73000|0|0,0,0,18000,18200|0,0,0,0,0|0,0,0,0,0|0|0|0",
            "P-C-AGENT|agent|109500|20000|89500|0,0,0,27000,27300|0,0,0,7000,27300|0,0,0,700,0|700|700|89500",
            "P-D-MIXED|agent,broker,client|146000|5000|141000|0,0,0,36000,36400|0,0,0,31000,36400|0,0,0,3100,0|3100|3100|141000",
        ];
        string[] juneTotals = ["client 2 177500 85500 4000", "broker 1 0 0 0", "agent 1 89500 34300 700", "all 4 267000 119800 4700"];
        string client = "P-A-CLIENT|client|36500|0|36500|0,0,100,9000,9100|0,0,100,9000,9100|0,0,30,900,0|930|930|36500";
        string agent = "P-C-AGENT|agent|109500|20000|89500|0,0,300,27000,27300|0,0,0,7300,27300|0,0,0,730,0|730|730|89500";
        string mixed = "P-D-MIXED|agent,broker,client|146000|5000|141000|0,0,400,36000,36400|0,0,0,31400,36400|0,0,0,3140,0|3140|3140|141000";
        string[] julyTotals = ["client 2 177500 86000 4070", "broker 1 0 0 0", "agent 1 89500 34600 730", "all 4 267000 120600 4800"];
        Assert.Equal(june, await RowsAsync(w, "2026-06-30"));
        Assert.Equal(juneTotals, await TotalsAsync(w, "2026-06-30"));
        var julyBefore = await w.ImpairmentAsync("2026-07-01");
        Assert.Equal([client, "P-B-BROKER|broker|73000|73000|0|0,0,200,18000,18200|0,0,0,0,0|0,0,0,0,0|0|0|0", agent, mixed], julyBefore.Rows.Select(Row));
        Assert.Equal(julyTotals, julyBefore.Totals.Select(Total));

        // The approved reduction as the report reads it: a change policy replacing the broker's, USD at 3.2000, −184.00 USD = −588.80 GEL.
        await w.PolicyAsync("P-B-BROKER-1", "2026-07-01", "2027-01-01", [new LineSpec("5.1", -184m, -588.80m)], currency: "USD", rate: 3.2m, holder: d.Holder, replaces: d.Broker.Id);
        Assert.Equal(june, await RowsAsync(w, "2026-06-30"));
        Assert.Equal(juneTotals, await TotalsAsync(w, "2026-06-30"));
        var reduced = await w.ImpairmentAsync("2026-07-01");
        Assert.Equal([client, "P-B-BROKER|broker|54600|73000|-18400|0,0,200,18000,18100|0,0,0,0,0|0,0,0,0,0|0|0|-58880", agent, mixed], reduced.Rows.Select(Row));
        Assert.Equal(julyTotals, reduced.Totals.Select(Total));

        // The workbook: the signed −588.80 on the broker's row; N1-4 as the report.
        var book = XlsxSheet.Read(ReportWorkbooks.Impairment(reduced));
        var form = XlsxSheet.Rows(book, "ფორმა");
        string Body(int n, string number, string kind, string numbers) =>
            $"{n}|{number}|2026-01-01|2027-01-01|{ImpairmentTexts.Kinds[kind]}||ტესტაძე ნინო|5.1|საცალო|||{numbers}";
        Assert.Equal([
            Body(1, "P-A-CLIENT", "client", "365|GEL|0|0|1|90|91|182|0|365|1|365|182|0|0|1|90|91|9.3|0|0|0.3|9|0|9.3"),
            Body(2, "P-B-BROKER", "broker", "546|USD|0|0|2|180|181|363|730|-184|3.2|-588.8|0|0|0|0|0|0|0|0|0|0|0|0|0"),
            Body(3, "P-C-AGENT", "agent", "1095|GEL|0|0|3|270|273|546|200|895|1|895|346|0|0|0|73|273|7.3|0|0|0|7.3|0|7.3"),
            Body(4, "P-D-MIXED", "client", "1460|GEL|0|0|4|360|364|728|50|1410|1|1410|678|0|0|0|314|364|31.4|0|0|0|31.4|0|31.4"),
        ], form.Skip(1).Select(r => string.Join("|", r)));
        Assert.Equal([
            $"{ImpairmentTexts.Kinds["client"]}|2|1775|860|40.7", $"{ImpairmentTexts.Kinds["broker"]}|1|0|0|0",
            $"{ImpairmentTexts.Kinds["agent"]}|1|895|346|7.3", $"{ImpairmentTexts.Kinds["all"]}|4|2670|1206|48",
        ], XlsxSheet.Rows(book, "N1-4").Skip(1).Select(r => string.Join("|", r)));
    }

    private static async Task<PolicyRef> EurAsync(ReportWorld w, ImpairmentWorld.Debtors d, string number, decimal paid)
    {
        var p = await w.PolicyAsync(number, "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 1022m)], [("2026-01-01", 365m, "broker")], "EUR", 2.8m, holder: d.Holder);
        await w.PayAsync(p.Installments[0], "2026-01-15", paid, 3m);
        return p;
    }

    private const string EurDecember = "P-E-EUR|broker|36500|10000|26500|0,9000,9100,9200,9200|0,0,8100,9200,9200|0,0,0,0,0|0|0|92750";
    private const string EurJanuaryBefore = "P-E-EUR|broker|36500|10000|26500|3100,8900,9200,9200,6100|0,2000,9200,9200,6100|0,0,0,0,0|0|0|95400";
    private const string EurJanuaryAfter = "P-E-EUR|broker|36500|15000|21500|3100,8900,9200,9200,6100|0,0,6200,9200,6100|0,0,0,0,0|0|0|77400";

    [Fact]
    public async Task USD_and_EUR_in_one_report_each_at_its_own_rate_N1_4_adds_GEL()
    {
        var (w, d) = await UsdAsync();
        await using var owner = w;
        var eur = await EurAsync(w, d, "P-E-EUR", 100m);
        var rates = w.Rates();
        var december = await w.ImpairmentAsync("2026-12-31", rates);
        Assert.Equal([Client, BrokerDecember, Agent, Mixed, EurDecember], december.Rows.Select(Row));
        Assert.Equal(["client 2 177500 177500 38250", "broker 2 281750 281750 0", "agent 1 89500 89500 14450", "all 5 548750 548750 52700"], december.Totals.Select(Total));
        Assert.Equal([new ImpairmentReport.RateUsed("USD", 3m, D("2026-12-31")), new ImpairmentReport.RateUsed("EUR", 3.5m, D("2026-12-31"))], december.Rates);
        Assert.Equal([("USD", D("2026-12-31")), ("EUR", D("2026-12-31"))], rates.Asked);
        var januaryBefore = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([ClientJanuary, BrokerJanuaryBefore, AgentJanuary, MixedJanuary, EurJanuaryBefore], januaryBefore.Rows.Select(Row));
        Assert.Equal(["broker 2 297000 297000 0", "all 5 564000 564000 70190"], januaryBefore.Totals.Where(t => t.Kind is "broker" or "all").Select(Total));
        // 50.00 EUR = 200.00 GEL at 4.0000 on 10.01.2027 for the EUR instalment only.
        await w.PayAsync(eur.Installments[0], "2027-01-10", 50m, 4m);
        Assert.Equal([Client, BrokerDecember, Agent, Mixed, EurDecember], await RowsAsync(w, "2026-12-31"));
        var after = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([ClientJanuary, BrokerJanuaryBefore, AgentJanuary, MixedJanuary, EurJanuaryAfter], after.Rows.Select(Row));
        Assert.Equal(["client 2 177500 177500 51150", "broker 2 279000 279000 0", "agent 1 89500 89500 19040", "all 5 546000 546000 70190"], after.Totals.Select(Total));
        Assert.Equal([new ImpairmentReport.RateUsed("USD", 3.2m, D("2027-01-31")), new ImpairmentReport.RateUsed("EUR", 3.6m, D("2027-01-31"))], after.Rates);
    }

    [Fact]
    public async Task When_the_second_rate_fails_the_error_comes_out_nothing_is_written_and_the_next_reports_are_right()
    {
        var (w, d) = await UsdAsync();
        await using var owner = w;
        var eur = await EurAsync(w, d, "P-E-EUR", 100m);
        await w.PayAsync(eur.Installments[0], "2027-01-10", 50m, 4m);
        var before = await w.FingerprintAsync();
        var history = await HistoryAsync(w);
        var stored = w.Rates();
        var asked = new List<string>();
        var failure = new InvalidOperationException("EUR rate of 2027-01-31 refused (armed)");
        async Task<(decimal Rate, DateOnly Date)> Armed(string currency, DateOnly date)
        {
            asked.Add($"{currency} {date:yyyy-MM-dd}");
            if (currency == "EUR") throw failure;
            return await stored.ValueAsync(currency, date);
        }
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => w.App.ReadTxAsync(tx => ImpairmentReport.BuildAsync(tx, Armed, D("2027-01-31"))));
        Assert.Same(failure, thrown);
        Assert.Equal(["USD 2027-01-31", "EUR 2027-01-31"], asked);
        Assert.Equal(before, await w.FingerprintAsync());
        Assert.Equal(history, await HistoryAsync(w));
        var january = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([ClientJanuary, BrokerJanuaryBefore, AgentJanuary, MixedJanuary, EurJanuaryAfter], january.Rows.Select(Row));
        Assert.Equal("all 5 546000 546000 70190", Total(january.Totals[3]));
        Assert.Equal("all 5 548750 548750 52700", (await TotalsAsync(w, "2026-12-31"))[3]);
    }

    [Fact]
    public async Task A_euro_cent_owed_on_each_of_two_policies_is_0_04_GEL_on_each_row_and_0_08_in_N1_4_settled_and_reversed()
    {
        var (w, d) = await UsdAsync();
        await using var owner = w;
        var eur = await EurAsync(w, d, "P-E-EUR", 100m);
        await w.PayAsync(eur.Installments[0], "2027-01-10", 50m, 4m);
        var f = await EurAsync(w, d, "P-F-EUR-CENT", 364.99m);
        await EurAsync(w, d, "P-G-EUR-CENT", 364.99m);
        string Cent(string number, string earned, int paid = 36499, long gel = 4) =>
            $"{number}|broker|36500|{paid}|{36500 - paid}|{earned}|0,0,0,0,{36500 - paid}|0,0,0,0,0|0|0|{gel}";
        const string decEarned = "0,9000,9100,9200,9200";
        const string janEarned = "3100,8900,9200,9200,6100";
        var december = await w.ImpairmentAsync("2026-12-31");
        Assert.Equal([Client, BrokerDecember, Agent, Mixed, EurDecember, Cent("P-F-EUR-CENT", decEarned), Cent("P-G-EUR-CENT", decEarned)], december.Rows.Select(Row));
        Assert.Equal(["client 2 177500 177500 38250", "broker 4 281758 281758 0", "agent 1 89500 89500 14450", "all 7 548758 548758 52700"], december.Totals.Select(Total));
        var january = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([Cent("P-F-EUR-CENT", janEarned), Cent("P-G-EUR-CENT", janEarned)], january.Rows.Skip(5).Select(Row));
        Assert.Equal(["broker 4 279008 279008 0", "all 7 546008 546008 70190"], january.Totals.Where(t => t.Kind is "broker" or "all").Select(Total));
        // The 0.01 EUR settled on 20.01.2027 at its own 3.0000 (0.03 GEL): the row goes from 0.04 to 0, N1-4 drops by 0.04, not 0.03.
        var receipt = await w.PayAsync(f.Installments[0], "2027-01-20", 0.01m, 3m);
        Assert.Equal(december.Rows.Select(Row), await RowsAsync(w, "2026-12-31"));
        var settled = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([Cent("P-F-EUR-CENT", janEarned, 36500, 0), Cent("P-G-EUR-CENT", janEarned)], settled.Rows.Skip(5).Select(Row));
        Assert.Equal(["broker 4 279004 279004 0", "all 7 546004 546004 70190"], settled.Totals.Where(t => t.Kind is "broker" or "all").Select(Total));
        // Reversed in full on 31.01.2027 (−0.03 GEL, −0.01 EUR at 3.0000): owed again, 0.04 at the report's rate, not 0.03.
        var reversal = await w.ReversalAsync(receipt, "2027-01-31", -0.03m);
        await w.AllocateAsync(reversal, f.Installments[0], -0.01m, -0.03m, 3m, "2027-01-31");
        Assert.Equal(december.Rows.Select(Row), await RowsAsync(w, "2026-12-31"));
        var reversed = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal(january.Rows.Select(Row), reversed.Rows.Select(Row));
        Assert.Equal(january.Totals.Select(Total), reversed.Totals.Select(Total));
        // The workbook shows the cent policies' 0.01 EUR and 0.04 GEL.
        var form = XlsxSheet.Rows(ReportWorkbooks.Impairment(reversed), "ფორმა");
        Assert.Equal(["365", "EUR", "31", "89", "92", "92", "61", "365", "364.99", "0.01", "3.6", "0.04", "0.01"], form[6][11..24]);
        Assert.Equal([ImpairmentTexts.Kinds["broker"], "4", "2790.08", "2790.08", "0"], XlsxSheet.Rows(ReportWorkbooks.Impairment(reversed), "N1-4")[2]);
    }

    [Fact]
    public async Task The_December_report_is_unchanged_by_rows_appended_in_January_which_the_January_report_reads()
    {
        await using var w = await CreateAsync();
        var holder = await w.PartyAsync("ტესტაძე ნინო", "person", taxId: "01001000365");
        var p = await w.PolicyAsync("P-IMPAIR-365", "2026-01-01", "2027-01-01", [new LineSpec("5.1", 365m, 365m)], [("2026-01-01", 365m, "broker")], holder: holder);
        await w.PayAsync(p.Installments[0], "2026-01-15", 100m);
        const string december = "P-IMPAIR-365|broker|36500|10000|26500|0,9000,9100,9200,9200|0,0,8100,9200,9200|0,0,0,0,0|0|0|26500";
        Assert.Equal([december], await RowsAsync(w, "2026-12-31"));
        Assert.Equal(["client 0 0 0 0", "broker 1 26500 26500 0", "agent 0 0 0 0", "all 1 26500 26500 0"], await TotalsAsync(w, "2026-12-31"));
        await w.PayAsync(p.Installments[0], "2027-01-10", 50m);
        await w.PolicyAsync("P-FUT", "2027-01-01", "2028-01-01", [new LineSpec("5.1", 365m, 365m)], [("2027-01-10", 365m, "broker")], holder: holder, bookedOn: "2027-01-10");
        Assert.Equal([december], await RowsAsync(w, "2026-12-31"));
        var january = await w.ImpairmentAsync("2027-01-31");
        Assert.Equal([
            "P-FUT|broker|36500|0|36500|0,0,0,0,3100|0,0,0,0,3100|0,0,0,0,0|0|0|36500",
            "P-IMPAIR-365|broker|36500|15000|21500|3100,8900,9200,9200,6100|0,0,6200,9200,6100|0,0,0,0,0|0|0|21500",
        ], january.Rows.Select(Row));
        Assert.Equal("broker 2 58000 24600 0", Total(january.Totals[1]));
    }

    [Fact]
    public async Task A_foreign_half_tetri_is_rounded_away_from_zero_alike_on_the_rows_the_workbook_and_form_1s_N1_4()
    {
        await using var w = await CreateAsync();
        await w.RateAsync("USD", "2026-12-31", 2.7015m);
        LineSpec[] line = [new LineSpec("5.1", 50m, 135m)];
        await w.PolicyAsync("P-H-BROKER", "2026-01-01", "2027-01-01", line, [("2026-01-01", 50m, "broker")], "USD", 2.7m);
        var over = await w.PolicyAsync("P-H-OVERPAID", "2026-01-01", "2027-01-01", line, [("2026-01-01", 50m, "client")], "USD", 2.7m);
        await w.PolicyAsync("P-H-OWED", "2026-01-01", "2027-01-01", line, [("2026-01-01", 50m, "client")], "USD", 2.7m);
        await w.PayAsync(over.Installments[0], "2026-01-15", 100m, 2.7m);
        var report = await w.ImpairmentAsync("2026-12-31");
        Assert.Equal([("P-H-BROKER", 13508L), ("P-H-OVERPAID", -13508L), ("P-H-OWED", 13508L)], report.Rows.Select(r => (r.PolicyNumber, r.UnpaidPremiumGel)));
        Assert.Equal(0L, report.Rows[0].ReserveGel);
        var by = report.Totals.ToDictionary(t => t.Kind);
        Assert.Equal((13508L, 13508L, 0L), (by["client"].UnpaidPremiumGel, by["broker"].UnpaidPremiumGel, by["broker"].ReserveGel));
        Assert.Equal(report.Rows[2].ReserveGel, by["client"].ReserveGel);
        Assert.Equal(["135.08", "-135.08", "135.08"], XlsxSheet.Rows(ReportWorkbooks.Impairment(report), "ფორმა").Skip(1).Select(r => r[22]));
        var exact = await w.ImpairmentExactAsync("2026-12-31");
        var n1 = RegulatoryForms.Form1(await w.MonthAsync("2026-12"), exact, new Dictionary<string, string>());
        decimal V(string reference) => decimal.Parse(Insurance.Domain.Regulatory.ReservesPlacement.Shown(n1.ValueOf(Insurance.Domain.Regulatory.FinancialAnnex.ReceivablesSheet, reference)) ?? "",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal((135.08m, by["client"].ReserveGel / 100m, 135.08m, 0m), (V("E10"), V("E11"), V("E13"), V("E14")));
    }
}
