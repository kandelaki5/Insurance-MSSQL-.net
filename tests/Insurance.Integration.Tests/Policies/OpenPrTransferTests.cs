using Insurance.Application.Services;
using Insurance.Integration.Tests.FailureInjection;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// transferOverpayment scenarios of the old program's open PRs #172, #174, #175, #176, #177 and #178 (scripts/integration/
/// client-credit-first-insert-refusal, -paid-target-refusal, -source-suspension, -target-suspension, -legacy-policy-reference
/// and -legacy-nonmatching-reference .test.ts), ported to PaymentActions.TransferOverpaymentAsync on a real database (owner's
/// answer a14: a client's overpayment may move to the client's other policy). The legacy "client-xfer" world: the client's
/// receipt of 270.00 GEL of 20.12.2026 holds policy A in full (100.00 USD / 270.00 GEL at 2.700000, booked 20.12.2026); A's
/// instalment is reduced by an approved −30.00 USD booked 15.01.2027, so A is 70.00 USD today and the receipt holds 30.00 USD /
/// 81.00 GEL too much; policy B owes 20.00 USD due 15.01.2027; USD 2.7 on 20.12.2026, 3.2 on 31.01.2027; the accountant
/// moves the excess onto B on 31.01.2027. #171 (no rate) is in <see cref="OpenPrNoRateTests"/>, #172's 23514 half in
/// <see cref="OpenPrPeriodClosedTests"/>.
/// </summary>
public sealed class OpenPrTransferTests : IClassFixture<TestDatabase>
{
    private readonly OpenPrWorld x;
    private World w => x.W;

    public OpenPrTransferTests(TestDatabase db)
    {
        x = new OpenPrWorld(db);
        x.OnLegacyToday();
    }

    public sealed record Xfer(CurrentUser Accountant, CurrentUser Admin, Guid Holder, World.Policy A, World.Policy B, Guid Receipt, Guid A1, Guid B1);

    /// <summary>The legacy client-xfer world, for a new client of its own.</summary>
    public static async Task<Xfer> XferAsync(OpenPrWorld x)
    {
        var w = x.W;
        var accountant = await w.UserAsync("accountant");
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var a = await x.UsdPolicyAsync(holder, (0, 100m));
        var b = await x.UsdPolicyAsync(holder, (14, 20m));
        var receipt = await w.ReceiptAsync(holder, OpenPrWorld.Received, 270m);
        var a1 = Assert.Single(await x.InstalmentsAsync(a));
        var b1 = Assert.Single(await x.InstalmentsAsync(b));
        await x.AllocateDirectAsync(receipt, a1, 100m, 270m, 2.7m, OpenPrWorld.Received);
        await x.AdjustDirectAsync(a1, -30m, new DateOnly(2027, 1, 15));
        return new Xfer(accountant, admin, holder, a, b, receipt, a1, b1);
    }

    private Task<string> Transfer(Xfer t) => PaymentActions.TransferOverpaymentAsync(x.App, x.Rates, t.Accountant, t.B.Id.ToString(), t.Receipt.ToString());

    /// <summary>A refusal: the answer, no rate asked, nothing written; A still holds its 30.00 USD / 81.00 GEL, B still owes 20.00 USD.</summary>
    private async Task RefusedAsync(Xfer t, string outcome, bool scheduleRead = false)
    {
        var ledger = await LedgerAsync(w);
        var lines = await x.LinesAsync(t.Receipt);
        x.Faults.ClearLog();
        Assert.Equal($"/policies/{t.B.Id}?advance={outcome}", await Transfer(t));
        Assert.Empty(x.RateQuestions());
        Assert.Equal(scheduleRead, x.ScheduleRead);
        Assert.False(x.Inserted);
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal(lines, await x.LinesAsync(t.Receipt));
        var held = Assert.Single(await w.App.ReadTxAsync(tx => Billing.LoadReleasableAsync(tx, t.Holder, OpenPrWorld.Today, t.B.Id)), r => r.PaymentId == t.Receipt);
        Assert.Equal((t.A.Id, 30m, 81m), (held.PolicyId, held.Excess, held.ExcessGel));
    }

