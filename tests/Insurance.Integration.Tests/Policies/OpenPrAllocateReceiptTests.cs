using Insurance.Application.Services;
using Insurance.Integration.Tests.FailureInjection;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// allocateReceipt scenarios of the old program's open PRs #156, #157, #158, #163 and #165 (scripts/integration/
/// advance-active-suspension, advance-targeted-suspension, advance-legacy-bank-reference, advance-usd-two-line-cents and
/// advance-first-insert-refusal .test.ts), ported to PaymentActions.AllocateReceiptAsync on a real database. The legacy
/// world: a USD policy from 01.01.2027 with two instalments of 100.00 USD (due 01.01 and 15.01), the client's own receipt of
/// 405.00 GEL received 20.12.2026 and not yet allocated; USD 2.7 on 20.12.2026 and 3.2 on 31.01.2027; the accountant
/// allocates on 31.01.2027 (owner, 05.10.2026, H21: the receipt keeps its date and its day's rate, the allocation is booked
/// today, the oldest instalment first). #155 (no rate on the receipt's day) is <see cref="OpenPrNoRateTests"/>; #165's
/// closed-month refusal is <see cref="OpenPrPeriodClosedTests"/>.
/// </summary>
public sealed class OpenPrAllocateReceiptTests : IClassFixture<TestDatabase>
{
    private readonly OpenPrWorld x;
    private World w => x.W;

    public OpenPrAllocateReceiptTests(TestDatabase db)
    {
        x = new OpenPrWorld(db);
        x.OnLegacyToday();
    }

    private Task<string> Allocate(CurrentUser user, World.Policy p, Guid receipt) =>
        PaymentActions.AllocateReceiptAsync(x.App, x.Rates, user, p.Id.ToString(), receipt.ToString());

    /// <summary>The legacy world for one client: the policy (two × 100.00 USD), its receipt (405.00 GEL, 20.12.2026) and the accountant.</summary>
    private async Task<(CurrentUser Accountant, Guid Holder, World.Policy P, Guid Receipt)> AdvanceAsync(string? reference = null)
    {
        await x.RatesAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var p = await x.UsdPolicyAsync(holder, (0, 100m), (14, 100m));
        var receipt = await w.ReceiptAsync(holder, OpenPrWorld.Received, 405m, reference: reference);
        return (accountant, holder, p, receipt);
    }

    [Fact]
    public async Task PR156_an_active_suspension_of_all_the_clients_operations_refuses_the_allocation_before_any_rate_or_schedule_the_receipt_kept_whole()
    {
        var (accountant, holder, p, receipt) = await AdvanceAsync();
        var admin = await w.UserAsync();
        var suspension = await x.SuspendAsync(holder, admin.Id);
        Assert.Equal("active", await x.SuspensionStateAsync(holder, suspension));
        var ledger = await LedgerAsync(w);
        var row = await x.ReceiptRowAsync(receipt);
        x.Faults.ClearLog();

        Assert.Equal($"/policies/{p.Id}?advance=suspended", await Allocate(accountant, p, receipt));
        // The legacy five statements stop at the suspension: what is paid and the schedule are not read, no rate is asked, nothing is written.
        Assert.False(x.ScheduleRead);
        Assert.Empty(x.RateQuestions());
        Assert.False(x.Inserted);
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal(row, await x.ReceiptRowAsync(receipt));
        Assert.Equal((405m, 200m), (await w.AvailableAsync(receipt), await w.OwedAsync(p.Id)));
    }

