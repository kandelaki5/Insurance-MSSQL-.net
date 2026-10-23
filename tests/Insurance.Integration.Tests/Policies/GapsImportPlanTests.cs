using System.Net;
using System.Numerics;
using System.Text;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.Policies.Gaps;
using Import = Insurance.Application.Services.PolicyImport;

namespace Insurance.Integration.Tests.Policies;

/// <summary>A National Bank that answers one date with fixed rates (per quantity, as the bank gives them) and refuses every other request; counted.</summary>
public sealed class OneDayNbg(string date, params (string Code, decimal Quantity, decimal Rate)[] rates) : IHttpClientFactory
{
    public List<string> Requests { get; } = [];
    public int Failures { get; set; }
    public string Date { get; } = date;
    public IReadOnlyList<(string Code, decimal Quantity, decimal Rate)> Answer { get; } = rates;

    private sealed class Handler(OneDayNbg owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (owner.Requests) owner.Requests.Add(url);
            if (!url.EndsWith($"&date={owner.Date}", StringComparison.Ordinal)) throw new HttpRequestException("network is forbidden in this test");
            if (owner.Failures > 0)
            {
                owner.Failures--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            var currencies = string.Join(",", owner.Answer.Select(r => $$"""{"code":"{{r.Code}}","quantity":{{r.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"rate":{{r.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""[{"date":"{{owner.Date}}","currencies":[{{currencies}}]}]""", Encoding.UTF8, "application/json"),
            });
        }
    }

    public HttpClient CreateClient(string name) => new(new Handler(this));
}

