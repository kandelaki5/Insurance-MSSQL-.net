using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Integration.Tests.Policies;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>
/// The read failures of legacy scripts/integration/fd-net-transfer-chain.test.ts that docs/legacy-tests-gaps-b.md left for the
/// read-failure seam, on the real SQL Server through the command interceptor: the refund day's stored rate read failing
/// ("the same error, nothing written — the stored rows as they were"), and, for each of the financial director's three
/// approvals (a claim payment request, a premium refund request, a receipt's reversal request), a read of the permission
/// check — the quick one (a snapshot read before the transaction) and the binding one (inside the writing transaction),
/// each of the three reads (own decisions, memberships, the group grants of the legacy SQL.grants) — failing once: the
/// same error comes out (never "not allowed"), nothing is written, the request stays pending, and the retry approves once.
/// The approver is an underwriter given payment.large.authorize by a group, as in the legacy test. Today is the real day.
/// </summary>
public sealed class FdApprovalFaultTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private const string Fd = "payment.large.authorize";
    private readonly Faults faults = new();
    private readonly World w = new(db);
    private static DateOnly Today => World.Today;

    /// <summary>The application with the interceptor: the actions under test, and their rates (no network).</summary>
    private Db App => new(new DbOptions { ConnectionString = db.AppConnection, Interceptor = faults, Clock = db.Clock });

    private Rates RatesOf(Db app) => new(app, new NoNetwork());

    private async Task<CurrentUser> GroupedUnderwriterAsync()
    {
        var u = await w.UserAsync("underwriter");
        await GroupAsync(w, Fd, u.Id);
        return u;
    }

    // ---------------------------------------------------------------- the refund day's rate read

    private sealed record Overpaid(CurrentUser Admin, CurrentUser Accountant, World.Policy P, Guid Inst, Guid Receipt);

    /// <summary>A policy in the currency with one instalment of 1 000.00 due on the first of the month, settled 1 000.00 + the excess by one receipt (at 2.7 for USD).</summary>
    private async Task<Overpaid> OverpaidAsync(string currency, decimal excess)
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var rate = currency == "GEL" ? 1m : 2.7m;
        var p = await w.PendingPolicyAsync(holder, MonthStart, currency, rate, 1000m, [(0, 1000m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        var inst = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = p.Id });
        var settled = 1000m + excess;
        var gel = Math.Round(settled * rate, 2, MidpointRounding.AwayFromZero);
        var receipt = await w.ReceiptAsync(holder, Today, gel, reference: "ჩარიცხვა");
        await using var c = await db.OpenAppAsync();
        await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @inst, @settled, @gel, @rate, @today)",
            new { receipt, inst, settled, gel, rate, today = Today });
        return new Overpaid(admin, accountant, p, inst, receipt);
    }

    /// <summary>Every row of the client's payments and the policy's allocations, as one text (the legacy "stored rows as they were").</summary>
    private Task<string> StoredAsync(World.Policy p) => w.ScalarAsync<string>("""
        select concat(
          (select string_agg(concat(convert(nvarchar(36), id), N':', kind, N':', amount_gel, N':', received_on), N'|') within group (order by id) from core.payment where payer_party_id = @h), N'#',
          (select string_agg(concat(convert(nvarchar(36), a.id), N':', a.amount, N':', a.amount_gel, N':', a.rate, N':', a.booked_on), N'|') within group (order by a.id)
             from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @p), N'#',
          (select count(*) from core.premium_refund_request where policy_id = @p))
        """, new { h = p.Holder, p = p.Id });

    private Task<List<(decimal Gel, Guid By)>> RefundsAsync(World.Policy p) => w.QueryAsync<(decimal, Guid)>(
        "select amount_gel, created_by from core.payment where kind = N'refund' and payer_party_id = @h", new { h = p.Holder });

    private static bool TodaysRateOf(CommandEvent c, string currency) =>
        Faults.Has(c, "select rate from core.exchange_rate where currency = @currency and rate_date = @date") && Equals(c.Parameter("currency"), currency);

    [Fact]
    public async Task The_refund_days_rate_read_fails_the_same_error_nothing_written_the_stored_rows_as_they_were_the_retry_refunds_minus_5_01_once()
    {
        // USD 2.50 paid beyond, today's rate 2.002: 2.50 × 2.002 = 5.005 → 5.01 (as GapsFdRefundReversalTests).
        await w.RateAsync("USD", Today, 2.002m);
        var o = await OverpaidAsync("USD", 2.50m);
        var stored = await StoredAsync(o.P);
        var app = App;
        faults.Reset();
        var sentinel = new InjectedFault("RATE_READ");
        faults.FailOnce(c => TodaysRateOf(c, "USD"), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => PaymentActions.RefundOverpaymentAsync(app, RatesOf(app), o.Accountant, o.P.Id.ToString(), "bank", "დაბრუნება")));
        Assert.True(faults.AllFired);
        // The refund's transaction was begun and kept nothing: the stored rows as they were.
        Assert.True(faults.BegunATransaction);
        Assert.Equal(stored, await StoredAsync(o.P));
        Assert.Empty(await RefundsAsync(o.P));
        // The retry: refunded once, −5.01 at today's 2.002.
        Assert.Null(await PaymentActions.RefundOverpaymentAsync(app, RatesOf(app), o.Accountant, o.P.Id.ToString(), "bank", "დაბრუნება"));
        Assert.Equal([(-5.01m, o.Accountant.Id)], await RefundsAsync(o.P));
        Assert.Equal([(o.Inst, -2.50m, -5.01m, 2.002m)], await w.QueryAsync<(Guid, decimal, decimal, decimal)>("""
            select a.installment_id, a.amount, a.amount_gel, a.rate from core.payment_allocation a join core.payment r on r.id = a.payment_id
            where r.kind = N'refund' and r.payer_party_id = @h
            """, new { h = o.P.Holder }));
    }

    // ---------------------------------------------------------------- the financial director's approvals: a permission read fails once

    /// <summary>A claim payment request: gross 11 000.01, deductible 1 000.00, net 10 000.01 above the threshold (GapsFdClaimTests' world).</summary>
    [Theory]
    [MemberData(nameof(Faults.QuickAndBinding), MemberType = typeof(Faults))]
    public async Task Claim_payment_request_the_approvals_permission_read_fails_the_same_error_nothing_written_the_retry_approves_once(string label, string name)
    {
        var f = new FdClaimWorld(db);
        var c = await f.ClaimAsync();
        var a = await f.C.UserAsync("accountant");
        Assert.Equal($"/claims/{c.Id}?payment=requested", (await f.PayAsync(a, c.Id, "11000.01")).RedirectTo);
        var request = await f.RequestIdAsync(c.Id);
        var underwriter = await GroupedUnderwriterAsync();
        var ledger = await f.LedgerAsync(c);
        var app = App;
        Task<string?> Approve() => ClaimActions.ApproveClaimPaymentRequestAsync(app, RatesOf(app), underwriter,
            ClaimWorld.Form(("claim_id", c.Id.ToString()), ("request_id", request.ToString())));
        faults.Reset();
        var sentinel = new InjectedFault($"FD_CLAIM_{label}_{name}");
        faults.FailOnce(Faults.Read(label, name), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(Approve));
        Assert.Equal(label == "binding", faults.BegunATransaction);
        Assert.Equal(ledger, await f.LedgerAsync(c));
        Assert.Empty(await f.PaymentsAsync(c.Id));
        Assert.Single(await f.RequestsAsync(c.Id));
        Assert.Equal($"/claims/{c.Id}?payment=approved", await Approve());
        Assert.Equal([FdClaimWorld.Paid(FdClaimWorld.Today, 11000.01m, 1000m, 0m, 10000.01m, 10000.01m, 1m, c.Policy.PartyId, underwriter.Id, request: request)], await f.PaymentsAsync(c.Id));
        Assert.Equal($"/claims/{c.Id}?payment=already", await Approve());
        Assert.Single(await f.PaymentsAsync(c.Id));
    }

    /// <summary>A premium refund request: 10 000.01 GEL paid beyond, asked by the accountant.</summary>
    [Theory]
    [MemberData(nameof(Faults.QuickAndBinding), MemberType = typeof(Faults))]
    public async Task Premium_refund_request_the_approvals_permission_read_fails_the_same_error_nothing_written_the_retry_approves_once(string label, string name)
    {
        var o = await OverpaidAsync("GEL", 10000.01m);
        Assert.Equal($"/policies/{o.P.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, o.Accountant, o.P.Id.ToString(), "bank", "დაბრუნება"));
        var request = await w.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @id", new { id = o.P.Id });
        var underwriter = await GroupedUnderwriterAsync();
        var stored = await StoredAsync(o.P);
        var app = App;
        Task<string?> Approve() => PaymentActions.ApprovePremiumRefundRequestAsync(app, RatesOf(app), underwriter, o.P.Id.ToString(), request.ToString());
        faults.Reset();
        var sentinel = new InjectedFault($"FD_REFUND_{label}_{name}");
        faults.FailOnce(Faults.Read(label, name), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(Approve));
        Assert.Equal(label == "binding", faults.BegunATransaction);
        Assert.Equal(stored, await StoredAsync(o.P));
        Assert.Empty(await RefundsAsync(o.P));
        Assert.Equal($"/policies/{o.P.Id}?refund=approved", await Approve());
        Assert.Equal([(-10000.01m, underwriter.Id)], await RefundsAsync(o.P));
        Assert.Equal($"/policies/{o.P.Id}?refund=already", await Approve());
        Assert.Single(await RefundsAsync(o.P));
    }

    /// <summary>A receipt's reversal request: a receipt of 10 000.01 GEL allocated to one instalment, its reversal asked by the accountant.</summary>
    [Theory]
    [MemberData(nameof(Faults.QuickAndBinding), MemberType = typeof(Faults))]
    public async Task Receipt_reversal_request_the_approvals_permission_read_fails_the_same_error_nothing_written_the_retry_approves_once(string label, string name)
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var p = await w.PendingPolicyAsync(holder, MonthStart, "GEL", 1m, 100000m, [(0, 100000m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        var inst = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = p.Id });
        var receipt = await w.ReceiptAsync(holder, Today, 10000.01m, reference: "ჩარიცხვა");
        await using (var c = await db.OpenAppAsync())
            await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @inst, 10000.01, 10000.01, 1, @today)",
                new { receipt, inst, today = Today });
        Assert.Equal($"/policies/{p.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, accountant, receipt.ToString(), p.Id.ToString(), "შეცდომა"));
        var request = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @receipt", new { receipt });
        var underwriter = await GroupedUnderwriterAsync();
        var stored = await StoredAsync(p);
        Task<List<(decimal Gel, Guid By)>> Mirrors() => w.QueryAsync<(decimal, Guid)>("select amount_gel, created_by from core.payment where reverses_payment_id = @receipt", new { receipt });
        var app = App;
        Task<string?> Approve() => PaymentActions.ApproveReceiptReversalRequestAsync(app, underwriter, p.Id.ToString(), request.ToString());
        faults.Reset();
        var sentinel = new InjectedFault($"FD_REVERSAL_{label}_{name}");
        faults.FailOnce(Faults.Read(label, name), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(Approve));
        Assert.Equal(label == "binding", faults.BegunATransaction);
        Assert.Equal(stored, await StoredAsync(p));
        Assert.Empty(await Mirrors());
        Assert.Equal($"/policies/{p.Id}?reversal=approved", await Approve());
        Assert.Equal([(-10000.01m, underwriter.Id)], await Mirrors());
        Assert.Equal($"/policies/{p.Id}?reversal=already", await Approve());
        Assert.Single(await Mirrors());
    }
}