    [Fact]
    public async Task PR157_a_suspension_over_policy_A_only_refuses_on_A_and_on_B_allocates_100_USD_270_and_50_USD_135_at_the_receipt_days_2_7()
    {
        await x.RatesAsync();
        var accountant = await w.UserAsync("accountant");
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var a = await x.UsdPolicyAsync(holder, (0, 100m), (14, 100m));
        var b = await x.UsdPolicyAsync(holder, (0, 100m), (14, 100m));
        Assert.NotEqual(a.ContractId, b.ContractId);
        var receipt = await w.ReceiptAsync(holder, OpenPrWorld.Received, 405m);
        var suspension = await x.SuspendAsync(holder, admin.Id, a.Id);
        Assert.Equal((a.Id, "active"), (await w.ScalarAsync<Guid>("select target_policy_id from core.fms_suspension where id = @suspension", new { suspension }),
            await x.SuspensionStateAsync(holder, suspension)));

        // On A: refused before anything is read for the money; nothing written.
        var ledger = await LedgerAsync(w);
        x.Faults.ClearLog();
        Assert.Equal($"/policies/{a.Id}?advance=suspended", await Allocate(accountant, a, receipt));
        Assert.Empty(x.RateQuestions());
        Assert.False(x.Inserted);
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal((405m, 200m, 200m), (await w.AvailableAsync(receipt), await w.OwedAsync(a.Id), await w.OwedAsync(b.Id)));

        // On B (not the suspension's target): 405.00 GEL / 2.7 = 150.00 USD, the oldest instalment first, booked today.
        x.Faults.ClearLog();
        Assert.Equal($"/policies/{b.Id}?advance=allocated&gel=40500&left=0", await Allocate(accountant, b, receipt));
        Assert.Equal(["USD 2026-12-20"], x.RateQuestions());
        var bi = await x.InstalmentsAsync(b);
        Assert.Equal([(bi[0], 100m, 270m, 2.7m, OpenPrWorld.Today), (bi[1], 50m, 135m, 2.7m, OpenPrWorld.Today)], await x.LinesAsync(receipt));
        Assert.Equal((0m, 200m, 50m), (await w.AvailableAsync(receipt), await w.OwedAsync(a.Id), await w.OwedAsync(b.Id)));
        Assert.Equal([100m, 50m], await x.SettledAsync(b));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder", new { holder }));
    }

    [Fact]
    public async Task PR158_an_old_suspension_carrying_only_PAY_77_blocks_the_receipt_referenced_pay_77_with_spaces_and_lets_PAY_78_allocate()
    {
        await x.RatesAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var p = await x.UsdPolicyAsync(holder, (0, 100m), (14, 100m));
        var blocked = await w.ReceiptAsync(holder, OpenPrWorld.Received, 405m, reference: " pay-77 ");
        var free = await w.ReceiptAsync(holder, OpenPrWorld.Received, 405m, reference: "PAY-78");
        var legacy = await x.LegacyUnmappedSuspensionAsync(holder, "PAY-77");
        Assert.Equal("active", await x.SuspensionStateAsync(holder, legacy));

        var ledger = await LedgerAsync(w);
        var row = await x.ReceiptRowAsync(blocked);
        x.Faults.ClearLog();
        Assert.Equal($"/policies/{p.Id}?advance=suspended", await Allocate(accountant, p, blocked));
        Assert.Empty(x.RateQuestions());
        Assert.False(x.Inserted);
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal(row, await x.ReceiptRowAsync(blocked));

        // The same old suspension does not match PAY-78 and is not widened to the client's other operations.
        var freeRow = await x.ReceiptRowAsync(free);
        x.Faults.ClearLog();
        Assert.Equal($"/policies/{p.Id}?advance=allocated&gel=40500&left=0", await Allocate(accountant, p, free));
        Assert.Equal(["USD 2026-12-20"], x.RateQuestions());
        var i = await x.InstalmentsAsync(p);
        Assert.Equal([(i[0], 100m, 270m, 2.7m, OpenPrWorld.Today), (i[1], 50m, 135m, 2.7m, OpenPrWorld.Today)], await x.LinesAsync(free));
        Assert.Equal(freeRow, await x.ReceiptRowAsync(free));
        Assert.Empty(await x.LinesAsync(blocked));
        Assert.Equal((405m, 0m, 50m), (await w.AvailableAsync(blocked), await w.AvailableAsync(free), await w.OwedAsync(p.Id)));
        // The suspension is as it was: still the old unmapped one, PAY-77, in force.
        Assert.Equal((true, "PAY-77", "active"), (await w.ScalarAsync<bool>("select legacy_unmapped from core.fms_suspension where id = @legacy", new { legacy }),
            await w.ScalarAsync<string>("select transaction_ref from core.fms_suspension where id = @legacy", new { legacy }), await x.SuspensionStateAsync(holder, legacy)));
    }