/// <summary>
/// The imported policies after the import (legacy import-plan-confirmation.test.ts, the cases ImportTests leaves out): the
/// two imported policies approved in their month's report, a receipt on each keeping to its own policy, a receipt's
/// excess attributed to the other policy, the shared receipt reversed, the second USD start rate fetched from the bank.
/// The import and approval cases keep the legacy dates (01.10.2026 and 15.10.2026): the report of a month with no receipt
/// does not depend on today. The receipt cases run on the first of today's month with receipts received and entered
/// today (legacy 07.10.2026 and 07.11.2026), their figures checked in one month; the tests at the end run the same cases on the
/// legacy days, the database's clock (DbOptions.Clock) standing on each action's day, with the month boundary between the receipt
/// and its later attribution, reversal or registration (docs/legacy-tests-gaps-b.md).
/// </summary>
public sealed class GapsImportPlanTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private const string Header = "make,model,plate,value,installments,premium_damage,limit_damage,deductible_damage,premium_theft,limit_theft,deductible_theft";
    private const string Georgian = Header + "\nტესტი,იმპორტი,IM-011-PT,30000,11,900,30000,0,300,30000,0\nტესტი,მეორე,IM-004-PT,20000,4,300,20000,0,100,20000,0\n";
    private const string UsdCsv = "make,model,plate,value,currency,effective_from,effective_to,installments,premium_damage,limit_damage,deductible_damage,premium_theft,limit_theft,deductible_theft\n"
        + "ტესტი,დოლარი,US-011-PT,30000,USD,2026-10-01,2027-10-01,11,900,30000,0,300,30000,0\n"
        + "ტესტი,მეორე,US-004-PT,20000,USD,2026-10-15,2027-10-15,4,300,20000,0,100,20000,0\n";

    private static string TaxId() => (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record Imported(CurrentUser Underwriter, CurrentUser Head, Guid Holder, Guid First, Guid Second);

    /// <summary>The real preview and confirmation of a file (the payload as the preview made it), by the underwriter; the two policies in start/number order.</summary>
    private static async Task<Imported> ImportAsync(World w, string csv, DateOnly from, Rates? rates = null)
    {
        var underwriter = await w.UserAsync("underwriter");
        var head = await w.UserAsync("operations_head");
        var taxId = TaxId();
        var holder = await w.ClientAsync(taxId);
        var form = World.Form(("policyholder_id_number", taxId), ("effective_from", Iso(from)), ("effective_to", Iso(from.AddYears(1))), ("currency", "GEL"), ("installments", "1"));
        var preview = await Import.PreviewAsync(w.App, underwriter, form, Encoding.UTF8.GetBytes(csv));
        Assert.True(preview.Payload is not null, preview.Message ?? string.Join("; ", preview.Rows!.SelectMany(r => r.Errors)));
        var confirmed = await Import.ConfirmAsync(w.App, rates ?? w.Rates, underwriter, preview.Payload, preview);
        Assert.True(confirmed.Redirect is not null, confirmed.Message);
        var ids = await w.QueryAsync<Guid>("""
            select p.id from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder order by p.effective_from, p.number
            """, new { holder });
        Assert.Equal(2, ids.Count);
        return new Imported(underwriter, head, holder, ids[0], ids[1]);
    }

    private static async Task ApproveBothAsync(World w, Imported i)
    {
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, i.Head, i.First.ToString())).Outcome);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, i.Head, i.Second.ToString())).Outcome);
    }

    private static void Empty(PremiumReport.Report r)
    {
        Assert.Empty(r.Classes);
        Assert.Empty(r.Receivables);
        Assert.Empty(r.Claims);
        Assert.Equal(new PremiumReport.Totals(0, 0, 0, 0, 0, 0, 0, 0), r.Totals);
        Assert.Equal(BigInteger.Zero, r.TreatyPremiumGel);
    }

    /// <summary>The premium part of a month of the two GEL policies: 2 issued in it (or 0), the given earned and UPR, IBNR 80.00.</summary>
    private static void Premium(PremiumReport.Report r, int issued, BigInteger written, BigInteger earned, BigInteger unearned, BigInteger ibnr)
    {
        var c = Assert.Single(r.Classes);
        Assert.Equal(("5.1", issued, written, written, earned, unearned, BigInteger.Zero, BigInteger.Zero, BigInteger.Zero),
            (c.ClassCode, c.PoliciesIssued, c.StatisticalWrittenGel, c.FinancialWrittenGel, c.EarnedGel, c.UnearnedGel, c.CededWrittenGel, c.CededUnearnedGel, c.RiCommissionGel));
        Assert.Equal(new PremiumReport.Totals(issued, written, written, earned, unearned, 0, 0, 0), r.Totals);
        Assert.Equal(new PremiumReport.ClaimsTotals(0, 0, ibnr, 0, 0, 0), r.ClaimsTotals);
        Assert.Equal(("5.1", ibnr), (Assert.Single(r.Claims).ClassCode, r.Claims[0].IbnrGel));
        Assert.Equal(BigInteger.Zero, r.TreatyPremiumGel);
    }

    [Fact]
    public async Task The_two_imported_policies_approved_October_two_policies_written_1600_earned_135_89_UPR_1464_11_owed_1600_with_300_overdue_IBNR_80_pending_nothing_September_empty() =>
        await OwnDatabaseAsync(async w =>
        {
            var october = new DateOnly(2026, 10, 1);
            var i = await ImportAsync(w, Georgian, october);
            // Pending: no premium is recognised — September and October empty.
            Empty(await MonthReportAsync(w, october));
            Empty(await MonthReportAsync(w, october.AddMonths(-1)));
            await ApproveBothAsync(w, i);
            // By hand (tetri), 31 of 365 days per premium row: 7644 + 2548 + 2548 + 849 = 13589; UPR 160000 − 13589 = 146411.
            // Owed at 31.10, no receipt: 160000; due by then the first's 20000 and the second's 10000 (both 01.10): 30000.
            var before = await LedgerAsync(w);
            var r = await MonthReportAsync(w, october);
            Premium(r, 2, 160000, 13589, 146411, 8000);
            Assert.Equal([new PremiumReport.ReceivableRow("GEL", 160000, 30000, 160000, 1m, new DateOnly(2026, 10, 31))], r.Receivables);
            Empty(await MonthReportAsync(w, october.AddMonths(-1)));
            Assert.Equal(before, await LedgerAsync(w));
            // The raw rows: four premium rows, each on its own line, booked 01.10, cover a year, by the head; both active.
            Assert.Equal([(i.First, 900m, october), (i.First, 300m, october), (i.Second, 300m, october), (i.Second, 100m, october)],
                await w.QueryAsync<(Guid, decimal, DateOnly)>("""
                    select l.policy_id, t.amount, t.booked_on from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id
                    where t.created_by = @head and t.cover_from = @october and t.cover_to = @to order by case when l.policy_id = @first then 0 else 1 end, t.amount desc
                    """, new { head = i.Head.Id, october, to = october.AddYears(1), first = i.First }));
            Assert.Equal(["active", "active"], await w.QueryAsync<string>("select status from core.policy where id in @ids and approved_by = @head", new { ids = new[] { i.First, i.Second }, head = i.Head.Id }));
            Assert.Equal(0, await w.ScalarAsync<int>("select (select count(*) from core.payment) + (select count(*) from core.installment_adjustment) + (select count(*) from core.premium_cession)"));
        });

    [Fact]
    public async Task A_receipt_on_each_imported_policy_keeps_to_its_own_policy_owed_1300_nothing_overdue() =>
        await OwnDatabaseAsync(async w =>
        {
            var from = MonthStart;
            var today = World.Today;
            var i = await ImportAsync(w, Georgian, from);
            await ApproveBothAsync(w, i);
            await DecideAsync(w, i.Underwriter.Id, "premium.receipt.create", "allow");
            var month = await MonthReportAsync(w, from);
            Assert.Equal([new PremiumReport.ReceivableRow("GEL", 160000, 30000, 160000, 1m, MonthEnd(from))], month.Receivables);
            Assert.Equal($"/policies/{i.First}?paid=0", (await w.PayAsync(i.Underwriter, i.First, "200.00", today, reference: "IMPORT-PAY-0001")).Redirect);
            Assert.Equal($"/policies/{i.Second}?paid=0", (await w.PayAsync(i.Underwriter, i.Second, "100.00", today, reference: "IMPORT-PAY-0002")).Redirect);
            // By hand: FIFO on each policy's own schedule — 200.00 on the first's start instalment, 100.00 on the second's.
            var after = await MonthReportAsync(w, from);
            Assert.Equal([new PremiumReport.ReceivableRow("GEL", 130000, 0, 130000, 1m, MonthEnd(from))], after.Receivables);
            Assert.Equal(month.Totals, after.Totals);
            Assert.Equal([("IMPORT-PAY-0001", 200m, i.First, from, 200m, today), ("IMPORT-PAY-0002", 100m, i.Second, from, 100m, today)],
                await w.QueryAsync<(string, decimal, Guid, DateOnly, decimal, DateOnly)>("""
                    select p.reference, p.amount_gel, i.policy_id, i.due_on, a.amount, a.booked_on from core.payment p
                    join core.payment_allocation a on a.payment_id = p.id join core.installment i on i.id = a.installment_id order by p.reference
                    """));
            Assert.Equal((1000m, 300m), (await w.OwedAsync(i.First), await w.OwedAsync(i.Second)));
        });

    [Fact]
    public async Task A_receipts_excess_attributed_to_the_other_imported_policy_then_the_shared_receipt_reversed() =>
        await OwnDatabaseAsync(async w =>
        {
            var from = MonthStart;
            var today = World.Today;
            var to = MonthEnd(from);
            var i = await ImportAsync(w, Georgian, from);
            await ApproveBothAsync(w, i);
            await DecideAsync(w, i.Underwriter.Id, "premium.receipt.create", "allow");
            var premium = (await MonthReportAsync(w, from)).Totals;
            // 1 300.00 on the first policy (owing 1 200.00): FIFO settles its whole schedule, 100.00 stays on the receipt.
            Assert.Equal($"/policies/{i.First}?paid=10000", (await w.PayAsync(i.Underwriter, i.First, "1300.00", today, reference: "IMPORT-PAY-1300")).Redirect);
            var receipt = await w.ScalarAsync<Guid>("select id from core.payment where reference = N'IMPORT-PAY-1300'");
            Assert.Equal([(from, 200m), .. Enumerable.Range(1, 10).Select(k => (from.AddMonths(k), 100m))], await w.QueryAsync<(DateOnly, decimal)>("""
                select i.due_on, a.amount from core.payment_allocation a join core.installment i on i.id = a.installment_id where a.payment_id = @receipt order by i.due_on
                """, new { receipt }));
            var advance = Assert.Single(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, i.Holder)));
            Assert.Equal((receipt, today, 1300m, 100m, "bank", "IMPORT-PAY-1300"), (advance.Id, advance.ReceivedOn, advance.AmountGel, advance.AvailableGel, advance.Method, advance.Reference));
            // Owed 160000 − 120000 = 40000; the second policy's start instalment (10000) due and unpaid.
            var r = await MonthReportAsync(w, from);
            Assert.Equal([new PremiumReport.ReceivableRow("GEL", 40000, 10000, 40000, 1m, to)], r.Receivables);
            Assert.Equal(premium, r.Totals);

            // The real AllocateReceipt: what is left of it settles the second policy's oldest unpaid instalment, booked today.
            Assert.Equal($"/policies/{i.Second}?advance=allocated&gel=10000&left=0",
                await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, i.Underwriter, i.Second.ToString(), receipt.ToString()));
            Assert.Equal([(from, 100m, 100m, 1m, today)], await w.QueryAsync<(DateOnly, decimal, decimal, decimal, DateOnly)>("""
                select i.due_on, a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.installment i on i.id = a.installment_id
                where a.payment_id = @receipt and i.policy_id = @second
                """, new { receipt, second = i.Second }));
            Assert.Equal(1300m, await w.ScalarAsync<decimal>("select sum(amount_gel) from core.payment_allocation where payment_id = @receipt", new { receipt }));
            Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, i.Holder)));
            Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment"));
            r = await MonthReportAsync(w, from);
            Assert.Equal([new PremiumReport.ReceivableRow("GEL", 30000, 0, 30000, 1m, to)], r.Receivables);
            Assert.Equal(premium, r.Totals);

            // The shared receipt reversed from the second policy's card (below the threshold: at once): a −1 300.00 mirror and a
            // mirror of each of its twelve allocations, booked today; everything it paid is owed again.
            var originals = await w.QueryAsync<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate)>(
                "select installment_id, amount, amount_gel, rate from core.payment_allocation where payment_id = @receipt", new { receipt });
            const string reason = "შეცდომით შეტანილი ქვითარი — ორი პოლისი";
            Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, i.Underwriter, receipt.ToString(), i.Second.ToString(), reason));
            var mirror = Assert.Single(await w.QueryAsync<(Guid Id, DateOnly ReceivedOn, decimal AmountGel, string Method, string Kind, Guid Payer, string Reference, Guid CreatedBy)>(
                "select id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by from core.payment where reverses_payment_id = @receipt", new { receipt }));
            Assert.Equal((today, -1300m, "bank", "reversal", i.Holder, reason, i.Underwriter.Id), (mirror.ReceivedOn, mirror.AmountGel, mirror.Method, mirror.Kind, mirror.Payer, mirror.Reference, mirror.CreatedBy));
            var mirrors = await w.QueryAsync<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>(
                "select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @id", new { id = mirror.Id });
            Assert.Equal(originals.Select(o => (o.Installment, -o.Amount, -o.AmountGel, o.Rate, today)).OrderBy(x => x.Installment).ToList(), mirrors.OrderBy(x => x.Installment).ToList());
            Assert.Equal(12, mirrors.Count);
            r = await MonthReportAsync(w, from);
            // Owed 160000 again; due by the month's end and unpaid: the first's start (20000) and the second's start (10000).
            Assert.Equal([new PremiumReport.ReceivableRow("GEL", 160000, 30000, 160000, 1m, to)], r.Receivables);
            Assert.Equal(premium, r.Totals);
            Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, i.Holder)));
            // A second reversal adds nothing.
            var ledger = await LedgerAsync(w);
            Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, i.Underwriter, receipt.ToString(), i.Second.ToString(), reason));
            Assert.Equal(ledger, await LedgerAsync(w));
            Assert.Equal((2, 24), (await w.ScalarAsync<int>("select count(*) from core.payment"), await w.ScalarAsync<int>("select count(*) from core.payment_allocation")));
        });

    private static async Task UsdRatesAsync(World w, bool withFifteenth = true)
    {
        await w.RateAsync("USD", new DateOnly(2026, 10, 1), 2.7m);
        if (withFifteenth) await w.RateAsync("USD", new DateOnly(2026, 10, 15), 2.8m);
        await w.RateAsync("USD", new DateOnly(2026, 10, 20), 2.9m);
        await w.RateAsync("USD", new DateOnly(2026, 10, 31), 3.0m);
    }

    [Fact]
    public async Task Two_imported_USD_policies_at_their_own_start_rates_approved_October_written_4360_earned_327_33_owed_1600_USD_300_overdue_4800_at_3() =>
        await OwnDatabaseAsync(async w =>
        {
            await UsdRatesAsync(w);
            var october = new DateOnly(2026, 10, 1);
            var i = await ImportAsync(w, UsdCsv, october);
            Assert.Equal([(i.First, "USD", 2.7m, 900m, 2430m), (i.First, "USD", 2.7m, 300m, 810m), (i.Second, "USD", 2.8m, 300m, 840m), (i.Second, "USD", 2.8m, 100m, 280m)],
                await w.QueryAsync<(Guid, string, decimal, decimal, decimal)>("""
                    select p.id, p.premium_currency, p.premium_rate, l.premium, l.premium_gel from core.policy_line l join core.policy p on p.id = l.policy_id
                    where p.id in @ids order by p.effective_from, l.premium desc
                    """, new { ids = new[] { i.First, i.Second } }));
            Assert.Equal([(new DateOnly(2026, 10, 15), 100m), (new DateOnly(2027, 1, 15), 100m), (new DateOnly(2027, 4, 15), 100m), (new DateOnly(2027, 7, 15), 100m)],
                await w.QueryAsync<(DateOnly, decimal)>("select due_on, amount from core.installment where policy_id = @id order by due_on", new { id = i.Second }));
            Empty(await MonthReportAsync(w, october));
            await ApproveBothAsync(w, i);
            // By hand (tetri): written 243000 + 81000 + 84000 + 28000 = 436000; earned 20638 + 6879 (31/365) + 3912 + 1304 (17/365) = 32733.
            // Owed 160000 cents USD, 30000 overdue (01.10's 200, 15.10's 100); at 31.10's 3.00: 480000. IBNR 5 % of 4 360.00.
            var r = await MonthReportAsync(w, october);
            Premium(r, 2, 436000, 32733, 403267, 21800);
            Assert.Equal([new PremiumReport.ReceivableRow("USD", 160000, 30000, 480000, 3m, new DateOnly(2026, 10, 31))], r.Receivables);
            Empty(await MonthReportAsync(w, october.AddMonths(-1)));
            Assert.Equal([(900m, 2430m, october), (300m, 810m, october), (300m, 840m, new DateOnly(2026, 10, 15)), (100m, 280m, new DateOnly(2026, 10, 15))],
                await w.QueryAsync<(decimal, decimal, DateOnly)>("""
                    select t.amount, t.amount_gel, t.booked_on from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id order by t.booked_on, t.amount desc
                    """));
        });

    [Fact]
    public async Task The_second_imported_USD_policys_start_rate_is_fetched_280_per_100_and_written_2_8_not_2_7() =>
        await OwnDatabaseAsync(async w =>
        {
            await UsdRatesAsync(w, withFifteenth: false);
            var nbg = new OneDayNbg("2026-10-15", ("USD", 100m, 280m), ("EUR", 1m, 3.1m));
            var i = await ImportAsync(w, UsdCsv, new DateOnly(2026, 10, 1), new Rates(w.App, nbg));
            // The bank asked once, for 15.10 only; both of its rates stored.
            Assert.Equal([$"{Rates.NbgUrl}?currencies=USD&currencies=EUR&date=2026-10-15"], nbg.Requests);
            Assert.Equal([("EUR", 3.1m), ("USD", 2.8m)], await w.QueryAsync<(string, decimal)>("select currency, rate from core.exchange_rate where rate_date = '2026-10-15' order by currency"));
            Assert.Equal([(2.7m, 900m, 2430m), (2.7m, 300m, 810m), (2.8m, 300m, 840m), (2.8m, 100m, 280m)], await w.QueryAsync<(decimal, decimal, decimal)>("""
                select p.premium_rate, l.premium, l.premium_gel from core.policy_line l join core.policy p on p.id = l.policy_id where p.id in @ids order by p.effective_from, l.premium desc
                """, new { ids = new[] { i.First, i.Second } }));
            Assert.Equal(436000m, await w.ScalarAsync<decimal>("select sum(premium_gel) * 100 from core.policy_line"));
            Assert.Equal(0, await w.ScalarAsync<int>("select (select count(*) from core.premium_transaction) + (select count(*) from core.payment) + (select count(*) from core.premium_cession)"));
        });

    [Fact]
    public async Task A_GEL_receipt_on_an_imported_USD_policy_refused_while_its_days_rate_is_missing_then_registered_once_the_bank_answers_200_USD_at_3()
    {
        // The legacy case received the 600.00 on 07.11.2026 and registered it on 10.11.2026; here it is received yesterday and
        // registered today (the receipt's own day is the rate's day either way). The bank first answers 503, then 300 / 100.
        var day = World.Today.AddDays(-1);
        var nbg = new OneDayNbg(Iso(day), ("USD", 100m, 300m), ("EUR", 1m, 3.3m)) { Failures = 1 };
        var rates = new Rates(w.App, nbg);
        var from = MonthStart.AddMonths(-1);
        await w.RateAsync("USD", from, 2.7m);
        var accountant = await w.UserAsync("accountant");
        var admin = await w.UserAsync();
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), from, "USD", 2.7m, 1200m);
        var form = World.Form(("policy_id", p.Id.ToString()), ("amount_gel", "600.00"), ("received_on", Iso(day)), ("method", "bank"), ("reference", "USD-LATE"));
        var refused = await PaymentActions.RecordPaymentAsync(w.App, rates, accountant, form);
        Assert.Equal(Insurance.Application.Texts.BillingTexts.Payment.Errors.Rate, refused.Errors["received_on"]);
        Assert.Equal(0m, await w.PaidAsync(p.Id));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.exchange_rate where rate_date = @day", new { day }));
        var sent = await PaymentActions.RecordPaymentAsync(w.App, rates, accountant, form);
        Assert.Equal($"/policies/{p.Id}?paid=0", sent.Redirect);
        Assert.Equal(2, nbg.Requests.Count);
        // 600.00 GEL at 3.00 = 200.00 USD, oldest instalment first (300.00 due on the start), booked today.
        Assert.Equal([(200m, 600m, 3m, World.Today)], await w.QueryAsync<(decimal, decimal, decimal, DateOnly)>("""
            select a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.payment r on r.id = a.payment_id where r.reference = N'USD-LATE'
            """));
        Assert.Equal(1000m, await w.OwedAsync(p.Id));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where reference = N'USD-LATE'"));
    }

    [Fact]
    public async Task A_payload_previewed_before_October_closed_is_refused_on_confirmation_with_nothing_written() =>
        await OwnDatabaseAsync(async w =>
        {
            var underwriter = await w.UserAsync("underwriter");
            var taxId = TaxId();
            var holder = await w.ClientAsync(taxId);
            var form = World.Form(("policyholder_id_number", taxId), ("effective_from", "2026-10-01"), ("effective_to", "2027-10-01"), ("currency", "GEL"), ("installments", "1"));
            var preview = await Import.PreviewAsync(w.App, underwriter, form, Encoding.UTF8.GetBytes(Georgian));
            Assert.NotNull(preview.Payload);
            Assert.All(preview.Rows!, r => Assert.Empty(r.Errors));
            await w.CloseMonthAsync(new DateOnly(2026, 9, 1), underwriter.Id);
            await w.CloseMonthAsync(new DateOnly(2026, 10, 1), underwriter.Id);
            var before = await LedgerAsync(w);
            var numbers = await w.ScalarAsync<string>("select string_agg(concat(code, '=', next_value), ',') within group (order by code) from core.number_sequence");
            Assert.Equal(Insurance.Application.Texts.PolicyTexts.Import.Errors.PeriodClosed, (await Import.ConfirmAsync(w.App, w.Rates, underwriter, preview.Payload, preview)).Message);
            Assert.Equal(before, await LedgerAsync(w));
            Assert.Equal(numbers, await w.ScalarAsync<string>("select string_agg(concat(code, '=', next_value), ',') within group (order by code) from core.number_sequence"));
            Assert.Equal(0, await w.ScalarAsync<int>("select (select count(*) from core.contract where policyholder_id = @holder) + (select count(*) from core.insured_object)", new { holder }));
        });

    // ================================================================ the legacy days, on the database's clock (DbOptions.Clock)

    /// <summary>The whole month of the two GEL policies (the legacy month(…) helper): premium, IBNR 80.00 and one GEL receivable at the month's end.</summary>
    private static void GelMonth(PremiumReport.Report r, DateOnly from, int issued, BigInteger written, BigInteger earned, BigInteger unearned, BigInteger outstanding, BigInteger overdue)
    {
        Assert.Equal((Month(from), from, MonthEnd(from)), (r.Month, r.From, r.To));
        Premium(r, issued, written, earned, unearned, 8000);
        Assert.Equal([new PremiumReport.ReceivableRow("GEL", outstanding, overdue, outstanding, 1m, MonthEnd(from))], r.Receivables);
    }

    /// <summary>The legacy chain on its days: imported and both approved on 01.10.2026, 1 300.00 on the first policy received and entered 07.10.2026.</summary>
    private static async Task<(Imported I, Guid Receipt)> Legacy1300Async(World w)
    {
        On(w, D(2026, 10, 1));
        var i = await ImportAsync(w, Georgian, D(2026, 10, 1));
        await ApproveBothAsync(w, i);
        await DecideAsync(w, i.Underwriter.Id, "premium.receipt.create", "allow");
        On(w, D(2026, 10, 7));
        Assert.Equal($"/policies/{i.First}?paid=10000", (await w.PayAsync(i.Underwriter, i.First, "1300.00", D(2026, 10, 7), reference: "IMPORT-PAY-1300")).Redirect);
        return (i, await w.ScalarAsync<Guid>("select id from core.payment where reference = N'IMPORT-PAY-1300'"));
    }

    [Fact]
    public async Task The_1300_receipts_100_left_allocated_to_the_second_policy_on_07_11_booked_07_11_October_as_reported_November_owes_300_nothing_overdue() =>
        await OwnDatabaseAsync(async w =>
        {
            var (i, receipt) = await Legacy1300Async(w);
            // October after the receipt, by hand (tetri): 2 policies, 160000, earned 13589, UPR 146411, IBNR 8000; owed 160000 − 120000
            // = 40000, the second policy's 01.10 instalment (10000) overdue. September empty.
            var october = await MonthReportAsync(w, D(2026, 10, 1));
            GelMonth(october, D(2026, 10, 1), 2, 160000, 13589, 146411, 40000, 10000);
            Empty(await MonthReportAsync(w, D(2026, 9, 1)));
            var advance = Assert.Single(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, i.Holder)));
            Assert.Equal((receipt, D(2026, 10, 7), 1300m, 100m), (advance.Id, advance.ReceivedOn, advance.AmountGel, advance.AvailableGel));
            // The real AllocateReceipt on 07.11.2026: the 100.00 left goes to the second policy's oldest unpaid instalment.
            On(w, D(2026, 11, 7));
            Assert.Equal($"/policies/{i.Second}?advance=allocated&gel=10000&left=0",
                await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, i.Underwriter, i.Second.ToString(), receipt.ToString()));
            var before = await LedgerAsync(w);
            var octoberAfter = await MonthReportAsync(w, D(2026, 10, 1));
            var november = await MonthReportAsync(w, D(2026, 11, 1));
            // October exactly as reported. November (61 days by 30.11): earned per row 15041 + 5014 + 5014 + 1671 = 26740 − 13589 = 13151,
            // UPR 160000 − 26740 = 133260; owed 160000 − 130000 = 30000; the second's 01.10 now paid, its next due 01.01 — nothing overdue.
            Assert.Equal(Shape(october), Shape(octoberAfter));
            GelMonth(november, D(2026, 11, 1), 0, 0, 13151, 133260, 30000, 0);
            Assert.Equal(before, await LedgerAsync(w));
            // The raw rows: eleven allocations on the first policy booked 07.10, one of 100.00 on the second's 01.10 booked 07.11; nothing left.
            Assert.Equal([(i.First, D(2026, 10, 1), 200m, D(2026, 10, 7)), .. Enumerable.Range(1, 10).Select(k => (i.First, D(2026, 10, 1).AddMonths(k), 100m, D(2026, 10, 7))),
                    (i.Second, D(2026, 10, 1), 100m, D(2026, 11, 7))],
                await w.QueryAsync<(Guid, DateOnly, decimal, DateOnly)>("""
                    select i.policy_id, i.due_on, a.amount, a.booked_on from core.payment_allocation a join core.installment i on i.id = a.installment_id
                    where a.payment_id = @receipt order by a.booked_on, i.due_on
                    """, new { receipt }));
            Assert.Equal([(D(2026, 10, 7), 1300m)], await w.QueryAsync<(DateOnly, decimal)>("select received_on, amount_gel from core.payment"));
            Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, i.Holder)));
        });

    [Fact]
    public async Task The_shared_1300_receipt_reversed_on_10_11_a_mirror_of_it_and_its_twelve_lines_booked_10_11_October_as_reported_November_owes_1600_with_400_overdue() =>
        await OwnDatabaseAsync(async w =>
        {
            var (i, receipt) = await Legacy1300Async(w);
            On(w, D(2026, 11, 7));
            Assert.Equal($"/policies/{i.Second}?advance=allocated&gel=10000&left=0",
                await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, i.Underwriter, i.Second.ToString(), receipt.ToString()));
            // Before the reversal, as accepted: October 40000 / 10000; November 30000 / 0; one receipt, twelve allocations, 1 300.00.
            var october = await MonthReportAsync(w, D(2026, 10, 1));
            GelMonth(october, D(2026, 10, 1), 2, 160000, 13589, 146411, 40000, 10000);
            GelMonth(await MonthReportAsync(w, D(2026, 11, 1)), D(2026, 11, 1), 0, 0, 13151, 133260, 30000, 0);
            var originals = await w.QueryAsync<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate)>(
                "select installment_id, amount, amount_gel, rate from core.payment_allocation where payment_id = @receipt", new { receipt });
            Assert.Equal((12, 1300m), (originals.Count, originals.Sum(o => o.AmountGel)));
            // The real reversePayment on 10.11.2026, from the second policy's card.
            On(w, D(2026, 11, 10));
            const string reason = "შეცდომით შეტანილი ქვითარი — ორი პოლისი";
            Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, i.Underwriter, receipt.ToString(), i.Second.ToString(), reason));
            var before = await LedgerAsync(w);
            // By hand: October unchanged; November owes everything again, 160000; due by 30.11 and unpaid: the first's 01.10 (20000) and
            // 01.11 (10000), the second's 01.10 (10000) — overdue 40000; earned 13151, UPR 133260, IBNR 8000 as before. September empty.
            Assert.Equal(Shape(october), Shape(await MonthReportAsync(w, D(2026, 10, 1))));
            GelMonth(await MonthReportAsync(w, D(2026, 11, 1)), D(2026, 11, 1), 0, 0, 13151, 133260, 160000, 40000);
            Empty(await MonthReportAsync(w, D(2026, 9, 1)));
            Assert.Equal(before, await LedgerAsync(w));
            // The raw rows: the mirror −1 300.00 dated 10.11 and a mirror of each of the twelve allocations, booked 10.11, sign opposite.
            var mirror = Assert.Single(await w.QueryAsync<(Guid Id, DateOnly ReceivedOn, decimal AmountGel, string Method, string Kind, Guid Payer, string Reference, Guid CreatedBy)>(
                "select id, received_on, amount_gel, method, kind, payer_party_id, reference, created_by from core.payment where reverses_payment_id = @receipt", new { receipt }));
            Assert.Equal((D(2026, 11, 10), -1300m, "bank", "reversal", i.Holder, reason, i.Underwriter.Id), (mirror.ReceivedOn, mirror.AmountGel, mirror.Method, mirror.Kind, mirror.Payer, mirror.Reference, mirror.CreatedBy));
            var mirrors = await w.QueryAsync<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>(
                "select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @id", new { id = mirror.Id });
            Assert.Equal(originals.Select(o => (o.Installment, -o.Amount, -o.AmountGel, o.Rate, D(2026, 11, 10))).OrderBy(x => x.Installment).ToList(), mirrors.OrderBy(x => x.Installment).ToList());
            Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, i.Holder)));
            // A second reversal adds nothing.
            Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, i.Underwriter, receipt.ToString(), i.Second.ToString(), reason));
            Assert.Equal(before, await LedgerAsync(w));
            Assert.Equal((2, 24), (await w.ScalarAsync<int>("select count(*) from core.payment"), await w.ScalarAsync<int>("select count(*) from core.payment_allocation")));
        });

    /// <summary>
    /// The USD chain on its legacy days: September closed by the real close (accountant, 20.10.2026), the two USD policies imported by
    /// the underwriter on 20.10.2026 with 15.10's rate fetched from the bank (280 / 100), both approved on 20.10.2026. Stored: 01.10 2.70,
    /// 20.10 2.90, 31.10 3.00, 30.11 3.50 and <paramref name="extra"/>.
    /// </summary>
    private static async Task<(Imported I, OneDayNbg Nbg)> LegacyUsdAsync(World w, params (DateOnly Day, decimal Rate)[] extra)
    {
        await w.RateAsync("USD", D(2026, 10, 1), 2.7m);
        await w.RateAsync("USD", D(2026, 10, 20), 2.9m);
        await w.RateAsync("USD", D(2026, 10, 31), 3.0m);
        await w.RateAsync("USD", D(2026, 11, 30), 3.5m);
        foreach (var (day, rate) in extra) await w.RateAsync("USD", day, rate);
        On(w, D(2026, 10, 20));
        var accountant = await w.UserAsync("accountant");
        Assert.Equal(new PeriodClose.Answer("done", "2026-09-01"), await PeriodClose.CloseAsync(w.App, accountant, "2026-09-01"));
        var nbg = new OneDayNbg("2026-10-15", ("USD", 100m, 280m), ("EUR", 1m, 3.1m));
        var i = await ImportAsync(w, UsdCsv, D(2026, 10, 1), new Rates(w.App, nbg));
        await ApproveBothAsync(w, i);
        await DecideAsync(w, i.Underwriter.Id, "premium.receipt.create", "allow");
        Assert.Equal([$"{Rates.NbgUrl}?currencies=USD&currencies=EUR&date=2026-10-15"], nbg.Requests);
        return (i, nbg);
    }

    /// <summary>October of the two USD policies, by hand (tetri): written 436000, earned 20638 + 6879 + 3912 + 1304 = 32733, UPR 403267; owed 160000 cents USD, 30000 overdue, at 3.00 480000; IBNR 21800.</summary>
    private static void UsdOctober(PremiumReport.Report r)
    {
        Assert.Equal(("2026-10", D(2026, 10, 1), D(2026, 10, 31)), (r.Month, r.From, r.To));
        Premium(r, 2, 436000, 32733, 403267, 21800);
        Assert.Equal([new PremiumReport.ReceivableRow("USD", 160000, 30000, 480000, 3m, D(2026, 10, 31))], r.Receivables);
    }

    /// <summary>November of the two USD policies, by hand (tetri): earned by 30.11 per row 40611 + 13537 + 10816 + 3605 = 68569 − 32733 = 35836, UPR 367431; IBNR 21800; the receivable at 30.11's 3.50.</summary>
    private static void UsdNovember(PremiumReport.Report r, BigInteger outstanding, BigInteger overdue, BigInteger outstandingGel)
    {
        Assert.Equal(("2026-11", D(2026, 11, 1), D(2026, 11, 30)), (r.Month, r.From, r.To));
        Premium(r, 0, 0, 35836, 367431, 21800);
        Assert.Equal([new PremiumReport.ReceivableRow("USD", outstanding, overdue, outstandingGel, 3.5m, D(2026, 11, 30))], r.Receivables);
    }

    [Fact]
    public async Task A_late_GEL_receipt_600_received_31_10_registered_07_11_on_the_imported_USD_policy_200_USD_at_31_10s_3_booked_07_11_October_as_reported_November_1400_USD_200_overdue_4900_at_3_5() =>
        await OwnDatabaseAsync(async w =>
        {
            var (i, nbg) = await LegacyUsdAsync(w, (D(2026, 11, 7), 3.2m));
            var octoberBefore = await MonthReportAsync(w, D(2026, 10, 1));
            UsdOctober(octoberBefore);
            // The real recordPayment on 07.11.2026: 600.00 GEL by bank, received 31.10, on the first policy.
            On(w, D(2026, 11, 7));
            Assert.Equal($"/policies/{i.First}?paid=0", (await PaymentActions.RecordPaymentAsync(w.App, new Rates(w.App, nbg), i.Underwriter, World.Form(
                ("policy_id", i.First.ToString()), ("amount_gel", "600.00"), ("received_on", "2026-10-31"), ("method", "bank"), ("reference", "IMPORT-USD-600")))).Redirect);
            // By hand: 31.10's 3.00 — 600.00 / 3.00 = 200.00 USD on the first policy's 01.10 instalment of 200.00, booked 07.11; not 07.11's 3.20,
            // not 30.11's 3.50, not the premium's 2.70. Nothing left over, nothing on the second policy; the bank not asked again.
            var receipt = Assert.Single(await w.QueryAsync<(Guid Id, string Kind, DateOnly ReceivedOn, decimal AmountGel, string Method, Guid Payer, string Reference, Guid CreatedBy, Guid? Reverses)>(
                "select id, kind, received_on, amount_gel, method, payer_party_id, reference, created_by, reverses_payment_id from core.payment"));
            Assert.Equal(("receipt", D(2026, 10, 31), 600m, "bank", i.Holder, "IMPORT-USD-600", i.Underwriter.Id, (Guid?)null),
                (receipt.Kind, receipt.ReceivedOn, receipt.AmountGel, receipt.Method, receipt.Payer, receipt.Reference, receipt.CreatedBy, receipt.Reverses));
            Assert.Equal([(receipt.Id, i.First, D(2026, 10, 1), 200m, 600m, 3m, D(2026, 11, 7))], await w.QueryAsync<(Guid, Guid, DateOnly, decimal, decimal, decimal, DateOnly)>("""
                select a.payment_id, i.policy_id, i.due_on, a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.installment i on i.id = a.installment_id
                """));
            Assert.Single(nbg.Requests);
            Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, i.Holder)));
            // The premium rows and the policies as approved.
            Assert.Equal([(900m, 2430m, D(2026, 10, 1)), (300m, 810m, D(2026, 10, 1)), (300m, 840m, D(2026, 10, 15)), (100m, 280m, D(2026, 10, 15))],
                await w.QueryAsync<(decimal, decimal, DateOnly)>("select amount, amount_gel, booked_on from core.premium_transaction order by booked_on, amount desc"));
            var before = await LedgerAsync(w);
            // The reports: October exactly as before (the allocation is booked 07.11), September empty, November owes 160000 − 20000 = 140000
            // cents USD; due by 30.11 unpaid: the first's 01.11 100 and the second's 15.10 100 — 20000; at 3.50 490000.
            var octoberAfter = await MonthReportAsync(w, D(2026, 10, 1));
            Assert.Equal(Shape(octoberBefore), Shape(octoberAfter));
            Empty(await MonthReportAsync(w, D(2026, 9, 1)));
            UsdNovember(await MonthReportAsync(w, D(2026, 11, 1)), 140000, 20000, 490000);
            Assert.Equal(before, await LedgerAsync(w));
        });

    [Fact]
    public async Task A_GEL_receipt_600_received_07_11_refused_on_10_11_bank_503_then_sent_again_bank_300_per_100_one_receipt_200_USD_at_3_booked_10_11_November_1400_USD_200_overdue_4900() =>
        await OwnDatabaseAsync(async w =>
        {
            var (i, _) = await LegacyUsdAsync(w, (D(2026, 11, 10), 3.2m));
            Assert.Equal(7, await w.ScalarAsync<int>("select count(*) from core.exchange_rate"));
            Empty(await MonthReportAsync(w, D(2026, 9, 1)));
            UsdOctober(await MonthReportAsync(w, D(2026, 10, 1)));
            UsdNovember(await MonthReportAsync(w, D(2026, 11, 1)), 160000, 40000, 560000);
            var form = World.Form(("policy_id", i.First.ToString()), ("amount_gel", "600.00"), ("received_on", "2026-11-07"), ("method", "bank"), ("reference", "IMPORT-USD-RETRY"));
            // First attempt on 10.11.2026: 07.11 is not stored and the bank answers 503 — the received-on field's message, nothing written.
            On(w, D(2026, 11, 10));
            var bank = new OneDayNbg("2026-11-07", ("USD", 100m, 300m), ("EUR", 1m, 3.1m)) { Failures = 1 };
            var ledger = await LedgerAsync(w);
            var refused = await PaymentActions.RecordPaymentAsync(w.App, new Rates(w.App, bank), i.Underwriter, form);
            Assert.Null(refused.Redirect);
            Assert.Equal([("received_on", Insurance.Application.Texts.BillingTexts.Payment.Errors.Rate)], refused.Errors.Select(e => (e.Key, e.Value)));
            Assert.Equal(ledger, await LedgerAsync(w));
            Assert.Equal(7, await w.ScalarAsync<int>("select count(*) from core.exchange_rate"));
            Assert.Equal([$"{Rates.NbgUrl}?currencies=USD&currencies=EUR&date=2026-11-07"], bank.Requests);
            UsdOctober(await MonthReportAsync(w, D(2026, 10, 1)));
            UsdNovember(await MonthReportAsync(w, D(2026, 11, 1)), 160000, 40000, 560000);
            // Second attempt, the same form, the same day: the bank answers USD 300 per 100 — 3.000000 stored with EUR 3.10, read again;
            // 600.00 / 3.00 = 200.00 USD on the first policy's 01.10 instalment, booked 10.11 (not 10.11's 3.20).
            Assert.Equal($"/policies/{i.First}?paid=0", (await PaymentActions.RecordPaymentAsync(w.App, new Rates(w.App, bank), i.Underwriter, form)).Redirect);
            Assert.Equal(2, bank.Requests.Count);
            Assert.Equal([("EUR", 3.1m), ("USD", 3m)], await w.QueryAsync<(string, decimal)>("select currency, rate from core.exchange_rate where rate_date = '2026-11-07' order by currency"));
            Assert.Equal(9, await w.ScalarAsync<int>("select count(*) from core.exchange_rate"));
            var receipt = Assert.Single(await w.QueryAsync<(Guid Id, DateOnly ReceivedOn, decimal AmountGel, string Reference)>("select id, received_on, amount_gel, reference from core.payment"));
            Assert.Equal((D(2026, 11, 7), 600m, "IMPORT-USD-RETRY"), (receipt.ReceivedOn, receipt.AmountGel, receipt.Reference));
            Assert.Equal([(i.First, D(2026, 10, 1), 200m, 600m, 3m, D(2026, 11, 10))], await w.QueryAsync<(Guid, DateOnly, decimal, decimal, decimal, DateOnly)>("""
                select i.policy_id, i.due_on, a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.installment i on i.id = a.installment_id
                """));
            Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, i.Holder)));
            // The reports: September empty, October as before, November owes 1 400 USD — 200 overdue (the first's 01.11, the second's 15.10) — 4 900.00 at 3.50.
            Empty(await MonthReportAsync(w, D(2026, 9, 1)));
            UsdOctober(await MonthReportAsync(w, D(2026, 10, 1)));
            UsdNovember(await MonthReportAsync(w, D(2026, 11, 1)), 140000, 20000, 490000);
        });
}