    [Fact]
    public async Task PR172_the_release_on_A_failing_with_another_error_comes_out_as_that_very_error_nothing_written_the_retry_moves_it_once()
    {
        await x.RatesAsync();
        var t = await XferAsync(x);
        var ledger = await LedgerAsync(w);
        var lines = await x.LinesAsync(t.Receipt);
        var sentinel = new InjectedFault("PR172_RELEASE_ON_A");
        x.Faults.Reset();
        x.Faults.FailOnce(c => Faults.Has(c, "insert into core.payment_allocation") && Equals(c.Parameter("amount"), -30m), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => Transfer(t)));
        Assert.True(x.Faults.AllFired);
        Assert.Single(x.Faults.Log, c => Faults.Has(c, "insert into core.payment_allocation"));
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal(lines, await x.LinesAsync(t.Receipt));
        Assert.Equal($"/policies/{t.B.Id}?advance=transferred&gel=5400&left=2700&released=8100", await Transfer(t));
        Assert.Equal(lines.Count + 2, (await x.LinesAsync(t.Receipt)).Count);
    }

    [Fact]
    public async Task PR174_onto_B_already_paid_in_full_by_its_own_receipt_answers_nothing_owed_before_any_rate_A_keeps_its_excess()
    {
        await x.RatesAsync();
        var t = await XferAsync(x);
        // The legacy test's two extra rows: the client's own 54.00 GEL of 20.12.2026 allocated to B, 20.00 USD / 54.00 at 2.7 on 31.01.2027.
        var own = await w.ReceiptAsync(t.Holder, OpenPrWorld.Received, 54m);
        await x.AllocateDirectAsync(own, t.B1, 20m, 54m, 2.7m, OpenPrWorld.Today);
        await RefusedAsync(t, "nothing-owed", scheduleRead: true);
        Assert.Equal((0m, 0m, 0m), (await w.OwedAsync(t.B.Id), await w.AvailableAsync(t.Receipt), await w.AvailableAsync(own)));
    }

    [Fact]
    public async Task PR175_a_suspension_over_the_source_policy_A_only_answers_suspended()
    {
        await x.RatesAsync();
        var t = await XferAsync(x);
        var s = await x.SuspendAsync(t.Holder, t.Admin.Id, t.A.Id);
        Assert.Equal("active", await x.SuspensionStateAsync(t.Holder, s));
        await RefusedAsync(t, "suspended");
    }

    [Fact]
    public async Task PR176_a_suspension_over_the_target_policy_B_only_answers_suspended()
    {
        await x.RatesAsync();
        var t = await XferAsync(x);
        var s = await x.SuspendAsync(t.Holder, t.Admin.Id, t.B.Id);
        Assert.Equal("active", await x.SuspensionStateAsync(t.Holder, s));
        await RefusedAsync(t, "suspended");
    }

    [Fact]
    public async Task PR177_an_old_unmapped_suspension_carrying_Bs_number_answers_suspended()
    {
        await x.RatesAsync();
        var t = await XferAsync(x);
        var s = await x.LegacyUnmappedSuspensionAsync(t.Holder, t.B.Number);
        Assert.Equal("active", await x.SuspensionStateAsync(t.Holder, s));
        await RefusedAsync(t, "suspended");
    }

    [Fact]
    public async Task PR178_an_old_unmapped_suspension_with_another_number_does_not_block_A_gives_back_30_USD_81_GEL_B_takes_20_USD_54_GEL_27_left()
    {
        await x.RatesAsync();
        var t = await XferAsync(x);
        var s = await x.LegacyUnmappedSuspensionAsync(t.Holder, "PAY-NOT-THIS-TRANSFER");
        Assert.Equal("active", await x.SuspensionStateAsync(t.Holder, s));
        var receiptRow = await x.ReceiptRowAsync(t.Receipt);
        x.Faults.ClearLog();
        Assert.Equal($"/policies/{t.B.Id}?advance=transferred&gel=5400&left=2700&released=8100", await Transfer(t));
        // One rate: USD of the receipt's day 20.12.2026 (2.7), not today's 3.2.
        Assert.Equal(["USD 2026-12-20"], x.RateQuestions());
        Assert.Equal([
            (t.A1, 100m, 270m, 2.7m, OpenPrWorld.Received),
            (t.A1, -30m, -81m, 2.7m, OpenPrWorld.Today),
            (t.B1, 20m, 54m, 2.7m, OpenPrWorld.Today)], await x.LinesAsync(t.Receipt));
        // A holds 70.00 USD / 189.00 GEL on its 70.00 (nothing owed, nothing over); B paid; 27.00 GEL free: 189 + 54 + 27 = 270.
        Assert.Equal((0m, 0m, 27m), (await w.OwedAsync(t.A.Id), await w.OwedAsync(t.B.Id), await w.AvailableAsync(t.Receipt)));
        Assert.Equal(receiptRow, await x.ReceiptRowAsync(t.Receipt));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @h", new { h = t.Holder }));
        Assert.Equal((true, "PAY-NOT-THIS-TRANSFER"), (await w.ScalarAsync<bool>("select legacy_unmapped from core.fms_suspension where id = @s", new { s }),
            await w.ScalarAsync<string>("select transaction_ref from core.fms_suspension where id = @s", new { s })));
    }
}