    [Fact]
    public async Task PR163_a_cents_advance_of_0_07_GEL_on_two_instalments_of_0_02_USD_is_0_02_for_0_05_and_0_01_for_the_rest_0_02()
    {
        // By hand (legacy): 7 tetri / 2.7 = 2.59 → 3 cents; the oldest instalment 0.02 USD × 2.7 = 5.4 → 0.05 GEL; the second
        // 0.01 USD takes what is left of the receipt, 0.07 − 0.05 = 0.02 GEL (alone it would be 2.7 → 0.03). 0.01 USD still owed.
        await x.RatesAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var p = await x.UsdPolicyAsync(holder, (0, 0.02m), (14, 0.02m));
        var receipt = await w.ReceiptAsync(holder, OpenPrWorld.Received, 0.07m);
        var row = await x.ReceiptRowAsync(receipt);
        x.Faults.ClearLog();
        Assert.Equal($"/policies/{p.Id}?advance=allocated&gel=7&left=0", await Allocate(accountant, p, receipt));
        Assert.Equal(["USD 2026-12-20"], x.RateQuestions());
        var i = await x.InstalmentsAsync(p);
        Assert.Equal([(i[0], 0.02m, 0.05m, 2.7m, OpenPrWorld.Today), (i[1], 0.01m, 0.02m, 2.7m, OpenPrWorld.Today)], await x.LinesAsync(receipt));
        Assert.Equal([0.02m, 0.01m], await x.SettledAsync(p));
        Assert.Equal((0m, 0.01m), (await w.AvailableAsync(receipt), await w.OwedAsync(p.Id)));
        Assert.Equal(row, await x.ReceiptRowAsync(receipt));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder", new { holder }));
    }

    [Fact]
    public async Task PR165_the_first_allocation_failing_with_another_error_comes_out_as_that_very_error_nothing_written_the_retry_allocates_once()
    {
        // #163's cents world; the first allocation insert (0.02 USD / 0.05 GEL) fails before it is written.
        await x.RatesAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var p = await x.UsdPolicyAsync(holder, (0, 0.02m), (14, 0.02m));
        var receipt = await w.ReceiptAsync(holder, OpenPrWorld.Received, 0.07m);
        var ledger = await LedgerAsync(w);
        var sentinel = new InjectedFault("PR165_FIRST_ALLOCATION");
        x.Faults.Reset();
        x.Faults.FailOnce(c => Faults.Has(c, "insert into core.payment_allocation") && Equals(c.Parameter("amount"), 0.02m), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => Allocate(accountant, p, receipt)));
        Assert.True(x.Faults.AllFired);
        Assert.Equal(1, x.Faults.Log.Count(c => Faults.Has(c, "insert into core.payment_allocation")));
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal((0.07m, 0.04m), (await w.AvailableAsync(receipt), await w.OwedAsync(p.Id)));
        // Nothing was half-written, so the same request allocates once.
        Assert.Equal($"/policies/{p.Id}?advance=allocated&gel=7&left=0", await Allocate(accountant, p, receipt));
        Assert.Equal(2, (await x.LinesAsync(receipt)).Count);
    }
}

/// <summary>
/// The old program's open PRs whose world has no USD rate at all (a database of its own, so no other test's rate is there):
/// #155 (allocateReceipt, scripts/integration/advance-usd-unavailable-receipt-rate.test.ts), #171 (transferOverpayment,
/// client-credit-unavailable-rate.test.ts) — no USD rate on the receipt's day 20.12.2026: ?advance=no-rate, nothing written,
/// and only the receipt's day asked, never today's — and the null-rate half of #146 (refundOverpayment,
/// refund-usd-rate-unavailable.test.ts): no USD rate for today 31.01.2027 → the error "No USD rate for 2027-01-31", nothing
/// written. The National Bank is unreachable: a rate not stored is "not published".
/// </summary>
public sealed class OpenPrNoRateTests : IClassFixture<TestDatabase>
{
    private readonly OpenPrWorld x;
    private World w => x.W;

