using Insurance.Application.Services;
using Insurance.Domain.Finance;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/payment-regression.ts (UAT D1, review H20) and scripts/schedule-asof-regression.ts (Codex H23): the
/// actions approve, record a payment, cancel and refund run against a real database, with the same scenarios and numbers.
/// </summary>
public sealed class PaymentRegressionTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public async Task A_two_partial_payments_settle_the_first_instalment_oldest_first()
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        var a = await w.PendingPolicyAsync(client, Start);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, a.Id.ToString())).Outcome);
        var issue = await w.PremiumRowsAsync(a.Id);
        Assert.Single(issue);
        Assert.Equal(365m, issue[0].Amount);

        var p1 = await w.PayAsync(user, a.Id, "73.00", Start.AddDays(4), reference: "P1");
        Assert.Equal($"/policies/{a.Id}?paid=0", p1.Redirect);
        Assert.Equal(292m, await w.OwedAsync(a.Id));
        var first = await w.ScalarAsync<decimal>("select coalesce(sum(a.amount), 0) from core.payment_allocation a where a.installment_id = (select top (1) id from core.installment where policy_id = @id order by due_on)", new { id = a.Id });
        Assert.Equal(73m, first);

        var p2 = await w.PayAsync(user, a.Id, "18.25", Start.AddDays(5), reference: "P2");
        Assert.NotNull(p2.Redirect);
        Assert.Equal(273.75m, await w.OwedAsync(a.Id));
        first = await w.ScalarAsync<decimal>("select coalesce(sum(a.amount), 0) from core.payment_allocation a where a.installment_id = (select top (1) id from core.installment where policy_id = @id order by due_on)", new { id = a.Id });
        Assert.Equal(91.25m, first);

        // The allocation is booked on the Tbilisi day it was made, the receipt keeps its own date.
        var rows = await w.QueryAsync<(string Kind, decimal AmountGel, DateOnly ReceivedOn, DateOnly BookedOn, int Lines)>("""
            select p.kind, p.amount_gel, p.received_on, max(a.booked_on), count(a.id)
            from core.payment p join core.payment_allocation a on a.payment_id = p.id join core.installment i on i.id = a.installment_id
            where i.policy_id = @id group by p.id, p.kind, p.amount_gel, p.received_on order by p.received_on
            """, new { id = a.Id });
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(("receipt", 1, World.Today), (r.Kind, r.Lines, r.BookedOn)));
        Assert.Equal([73m, 18.25m], rows.Select(r => r.AmountGel));
        Assert.Equal([Start.AddDays(4), Start.AddDays(5)], rows.Select(r => r.ReceivedOn));
        // The approval is in the audit trail (the legacy "audit" check counts A's approval with B's two updates).
        Assert.True(await w.ScalarAsync<int>("select count(*) from core.audit_log where table_name = N'policy' and row_id = @id and action = N'update'", new { id = a.Id.ToString() }) >= 1);
    }

    [Fact]
    public async Task B_full_payment_cancellation_and_one_refund()
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        var b = await w.PendingPolicyAsync(client, Start);
        await PolicyActions.ApprovePolicyAsync(w.App, user, b.Id.ToString());
        Assert.NotNull((await w.PayAsync(user, b.Id, "365.00", Start.AddDays(4))).Redirect);
        Assert.Equal(0m, await w.OwedAsync(b.Id));

        var cancel = await w.CancelAsync(user, b.Id, Start.AddDays(181));
        Assert.Equal($"/policies/{b.Id}?cancelled=1", cancel.Redirect);
        var cancellations = (await w.PremiumRowsAsync(b.Id)).Where(r => r.Kind == "cancellation").ToList();
        Assert.Single(cancellations);
        Assert.Equal(-184m, cancellations[0].Amount);
        Assert.Equal(-184m, await w.OwedAsync(b.Id));

        Assert.Null(await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, user, b.Id.ToString(), "bank", "REFUND"));
        Assert.Null(await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, user, b.Id.ToString(), "bank", "REFUND-2"));
        var payments = await w.QueryAsync<(string Kind, decimal AmountGel)>("""
            select p.kind, p.amount_gel from core.payment p where exists (select 1 from core.payment_allocation a join core.installment i on i.id = a.installment_id
            where a.payment_id = p.id and i.policy_id = @id) order by p.created_at
            """, new { id = b.Id });
        Assert.Equal([("receipt", 365m), ("refund", -184m)], payments);
        Assert.Equal(181m, payments.Sum(p => p.AmountGel));
        Assert.Equal(0m, await w.OwedAsync(b.Id));

        // The approvals and the cancellation are in the audit trail.
        Assert.True(await w.ScalarAsync<int>("select count(*) from core.audit_log where table_name = N'policy' and row_id = @id and action = N'update'", new { id = b.Id.ToString() }) >= 2);
    }

    // ---------------------------------------------------------------- schedule-asof-regression (H23)

    // A 365-day cover; four instalments of 91.25 (the last on day 273); a cancellation on day 274 that lies in the future:
    // 274.00 retained, 91.00 returned, the last instalment reduced by 91.00 — booked on the cancellation day. Day 182 lies
    // in the past (the "reduction already in force" case).
    private static readonly DateOnly AsOfStart = World.Today.AddDays(-200);
    private static readonly DateOnly CancelOn = AsOfStart.AddDays(274);
    private static readonly DateOnly PaidOn = AsOfStart.AddDays(4);

    [Fact]
    public async Task H23_a_cancellation_from_a_future_day_counts_from_that_day_only()
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        var p1 = await w.ActivePolicyAsync(user, client, AsOfStart);
        await w.PayAsync(user, p1.Id, "91.25", PaidOn);
        var c1 = await w.CancelAsync(user, p1.Id, CancelOn);
        Assert.Contains("cancelled", c1.Redirect);
        var adjustment = await w.QueryAsync<(DateOnly BookedOn, decimal Change)>(
            "select j.booked_on, j.change from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id", new { id = p1.Id });
        Assert.Equal([(CancelOn, -91m)], adjustment);
        Assert.Equal(273.75m, await w.OwedOnAsync(p1.Id, World.Today));
        Assert.Equal(273.75m, await w.OwedOnAsync(p1.Id, CancelOn.AddDays(-1)));
        Assert.Equal(182.75m, await w.OwedOnAsync(p1.Id, CancelOn));

        // A payment of the debt shown today is allocated in full; no part is left as an advance.
        var paid = await w.PayAsync(user, p1.Id, "273.75", World.Today);
        Assert.Equal("0", World.Param(paid.Redirect, "paid"));
        Assert.Equal(365m, await w.PaidAsync(p1.Id));
        Assert.Equal(0m, await w.OwedOnAsync(p1.Id, World.Today));
        Assert.Equal(-91m, await w.OwedOnAsync(p1.Id, CancelOn));
    }

    [Fact]
    public async Task H23_a_refund_before_the_cancellation_day_pays_back_nothing()
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        var p2 = await w.ActivePolicyAsync(user, client, AsOfStart);
        await w.PayAsync(user, p2.Id, "365.00", PaidOn);
        await w.CancelAsync(user, p2.Id, CancelOn);
        Assert.Null(await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, user, p2.Id.ToString(), "bank", "REFUND"));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where kind = N'refund' and payer_party_id = @client", new { client }));
        Assert.Equal(0m, await w.OwedOnAsync(p2.Id, World.Today));
        Assert.Equal(-91m, await w.OwedOnAsync(p2.Id, CancelOn));
    }

    [Fact]
    public async Task H23_an_earlier_receipt_allocated_after_the_cancellation_covers_todays_debt()
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        var p3 = await w.ActivePolicyAsync(user, client, AsOfStart);
        await w.PayAsync(user, p3.Id, "91.25", PaidOn);
        await w.CancelAsync(user, p3.Id, CancelOn);
        var receipt = await w.ReceiptAsync(client, PaidOn, 273.75m, user.Id, "advance");
        var to = await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, user, p3.Id.ToString(), receipt.ToString());
        Assert.Equal($"/policies/{p3.Id}?advance=allocated&gel=27375&left=0", to);
        Assert.Equal(0m, await w.OwedOnAsync(p3.Id, World.Today));
    }

    [Fact]
    public async Task H23_a_cancellation_from_a_past_day_counts_today()
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        var p4 = await w.ActivePolicyAsync(user, client, AsOfStart);
        await w.PayAsync(user, p4.Id, "91.25", PaidOn);
        Assert.NotNull((await w.CancelAsync(user, p4.Id, AsOfStart.AddDays(182), "past")).Redirect);
        var change = await w.ScalarAsync<decimal>("select sum(j.change) from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id", new { id = p4.Id });
        Assert.Equal(-183m, change);
        Assert.Equal(90.75m, await w.OwedOnAsync(p4.Id, World.Today));
    }
}

