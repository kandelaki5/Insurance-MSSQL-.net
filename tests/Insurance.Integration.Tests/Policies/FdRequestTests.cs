using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The financial director's authorisation of a premium refund (0037) and of a receipt's reversal (0038) above the
/// company's threshold (owner 17.10.2026, docs/05 ვ8), through the card's actions on a real database: without a request
/// nothing is written and a request is kept; the approval needs payment.large.authorize, writes exactly the plan once,
/// and refuses a stale plan, a second approval, another policy's request.
/// </summary>
public sealed class FdRequestTests(TestDatabase db) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);

    public Task InitializeAsync() => w.SettingAsync("payment.fd_threshold_gel", "170.00");
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A 365.00 policy paid by two receipts (200.00 and 165.00), cancelled on day 181: 184.00 overpaid.</summary>
    private async Task<(CurrentUser Admin, World.Policy P, Guid R200, Guid R165)> OverpaidAsync()
    {
        var admin = await w.UserAsync();
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        await w.PayAsync(admin, p.Id, "200.00", Start.AddDays(4));
        await w.PayAsync(admin, p.Id, "165.00", Start.AddDays(5));
        Assert.NotNull((await w.CancelAsync(admin, p.Id, Start.AddDays(181))).Redirect);
        var receipts = await w.QueryAsync<(Guid Id, decimal Gel)>("select id, amount_gel from core.payment where payer_party_id = @h and kind = N'receipt'", new { h = p.Holder });
        return (admin, p, receipts.Single(r => r.Gel == 200m).Id, receipts.Single(r => r.Gel == 165m).Id);
    }

    private Task<int> RefundsAsync(Guid policyId) => w.ScalarAsync<int>("""
        select count(distinct p.id) from core.payment p join core.payment_allocation a on a.payment_id = p.id join core.installment i on i.id = a.installment_id
        where i.policy_id = @policyId and p.kind = N'refund'
        """, new { policyId });

    [Fact]
    public async Task A_refund_above_the_threshold_is_a_request_and_its_approval_pays_exactly_the_plan_once()
    {
        var (admin, p, _, _) = await OverpaidAsync();
        var accountant = await w.UserAsync("accountant");
        Assert.Equal($"/policies/{p.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, accountant, p.Id.ToString(), "cash", "  REF-1  "));
        Assert.Equal(0, await RefundsAsync(p.Id));
        var request = Assert.Single(await w.QueryAsync<(Guid Id, DateOnly RefundedOn, decimal AmountGel, string Currency, decimal Rate, string Method, string? Reference, Guid CreatedBy)>(
            "select id, refunded_on, amount_gel, currency, rate, method, reference, created_by from core.premium_refund_request where policy_id = @p", new { p = p.Id }));
        Assert.Equal((World.Today, 184m, "GEL", 1m, "cash", "REF-1", accountant.Id), (request.RefundedOn, request.AmountGel, request.Currency.Trim(), request.Rate, request.Method, request.Reference, request.CreatedBy));
        var plan = await w.QueryAsync<(int Position, decimal Amount, decimal AmountGel, DateOnly DueOn)>("""
            select l.position, l.amount, l.amount_gel, i.due_on from core.premium_refund_request_line l join core.installment i on i.id = l.installment_id
            where l.request_id = @id order by l.position
            """, new { id = request.Id });
        // Newest instalment first: 91.25 + 91.25 + 1.50 = 184.00.
        Assert.Equal([(1, 91.25m, 91.25m, Start.AddDays(273)), (2, 91.25m, 91.25m, Start.AddDays(182)), (3, 1.50m, 1.50m, Start.AddDays(91))], plan);

        // Whoever has no payment.large.authorize is refused — nothing written; the admin (by role) approves.
        Assert.Equal($"/policies/{p.Id}?refund=not-allowed", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, accountant, p.Id.ToString(), request.Id.ToString()));
        Assert.Equal(0, await RefundsAsync(p.Id));
        Assert.Equal($"/policies/{p.Id}?refund=state", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, admin, p.Id.ToString(), "not-a-request"));
        var other = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        Assert.Equal($"/policies/{other.Id}?refund=state", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, admin, other.Id.ToString(), request.Id.ToString()));
        Assert.Equal($"/policies/{p.Id}?refund=approved", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, admin, p.Id.ToString(), request.Id.ToString()));
        var refund = Assert.Single(await w.QueryAsync<(Guid Id, decimal AmountGel, string Method, string? Reference, Guid CreatedBy, DateOnly ReceivedOn)>(
            "select id, amount_gel, method, reference, created_by, received_on from core.payment where refund_request_id = @id", new { id = request.Id }));
        Assert.Equal((-184m, "cash", "REF-1", admin.Id, World.Today), (refund.AmountGel, refund.Method, refund.Reference, refund.CreatedBy, refund.ReceivedOn));
        var lines = await w.QueryAsync<(decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>("select amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @id order by amount", new { id = refund.Id });
        Assert.Equal([(-91.25m, -91.25m, 1m, World.Today), (-91.25m, -91.25m, 1m, World.Today), (-1.50m, -1.50m, 1m, World.Today)], lines);
        Assert.Equal(0m, await w.OwedAsync(p.Id));
        Assert.Equal($"/policies/{p.Id}?refund=already", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, admin, p.Id.ToString(), request.Id.ToString()));
        Assert.Equal(1, await RefundsAsync(p.Id));
    }

    [Fact]
    public async Task A_refund_request_whose_plan_changed_is_stale_and_writes_nothing()
    {
        var (admin, p, _, r165) = await OverpaidAsync();
        Assert.Equal($"/policies/{p.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, admin, p.Id.ToString(), "bank", null));
        var request = await w.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @p", new { p = p.Id });
        // The 165.00 receipt is reversed (below the threshold, at once): only 19.00 is overpaid now.
        Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, admin, r165.ToString(), p.Id.ToString(), "მცდარი"));
        Assert.Equal($"/policies/{p.Id}?refund=stale", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, admin, p.Id.ToString(), request.ToString()));
        Assert.Equal(0, await RefundsAsync(p.Id));
        // Below the threshold the 19.00 is paid back at once.
        Assert.Null(await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, admin, p.Id.ToString(), "bank", null));
        Assert.Equal(-19m, await w.ScalarAsync<decimal>("select amount_gel from core.payment where kind = N'refund' and payer_party_id = @h", new { h = p.Holder }));
    }

    [Fact]
    public async Task A_suspension_stops_a_refund_and_its_approval()
    {
        var (admin, p, _, _) = await OverpaidAsync();
        Assert.Equal($"/policies/{p.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, admin, p.Id.ToString(), "bank", null));
        var request = await w.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @p", new { p = p.Id });
        var suspension = await w.SuspendAsync(p.Holder, admin.Id);
        Assert.Equal($"/policies/{p.Id}?blocked=fms", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, admin, p.Id.ToString(), "bank", null));
        Assert.Equal($"/policies/{p.Id}?refund=suspended", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, admin, p.Id.ToString(), request.ToString()));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.premium_refund_request where policy_id = @p", new { p = p.Id }));
        Assert.Equal(0, await RefundsAsync(p.Id));
        await w.LiftAsync(suspension);
        Assert.Equal($"/policies/{p.Id}?refund=approved", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, admin, p.Id.ToString(), request.ToString()));
    }

    [Fact]
    public async Task Refund_refusals_without_a_redirect_write_nothing()
    {
        var (admin, p, _, _) = await OverpaidAsync();
        var viewer = await w.UserAsync("viewer");
        Assert.Null(await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, viewer, p.Id.ToString(), "bank", null));
        Assert.Null(await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, admin, p.Id.ToString(), "claim_offset", null));
        Assert.Null(await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, admin, "nope", "bank", null));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.premium_refund_request where policy_id = @p", new { p = p.Id }));
        Assert.Equal(0, await RefundsAsync(p.Id));
    }

    [Fact]
    public async Task A_reversal_above_the_threshold_is_a_request_and_its_approval_mirrors_exactly_the_plan_once()
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        await w.PayAsync(admin, p.Id, "200.00", Start.AddDays(4));
        var receipt = await w.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @h", new { h = p.Holder });
        Assert.Equal($"/policies/{p.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, accountant, receipt.ToString(), p.Id.ToString(), " მცდარი ჩარიცხვა "));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @receipt", new { receipt }));
        var request = Assert.Single(await w.QueryAsync<(Guid Id, Guid ReceiptId, DateOnly ReversedOn, decimal AmountGel, string Method, string Reason, Guid CreatedBy)>(
            "select id, receipt_id, reversed_on, amount_gel, method, reason, created_by from core.receipt_reversal_request where policy_id = @p", new { p = p.Id }));
        Assert.Equal((receipt, World.Today, 200m, "bank", "მცდარი ჩარიცხვა", accountant.Id), (request.ReceiptId, request.ReversedOn, request.AmountGel, request.Method, request.Reason, request.CreatedBy));
        Assert.Equal([91.25m, 91.25m, 17.50m], (await w.QueryAsync<decimal>("select amount from core.receipt_reversal_request_line where request_id = @id order by position", new { id = request.Id })).Order().Reverse());

        Assert.Equal($"/policies/{p.Id}?reversal=not-allowed", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, accountant, p.Id.ToString(), request.Id.ToString()));
        Assert.Equal($"/policies/{p.Id}?reversal=state", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, p.Id.ToString(), Guid.NewGuid().ToString()));
        Assert.Null(await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, "nope", request.Id.ToString()));
        Assert.Equal($"/policies/{p.Id}?reversal=approved", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, p.Id.ToString(), request.Id.ToString()));
        var mirror = Assert.Single(await w.QueryAsync<(Guid Id, decimal AmountGel, string Reference, Guid CreatedBy, DateOnly ReceivedOn, string Kind)>(
            "select id, amount_gel, reference, created_by, received_on, kind from core.payment where reversal_request_id = @id", new { id = request.Id }));
        Assert.Equal((-200m, "მცდარი ჩარიცხვა", admin.Id, World.Today, "reversal"), (mirror.AmountGel, mirror.Reference, mirror.CreatedBy, mirror.ReceivedOn, mirror.Kind));
        Assert.Equal(-200m, await w.ScalarAsync<decimal>("select sum(amount_gel) from core.payment_allocation where payment_id = @id", new { id = mirror.Id }));
        Assert.Equal(365m, await w.OwedAsync(p.Id));
        Assert.Equal($"/policies/{p.Id}?reversal=already", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, p.Id.ToString(), request.Id.ToString()));
    }

    [Fact]
    public async Task A_reversal_request_whose_allocations_changed_is_stale_and_a_small_one_is_made_at_once()
    {
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var p = await w.ActivePolicyAsync(admin, holder, Start);
        var q = await w.ActivePolicyAsync(admin, holder, Start);
        var receipt = await w.ReceiptAsync(holder, Start.AddDays(4), 400m);
        await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, admin, p.Id.ToString(), receipt.ToString());
        Assert.Equal($"/policies/{p.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, admin, receipt.ToString(), p.Id.ToString(), "x"));
        var request = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where policy_id = @p", new { p = p.Id });
        // The 35.00 left on the receipt goes to the client's other policy: the receipt's allocations are not the planned ones.
        Assert.Contains("allocated", await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, admin, q.Id.ToString(), receipt.ToString()));
        Assert.Equal($"/policies/{p.Id}?reversal=stale", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, p.Id.ToString(), request.ToString()));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @receipt", new { receipt }));

        // At or below the threshold a reversal is written at once, with the reason, mirroring every line on every policy.
        var small = await w.ReceiptAsync(holder, Start.AddDays(6), 170m);
        await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, admin, q.Id.ToString(), small.ToString());
        Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, admin, small.ToString(), q.Id.ToString(), "ზღვარზე"));
        var mirror = await w.QueryAsync<(decimal AmountGel, string Reference)>("select amount_gel, reference from core.payment where reverses_payment_id = @small", new { small });
        Assert.Equal([(-170m, "ზღვარზე")], mirror);
        // A second reversal of it: nothing more (the receipt is reversed).
        Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, admin, small.ToString(), q.Id.ToString(), "ზღვარზე"));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @small", new { small }));
        // A viewer and an empty reason write nothing.
        var other = await w.ReceiptAsync(holder, Start.AddDays(6), 10m);
        Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, await w.UserAsync("viewer"), other.ToString(), q.Id.ToString(), "x"));
        Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, admin, other.ToString(), q.Id.ToString(), "   "));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @other", new { other }));
    }

    [Fact]
    public async Task A_claim_set_off_is_not_reversed_here()
    {
        var admin = await w.UserAsync();
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        var offset = Guid.NewGuid();
        await w.App.AsUserAsync(admin.Id, tx => tx.ExecuteAsync(
            "insert core.payment (id, received_on, amount_gel, method, payer_party_id) values (@offset, @on, 50, N'claim_offset', @h)", new { offset, on = Start.AddDays(4), h = p.Holder }));
        Assert.Null(await PaymentActions.ReversePaymentAsync(w.App, admin, offset.ToString(), p.Id.ToString(), "x"));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @offset", new { offset }));
    }
}