    public OpenPrNoRateTests(TestDatabase db)
    {
        x = new OpenPrWorld(db);
        x.OnLegacyToday();
    }

    [Fact]
    public async Task PR155_allocateReceipt_with_no_USD_rate_on_the_receipts_day_answers_no_rate_asking_only_that_day_and_writes_nothing()
    {
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var p = await x.UsdPolicyAsync(holder, (0, 100m), (14, 100m));
        var receipt = await w.ReceiptAsync(holder, OpenPrWorld.Received, 405m);
        var ledger = await LedgerAsync(w);
        var row = await x.ReceiptRowAsync(receipt);
        x.Faults.ClearLog();
        x.Nbg.Requests.Clear();

        Assert.Equal($"/policies/{p.Id}?advance=no-rate", await PaymentActions.AllocateReceiptAsync(x.App, x.Rates, accountant, p.Id.ToString(), receipt.ToString()));
        // One rate question, USD on 20.12.2026 (the receipt's day, stored: none; the bank: none) — not today's.
        Assert.Equal(["USD 2026-12-20"], x.RateQuestions());
        Assert.Equal(["date=2026-12-20"], x.Nbg.Requests.Select(r => r[(r.IndexOf("date=", StringComparison.Ordinal))..]));
        Assert.False(x.Inserted);
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal(row, await x.ReceiptRowAsync(receipt));
        Assert.Equal((405m, 200m), (await w.AvailableAsync(receipt), await w.OwedAsync(p.Id)));
    }

    [Fact]
    public async Task PR171_transferOverpayment_onto_a_USD_target_with_no_rate_on_the_receipts_day_answers_no_rate_A_keeps_its_excess_B_its_debt()
    {
        var t = await OpenPrTransferTests.XferAsync(x);
        var ledger = await LedgerAsync(w);
        var lines = await x.LinesAsync(t.Receipt);
        x.Faults.ClearLog();
        x.Nbg.Requests.Clear();
        Assert.Equal($"/policies/{t.B.Id}?advance=no-rate", await PaymentActions.TransferOverpaymentAsync(x.App, x.Rates, t.Accountant, t.B.Id.ToString(), t.Receipt.ToString()));
        Assert.Equal(["USD 2026-12-20"], x.RateQuestions());
        Assert.Single(x.Nbg.Requests);
        Assert.False(x.Inserted);
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal(lines, await x.LinesAsync(t.Receipt));
        // A still holds 30.00 USD / 81.00 GEL beyond its 70.00; B still owes 20.00 USD; nothing free on the receipt.
        Assert.Equal((-30m, 20m, 0m), (await w.OwedAsync(t.A.Id), await w.OwedAsync(t.B.Id), await w.AvailableAsync(t.Receipt)));
        var held = Assert.Single(await w.App.ReadTxAsync(tx => Billing.LoadReleasableAsync(tx, t.Holder, OpenPrWorld.Today, t.B.Id)));
        Assert.Equal((t.A.Id, 30m, 81m), (held.PolicyId, held.Excess, held.ExcessGel));
    }

    [Fact]
    public async Task PR146_refundOverpayment_with_no_USD_rate_today_fails_with_No_USD_rate_for_2027_01_31_and_writes_nothing()
    {
        var r = await OpenPrRefundTests.OverpaidAsync(x);
        var ledger = await LedgerAsync(w);
        x.Faults.ClearLog();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentActions.RefundOverpaymentAsync(x.App, x.Rates, r.Accountant, r.A.Id.ToString(), "bank", null));
        Assert.Equal("No USD rate for 2027-01-31", error.Message);
        Assert.Equal(["USD 2027-01-31"], x.RateQuestions());
        Assert.False(x.Inserted);
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal(-184m, await w.OwedAsync(r.A.Id));
    }
}