/// <summary>
/// Legacy H23 for a reduction approved from a future day: it counts from that day in the card, the payment form and the report.
/// Its own database: the original's month is closed so that the change stands as a policy of its own (legacy PR #207).
/// </summary>
public sealed class PaymentRegressionOwnChangeTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly AsOfStart = World.Today.AddDays(-200);
    private static readonly DateOnly CancelOn = AsOfStart.AddDays(274);
    private static readonly DateOnly PaidOn = AsOfStart.AddDays(4);

    [Fact]
    public async Task H23_an_approved_reduction_from_a_future_day_counts_from_that_day_in_card_payment_and_report()
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        var p5 = await w.ActivePolicyAsync(user, client, AsOfStart);
        await w.PayAsync(user, p5.Id, "91.25", PaidOn);
        // The original's own month closed: the change stands as a policy of its own on its date (legacy PR #207); in an open
        // month it would correct the original from its start instead (EndorsementTests).
        await w.CloseMonthAsync(new DateOnly(AsOfStart.Year, AsOfStart.Month, 1), user.Id);
        async Task<System.Numerics.BigInteger> ReceivableGel(DateOnly day) =>
            (await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, day.ToString("yyyy-MM")))).Receivables.FirstOrDefault(r => r.Currency == "GEL")?.Outstanding ?? 0;
        var before = (await ReceivableGel(World.Today), await ReceivableGel(CancelOn));
        var issued = await w.EndorseAsync(user, p5.Id, CancelOn, "-50", "შემცირება");
        var change = await w.ChangeOfAsync(p5.Id);
        Assert.Equal($"/policies/{change!.Value.Id}?issued=1", issued.Redirect);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, change.Value.Id.ToString())).Outcome);
        var after = (await ReceivableGel(World.Today), await ReceivableGel(CancelOn));

        var row = Assert.Single(await w.PremiumRowsAsync(change.Value.Id));
        Assert.Equal((-50m, CancelOn), (row.Amount, row.BookedOn));
        Assert.Equal("active", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = change.Value.Id }));
        var adjustments = await w.QueryAsync<(DateOnly BookedOn, decimal Change)>("""
            select j.booked_on, sum(j.change) from core.installment_adjustment j join core.installment i on i.id = j.installment_id
            where i.policy_id = @id group by j.booked_on
            """, new { id = p5.Id });
        Assert.Equal([(CancelOn, -50m)], adjustments);
        Assert.Equal(273.75m, await w.OwedOnAsync(p5.Id, World.Today));
        Assert.Equal(273.75m, await w.OwedOnAsync(p5.Id, CancelOn.AddDays(-1)));
        Assert.Equal(223.75m, await w.OwedOnAsync(p5.Id, CancelOn));
        // The month-end receivable of the report follows the same date.
        if (CancelOn.Month != World.Today.Month || CancelOn.Year != World.Today.Year) Assert.Equal(before.Item1, after.Item1);
        Assert.Equal(-5000, (int)(after.Item2 - before.Item2));

        var paid = await w.PayAsync(user, p5.Id, "273.75", World.Today);
        Assert.Equal("0", World.Param(paid.Redirect, "paid"));
        Assert.Equal(0m, await w.OwedOnAsync(p5.Id, World.Today));
        Assert.Equal(-50m, await w.OwedOnAsync(p5.Id, CancelOn));
    }
}
