using Insurance.Application.Services;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// refundOverpayment against a suspension of the old program's open PRs #149 and #150 (scripts/integration/
/// refund-targeted-suspension.test.ts and refund-legacy-bank-reference.test.ts), ported to PaymentActions.RefundOverpaymentAsync
/// on a real database (docs/04 rules 8 / 8a; H11, H11-U1). The legacy world: a USD policy A of 365.00 USD due 01.01.2027,
/// paid by the client's 985.50 GEL of 20.01.2027 (365.00 USD at 2.7, booked 20.01), reduced by an approved −184.00 USD booked
/// 31.01.2027, so 184.00 USD is overpaid today; the client's second policy B of 100.00 USD paid by 270.00 GEL (2.7), nothing
/// over; USD 3.2 on 31.01.2027; the accountant asks for the refund on A on 31.01.2027. The refund, −588.80 GEL, is below the
/// baseline's financial-director threshold (payment.fd_threshold_gel, 10 000 GEL as migrated), so under the baseline rule
/// (legacy 0036–0038) it is still paid at once, as the legacy chain expected.
/// </summary>
public sealed class OpenPrRefundTests : IClassFixture<TestDatabase>
{
    private readonly OpenPrWorld x;
    private World w => x.W;

    public OpenPrRefundTests(TestDatabase db)
    {
        x = new OpenPrWorld(db);
        x.OnLegacyToday();
    }

    public sealed record Overpaid(CurrentUser Accountant, CurrentUser Admin, Guid Holder, World.Policy A, World.Policy B, Guid ReceiptA, Guid A1);

    /// <summary>The legacy refund world for a new client of its own.</summary>
    public static async Task<Overpaid> OverpaidAsync(OpenPrWorld x)
    {
        var w = x.W;
        var accountant = await w.UserAsync("accountant");
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var a = await x.UsdPolicyAsync(holder, (0, 365m));
        var b = await x.UsdPolicyAsync(holder, (0, 100m));
        var paidOn = new DateOnly(2027, 1, 20);
        var ra = await w.ReceiptAsync(holder, paidOn, 985.50m);
        var rb = await w.ReceiptAsync(holder, paidOn, 270m);
        var a1 = Assert.Single(await x.InstalmentsAsync(a));
        await x.AllocateDirectAsync(ra, a1, 365m, 985.50m, 2.7m, paidOn);
        await x.AllocateDirectAsync(rb, Assert.Single(await x.InstalmentsAsync(b)), 100m, 270m, 2.7m, paidOn);
        await x.AdjustDirectAsync(a1, -184m, OpenPrWorld.Today);
        return new Overpaid(accountant, admin, holder, a, b, ra, a1);
    }

    private Task<string?> Refund(Overpaid o, string? reference = null) =>
        PaymentActions.RefundOverpaymentAsync(x.App, x.Rates, o.Accountant, o.A.Id.ToString(), "bank", reference);

    private Task<List<(DateOnly ReceivedOn, decimal AmountGel, string Method, Guid? Payer, string? Reference, Guid? CreatedBy)>> RefundsAsync(Overpaid o) =>
        w.QueryAsync<(DateOnly, decimal, string, Guid?, string?, Guid?)>(
            "select received_on, amount_gel, method, payer_party_id, reference, created_by from core.payment where kind = N'refund' and payer_party_id = @h", new { h = o.Holder });

    private Task<List<(Guid InstallmentId, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>> RefundLinesAsync(Overpaid o) =>
        w.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly)>("""
            select a.installment_id, a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.payment p on p.id = a.payment_id
            where p.kind = N'refund' and p.payer_party_id = @h
            """, new { h = o.Holder });

    private async Task BlockedAsync(Overpaid o, string? reference)
    {
        var ledger = await LedgerAsync(w);
        x.Faults.ClearLog();
        Assert.Equal($"/policies/{o.A.Id}?blocked=fms", await Refund(o, reference));
        // Stopped at the suspension: what is paid and the schedule are not read, no rate asked, nothing written.
        Assert.False(x.ScheduleRead);
        Assert.Empty(x.RateQuestions());
        Assert.False(x.Inserted);
        Assert.Equal(ledger, await LedgerAsync(w));
        Assert.Equal((-184m, 0m), (await w.OwedAsync(o.A.Id), await w.OwedAsync(o.B.Id)));
    }

    private async Task RefundedAsync(Overpaid o, string? reference)
    {
        x.Faults.ClearLog();
        Assert.Null(await Refund(o, reference));
        Assert.Equal(["USD 2027-01-31"], x.RateQuestions());
        // By hand: 184.00 USD × 3.2 = 588.80 GEL, one refund and its line on A's instalment, today, at today's 3.2.
        Assert.Equal([(OpenPrWorld.Today, -588.80m, "bank", (Guid?)o.Holder, reference, (Guid?)o.Accountant.Id)], await RefundsAsync(o));
        Assert.Equal([(o.A1, -184m, -588.80m, 3.2m, OpenPrWorld.Today)], await RefundLinesAsync(o));
        // A now holds 181.00 USD on its 181.00; B untouched; the receipt's own allocation is as it was (985.50 GEL not revalued).
        Assert.Equal((0m, 0m, 181m), (await w.OwedAsync(o.A.Id), await w.OwedAsync(o.B.Id), await w.PaidAsync(o.A.Id)));
        Assert.Equal([(o.A1, 365m, 985.50m, 2.7m, new DateOnly(2027, 1, 20))], await x.LinesAsync(o.ReceiptA));
    }

    [Fact]
    public async Task PR149_a_suspension_over_A_blocks_the_refund_on_A_one_over_B_lets_A_refund_minus_588_80_at_3_2()
    {
        await x.RatesAsync();
        // Two identical worlds (two clients), differing only in the policy the client's one suspension is attached to.
        var onA = await OverpaidAsync(x);
        var sa = await x.SuspendAsync(onA.Holder, onA.Admin.Id, onA.A.Id);
        Assert.Equal("active", await x.SuspensionStateAsync(onA.Holder, sa));
        await BlockedAsync(onA, null);
        Assert.Empty(await RefundsAsync(onA));

        var onB = await OverpaidAsync(x);
        var sb = await x.SuspendAsync(onB.Holder, onB.Admin.Id, onB.B.Id);
        Assert.Equal("active", await x.SuspensionStateAsync(onB.Holder, sb));
        await RefundedAsync(onB, null);
        // The suspension over B still blocks an operation on B.
        Assert.Equal($"/policies/{onB.B.Id}?blocked=fms", await PaymentActions.RefundOverpaymentAsync(x.App, x.Rates, onB.Accountant, onB.B.Id.ToString(), "bank", null));
    }

    [Fact]
    public async Task PR150_an_old_suspension_carrying_only_PAY_77_blocks_the_refund_referenced_pay_77_with_spaces_and_PAY_78_refunds_minus_588_80()
    {
        await x.RatesAsync();
        var o = await OverpaidAsync(x);
        var legacy = await x.LegacyUnmappedSuspensionAsync(o.Holder, "PAY-77");
        Assert.Equal("active", await x.SuspensionStateAsync(o.Holder, legacy));
        await BlockedAsync(o, " pay-77 ");
        Assert.Empty(await RefundsAsync(o));
        // The reference is trimmed before it is compared and kept: PAY-78 matches nothing, the refund is paid with it.
        await RefundedAsync(o, "PAY-78");
    }
}