/// <summary>A request dated in a month closed before its approval: nothing is written (its own database: today's month is closed).</summary>
public sealed class FdRequestClosedMonthTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    [Fact]
    public async Task Approval_of_a_refund_or_a_reversal_dated_in_a_month_closed_since_is_period_closed()
    {
        await w.SettingAsync("payment.fd_threshold_gel", "100.00");
        var admin = await w.UserAsync();
        var start = new DateOnly(2026, 1, 1);
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), start);
        await w.PayAsync(admin, p.Id, "365.00", start.AddDays(4));
        await w.CancelAsync(admin, p.Id, start.AddDays(181));
        Assert.Equal($"/policies/{p.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, admin, p.Id.ToString(), "bank", null));
        var receipt = await w.ScalarAsync<Guid>("select id from core.payment where payer_party_id = @h and kind = N'receipt'", new { h = p.Holder });
        Assert.Equal($"/policies/{p.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, admin, receipt.ToString(), p.Id.ToString(), "x"));
        var refund = await w.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @p", new { p = p.Id });
        var reversal = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where policy_id = @p", new { p = p.Id });

        await w.CloseMonthAsync(World.Today, admin.Id);
        Assert.Equal($"/policies/{p.Id}?refund=period-closed", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, admin, p.Id.ToString(), refund.ToString()));
        Assert.Equal($"/policies/{p.Id}?reversal=period-closed", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, p.Id.ToString(), reversal.ToString()));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where refund_request_id = @refund or reversal_request_id = @reversal", new { refund, reversal }));

        // A receipt dated in the closed month is refused on its date, with nothing written.
        var owing = await w.ActivePolicyAsync(admin, await w.ClientAsync(), start);
        var r = await w.PayAsync(admin, owing.Id, "10.00", World.Today);
        Assert.Equal(Insurance.Application.Texts.BillingTexts.Payment.Errors.PeriodClosed, r.Errors["received_on"]);
        Assert.Equal(0m, await w.PaidAsync(owing.Id));
    }
}