/// <summary>
/// The closed-month refusal of the first write (the old program's open PRs #165 for allocateReceipt and #172 for
/// transferOverpayment, scripts/integration/advance-first-insert-refusal.test.ts and client-credit-first-insert-refusal.test.ts):
/// the database refuses it with 23514 → ?advance=period-closed, nothing written. The legacy tests made their fake store throw
/// 23514; here the real period guard refuses it: the accountant closes January 2027 (today's month, early) on another
/// connection just before the action's first allocation is sent. Each on a database of its own, as the month stays closed.
/// </summary>
public sealed class OpenPrPeriodClosedTests
{
    private static async Task CloseJanuaryBeforeFirstAllocationAsync(OpenPrWorld x, CurrentUser by)
    {
        x.Faults.Reset();
        x.Faults.BeforeOnce(c => Faults.Has(c, "insert into core.payment_allocation"),
            () => x.W.CloseMonthAsync(OpenPrWorld.Today, by.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task PR165_allocateReceipt_whose_first_allocation_the_database_refuses_23514_answers_period_closed_and_writes_nothing() =>
        await OwnDatabaseAsync(async w0 =>
        {
            var x = new OpenPrWorld(w0.Database);
            var w = x.W;
            x.OnLegacyToday();
            await x.RatesAsync();
            var accountant = await w.UserAsync("accountant");
            var holder = await w.ClientAsync();
            var p = await x.UsdPolicyAsync(holder, (0, 0.02m), (14, 0.02m));
            var receipt = await w.ReceiptAsync(holder, OpenPrWorld.Received, 0.07m);
            var ledger = await LedgerAsync(w);
            await CloseJanuaryBeforeFirstAllocationAsync(x, accountant);
            Assert.Equal($"/policies/{p.Id}?advance=period-closed", await PaymentActions.AllocateReceiptAsync(x.App, x.Rates, accountant, p.Id.ToString(), receipt.ToString()));
            Assert.True(x.Faults.AllFired);
            Assert.Equal(["USD 2026-12-20"], x.RateQuestions());
            Assert.Equal(1, x.Faults.Log.Count(c => Faults.Has(c, "insert into core.payment_allocation")));
            Assert.Equal(ledger, await LedgerAsync(w));
            Assert.Equal((0.07m, 0.04m), (await w.AvailableAsync(receipt), await w.OwedAsync(p.Id)));
            Assert.Equal("closed", await w.ScalarAsync<string>("select status from core.accounting_period where period_start = '2027-01-01'"));
        });

    [Fact]
    public async Task PR172_transferOverpayment_whose_release_on_A_the_database_refuses_23514_answers_period_closed_and_writes_nothing() =>
        await OwnDatabaseAsync(async w0 =>
        {
            var x = new OpenPrWorld(w0.Database);
            var w = x.W;
            x.OnLegacyToday();
            await x.RatesAsync();
            var t = await OpenPrTransferTests.XferAsync(x);
            var ledger = await LedgerAsync(w);
            var lines = await x.LinesAsync(t.Receipt);
            await CloseJanuaryBeforeFirstAllocationAsync(x, t.Accountant);
            Assert.Equal($"/policies/{t.B.Id}?advance=period-closed", await PaymentActions.TransferOverpaymentAsync(x.App, x.Rates, t.Accountant, t.B.Id.ToString(), t.Receipt.ToString()));
            Assert.True(x.Faults.AllFired);
            Assert.Equal(["USD 2026-12-20"], x.RateQuestions());
            // The first write tried was A's release (−30.00 USD), and nothing after it.
            var tried = Assert.Single(x.Faults.Log, c => Faults.Has(c, "insert into core.payment_allocation"));
            Assert.Equal(-30m, tried.Parameter("amount"));
            Assert.Equal(ledger, await LedgerAsync(w));
            Assert.Equal(lines, await x.LinesAsync(t.Receipt));
            Assert.Equal((-30m, 20m), (await w.OwedAsync(t.A.Id), await w.OwedAsync(t.B.Id)));
        });
}
