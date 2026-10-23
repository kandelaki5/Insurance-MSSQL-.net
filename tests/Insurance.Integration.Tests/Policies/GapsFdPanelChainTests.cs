using System.Numerics;
using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The financial director's panel over the rows the real actions write (legacy fd-net-transfer-chain.test.ts, "the panel
/// and the real claim actions on one store" and "the panel and the real refund and receipt reversal actions"): the
/// panel's pending lists (FdPanel.ReadPendingQueueAsync, what the panel page lists) and its month figures (the monthly
/// report, what the panel page shows) after PayClaimAsync / ApproveClaimPaymentRequestAsync / ReverseClaimPaymentAsync,
/// RefundOverpaymentAsync / ApprovePremiumRefundRequestAsync and ReversePaymentAsync / ApproveReceiptReversalRequestAsync.
/// Each test on a database of its own (the report counts every row). The claim chain keeps the legacy days (the claim
/// actions take today); the refund and reversal chains run in today's month (the payment actions read the real day).
/// </summary>
public sealed class GapsFdPanelChainTests
{
    private const string Fd = "payment.large.authorize";

    private static Task<FdPanel.QueueRead> QueueAsync(World w, string queue) => w.App.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, queue, null));

    private static async Task<List<(Guid Request, string Card, string Amount)>> ListedAsync(World w, string queue) =>
        (await QueueAsync(w, queue)).Rows.Select(r => (r.Id, r.CardNumber, FdPanel.AmountText(r.AmountGel))).ToList();

    private static async Task<(string Paid, string Rbns)> ClaimFiguresAsync(World w, string month)
    {
        var r = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, month));
        return (PremiumReport.ReportMoney(r.ClaimsTotals.PaidGel), PremiumReport.ReportMoney(r.ClaimsTotals.RbnsGel));
    }

    // ---------------------------------------------------------------- claims

    /// <summary>The claim world (October closed), the ordinary 500.00 (all deductible) and the 10 500.02 request; Beka (an underwriter) authorises by a group.</summary>
    private static async Task<(FdClaimWorld F, FdClaimWorld.Claim C, CurrentUser Accountant, CurrentUser Beka, Guid Request)> RequestedAsync(World w)
    {
        var f = new FdClaimWorld(w.Database);
        await f.RatesAsync();
        var c = await f.ClaimAsync();
        var a = await f.C.UserAsync("accountant");
        var beka = await f.C.UserAsync("underwriter", "ბექა");
        await f.C.GroupAsync(Fd, beka.Id);
        await f.C.CloseMonthAsync(a.Id, new DateOnly(2026, 10, 1));
        Assert.Equal($"/claims/{c.Id}?paid=1", (await f.PayAsync(a, c.Id, "500.00")).RedirectTo);
        Assert.Equal(("500.00", "19,500.00"), await ClaimFiguresAsync(w, "2026-11"));
        Assert.Equal($"/claims/{c.Id}?payment=requested", (await f.PayAsync(a, c.Id, "10500.02")).RedirectTo);
        return (f, c, a, beka, await f.RequestIdAsync(c.Id));
    }

    private static async Task<string> ClaimNumberAsync(FdClaimWorld f, Guid claim) => await f.C.ScalarAsync<string>("select number from core.claim where id = @claim", new { claim });

    [Fact]
    public async Task Claim_asked_listed_once_with_its_net_no_execution_row_the_months_figures_as_before_the_request() =>
        await OwnDatabaseAsync(async w =>
        {
            var (f, c, _, _, request) = await RequestedAsync(w);
            Assert.Equal([(request, await ClaimNumberAsync(f, c.Id), "10,000.02")], await ListedAsync(w, "claim"));
            Assert.Equal([(10500.02m, 500m, 10000.02m, 10000.02m)], (await f.RequestsAsync(c.Id)).Select(q => (q.Amount, q.DeductibleWithheld, q.NetPaid, q.NetPaidGel)));
            Assert.Equal([(500m, 0m, (Guid?)null)], (await f.PaymentsAsync(c.Id)).Select(p => (p.Amount, p.NetPaid, p.RequestId)));
            Assert.Equal(("500.00", "19,500.00"), await ClaimFiguresAsync(w, "2026-11"));
        });

    [Fact]
    public async Task Claim_approved_by_the_financial_director_one_payment_carrying_the_request_off_the_list_paid_11000_02_RBNS_8999_98_a_retry_writes_nothing() =>
        await OwnDatabaseAsync(async w =>
        {
            var (f, c, a, beka, request) = await RequestedAsync(w);
            Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(beka, c.Id, request));
            Assert.Equal([(500m, 500m, 0m, (Guid?)null, (Guid?)a.Id), (10500.02m, 500m, 10000.02m, request, beka.Id)],
                (await f.PaymentsAsync(c.Id)).Select(p => (p.Amount, p.DeductibleWithheld, p.NetPaidGel, p.RequestId, p.CreatedBy)));
            Assert.Equal([(FdClaimWorld.Today, -500m, (Guid?)a.Id), (FdClaimWorld.Today, -10500.02m, beka.Id)], (await f.ReservesAsync(c.Id)).Skip(1));
            Assert.Empty(await ListedAsync(w, "claim"));
            Assert.Equal(("11,000.02", "8,999.98"), await ClaimFiguresAsync(w, "2026-11"));
            var done = await f.LedgerAsync(c);
            Assert.Equal($"/claims/{c.Id}?payment=already", await f.AuthoriseAsync(beka, c.Id, request));
            Assert.Equal(done, await f.LedgerAsync(c));
        });

    [Fact]
    public async Task Claim_reversed_in_the_next_open_month_by_the_real_correction_the_request_not_back_on_the_list_November_as_it_was_December_by_hand() =>
        await OwnDatabaseAsync(async w =>
        {
            var (f, c, a, beka, request) = await RequestedAsync(w);
            Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(beka, c.Id, request));
            var november = await ClaimFiguresAsync(w, "2026-11");
            var approved = await f.C.ScalarAsync<Guid>("select id from core.claim_payment where request_id = @request", new { request });
            var december = new DateOnly(2026, 12, 2);
            Assert.Equal($"/claims/{c.Id}?reversal=ok", await ClaimActions.ReverseClaimPaymentAsync(f.C.App, a, ClaimWorld.Form(("claim_id", c.Id.ToString()),
                ("claim_payment_id", approved.ToString()), ("reason", "correction"), ("reversed_on", ClaimWorld.D(december)), ("note", "შეცდომით ჩაიწერა")), december));
            Assert.Equal([(december, -10500.02m, -10000.02m, (Guid?)null)], await f.C.RowsAsync<(DateOnly, decimal, decimal, Guid?)>(
                "select paid_on, amount, net_paid_gel, request_id from core.claim_payment where reverses_claim_payment_id = @approved", new { approved }));
            Assert.Equal((december, 10500.02m, (Guid?)a.Id), (await f.ReservesAsync(c.Id))[^1]);
            Assert.Equal((10500.02m, request), (await f.C.RowsAsync<(decimal, Guid)>("select amount, request_id from core.claim_payment where id = @approved", new { approved })).Single());
            Assert.Equal(november, await ClaimFiguresAsync(w, "2026-11"));
            Assert.Equal(("-10,500.02", "19,500.00"), await ClaimFiguresAsync(w, "2026-12"));
            Assert.Empty(await ListedAsync(w, "claim"));
        });

    [Fact]
    public async Task Claim_seeing_is_not_authorising_a_right_taken_away_after_the_request_stops_the_approval_the_request_still_listed() =>
        await OwnDatabaseAsync(async w =>
        {
            var (f, c, _, beka, request) = await RequestedAsync(w);
            var viewer = await f.C.UserAsync("viewer");
            await f.C.GrantAsync(viewer.Id, "finance.panel.view", "allow");
            Assert.True(await PermissionStore.HasPermissionAsync(w.App, viewer, "finance.panel.view"));
            var asked = await f.LedgerAsync(c);
            Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(viewer, c.Id, request));
            await f.C.ExecAsync("delete from core.user_group_permission where permission = @Fd", new { Fd });
            Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(beka, c.Id, request));
            await f.C.ExecAsync("insert core.user_group_permission (group_id, permission) select group_id, @Fd from core.user_group_member where user_id = @beka", new { Fd, beka = beka.Id });
            await f.C.GrantAsync(beka.Id, Fd, "deny");
            Assert.Equal($"/claims/{c.Id}?payment=not-allowed", await f.AuthoriseAsync(beka, c.Id, request));
            Assert.Equal(asked, await f.LedgerAsync(c));
            Assert.Equal([request], (await ListedAsync(w, "claim")).Select(r => r.Request));
        });

    [Fact]
    public async Task Claim_the_approval_failing_inside_its_transaction_leaves_nothing_the_request_still_listed_the_figures_as_before_the_retry_once() =>
        await OwnDatabaseAsync(async w =>
        {
            var (f, c, _, beka, request) = await RequestedAsync(w);
            var figures = await ClaimFiguresAsync(w, "2026-11");
            var asked = await f.LedgerAsync(c);
            await ArmAsync(w.Database, "claim_reserve");
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => f.AuthoriseAsync(beka, c.Id, request));
            }
            finally
            {
                await DisarmAsync(w.Database);
            }
            Assert.Equal(asked, await f.LedgerAsync(c));
            Assert.Equal([request], (await ListedAsync(w, "claim")).Select(r => r.Request));
            Assert.Equal(figures, await ClaimFiguresAsync(w, "2026-11"));
            Assert.Equal($"/claims/{c.Id}?payment=approved", await f.AuthoriseAsync(beka, c.Id, request));
            Assert.Single(await f.C.RowsAsync<Guid>("select id from core.claim_payment where request_id = @request", new { request }));
            Assert.Empty(await ListedAsync(w, "claim"));
        });

    // ---------------------------------------------------------------- refunds

    /// <summary>
    /// Premium 3 000.00 in three instalments of 1 000.00 due on the 1st and the 2nd of today's month and the 15th of next
    /// month; one receipt today of 12 000.01, allocated 6 000.00 to the first and 6 000.01 to the second. Overpaid today:
    /// 5 000.01 + 5 000.00 = 10 000.01 — a request whose plan has two lines (the later instalment first).
    /// </summary>
    private static async Task<(World.Policy P, CurrentUser Accountant, CurrentUser Beka, Guid[] Inst)> OverpaidAsync(World w)
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var beka = await w.UserAsync("underwriter");
        await GroupAsync(w, Fd, beka.Id);
        var holder = await w.ClientAsync();
        var from = MonthStart;
        var p = await w.PendingPolicyAsync(holder, from, premium: 3000m, schedule: [(0, 1000m), (1, 1000m), (from.AddMonths(1).AddDays(14).DayNumber - from.DayNumber, 1000m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        var inst = (await w.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = p.Id })).ToArray();
        var receipt = await w.ReceiptAsync(holder, World.Today, 12000.01m, reference: "ჩარიცხვა");
        await using var c = await w.Database.OpenAppAsync();
        foreach (var (i, amount) in new[] { (inst[0], 6000.00m), (inst[1], 6000.01m) })
            await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @i, @amount, @amount, 1, @today)",
                new { receipt, i, amount, today = World.Today });
        return (p, accountant, beka, inst);
    }

    private static async Task<PremiumReport.ReceivableRow?> OwedAsync(World w, string currency) =>
        (await MonthReportAsync(w, MonthStart)).Receivables.SingleOrDefault(r => r.Currency == currency);

    [Fact]
    public async Task Refund_asked_listed_once_with_its_stored_GEL_nothing_owed_nothing_written_approved_the_two_line_plan_once_off_the_list_1000_owed_a_retry_nothing() =>
        await OwnDatabaseAsync(async w =>
        {
            var (p, accountant, beka, inst) = await OverpaidAsync(w);
            var nothing = await OwedAsync(w, "GEL");
            Assert.True(nothing is null || (nothing.Outstanding, nothing.Overdue) == (BigInteger.Zero, BigInteger.Zero));
            Assert.Equal($"/policies/{p.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, accountant, p.Id.ToString(), "bank", "დაბრუნება"));
            var request = await w.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @id", new { id = p.Id });
            Assert.Equal([(1, inst[1], 5000.01m, 5000.01m), (2, inst[0], 5000.00m, 5000.00m)], await w.QueryAsync<(int, Guid, decimal, decimal)>(
                "select position, installment_id, amount, amount_gel from core.premium_refund_request_line where request_id = @request order by position", new { request }));
            Assert.Equal([(request, p.Number, "10,000.01")], await ListedAsync(w, "refund"));
            Assert.Empty(await ListedAsync(w, "claim"));
            Assert.Empty(await ListedAsync(w, "reversal"));
            Assert.Equal(nothing, await OwedAsync(w, "GEL"));
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.payment where kind = N'refund'"));

            Assert.Equal($"/policies/{p.Id}?refund=approved", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, beka, p.Id.ToString(), request.ToString()));
            var back = Assert.Single(await w.QueryAsync<(Guid Id, DateOnly On, decimal Gel, Guid Request, Guid By)>(
                "select id, received_on, amount_gel, refund_request_id, created_by from core.payment where kind = N'refund'"));
            Assert.Equal((World.Today, -10000.01m, request, beka.Id), (back.On, back.Gel, back.Request, back.By));
            Assert.Equal([(inst[1], -5000.01m, -5000.01m, 1m), (inst[0], -5000.00m, -5000.00m, 1m)], await w.QueryAsync<(Guid, decimal, decimal, decimal)>(
                "select installment_id, amount, amount_gel, rate from core.payment_allocation where payment_id = @id order by amount", new { id = back.Id }));
            Assert.Empty(await ListedAsync(w, "refund"));
            // 2 000.00 of credit left; the instalment of next month's 15th owed: 1 000.00, not overdue at the month's end.
            Assert.Equal((new BigInteger(100000), BigInteger.Zero, new BigInteger(100000)), (await OwedAsync(w, "GEL")) is { } o ? (o.Outstanding, o.Overdue, o.OutstandingGel) : default);
            var after = await LedgerAsync(w);
            Assert.Equal($"/policies/{p.Id}?refund=already", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, beka, p.Id.ToString(), request.ToString()));
            Assert.Equal(after, await LedgerAsync(w));
        });

    [Fact]
    public async Task Refund_two_requests_of_one_policy_carrying_out_the_first_takes_only_it_off_the_second_stays_listed() =>
        await OwnDatabaseAsync(async w =>
        {
            var (p, accountant, beka, _) = await OverpaidAsync(w);
            foreach (var _ in new[] { 0, 1 })
                Assert.Equal($"/policies/{p.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, accountant, p.Id.ToString(), "bank", "დაბრუნება"));
            var requests = await w.QueryAsync<Guid>("select id from core.premium_refund_request where policy_id = @id order by created_at", new { id = p.Id });
            Assert.Equal(requests.Select(r => (r, p.Number, "10,000.01")), await ListedAsync(w, "refund"));
            Assert.Equal($"/policies/{p.Id}?refund=approved", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, beka, p.Id.ToString(), requests[0].ToString()));
            Assert.Equal([(requests[1], p.Number, "10,000.01")], await ListedAsync(w, "refund"));
        });

    [Fact]
    public async Task Refund_the_view_right_does_not_approve_a_right_taken_away_stops_it_the_second_lines_write_fails_nothing_stays_still_listed_the_retry_once() =>
        await OwnDatabaseAsync(async w =>
        {
            var (p, accountant, beka, _) = await OverpaidAsync(w);
            Assert.Equal($"/policies/{p.Id}?refund=requested", await PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, accountant, p.Id.ToString(), "bank", "დაბრუნება"));
            var request = await w.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @id", new { id = p.Id });
            var viewer = await w.UserAsync("viewer");
            await DecideAsync(w, viewer.Id, "finance.panel.view", "allow");
            var asked = await LedgerAsync(w);
            Assert.Equal($"/policies/{p.Id}?refund=not-allowed", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, viewer, p.Id.ToString(), request.ToString()));
            await w.QueryAsync<int>("delete from core.user_group_permission where permission = @Fd; select 1", new { Fd });
            Assert.Equal($"/policies/{p.Id}?refund=not-allowed", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, beka, p.Id.ToString(), request.ToString()));
            await w.QueryAsync<int>("insert core.user_group_permission (group_id, permission) select group_id, @Fd from core.user_group_member where user_id = @beka; select 1", new { Fd, beka = beka.Id });
            await DecideAsync(w, beka.Id, Fd, "deny");
            Assert.Equal($"/policies/{p.Id}?refund=not-allowed", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, beka, p.Id.ToString(), request.ToString()));
            Assert.Equal(asked, await LedgerAsync(w));
            await InheritAsync(w, beka.Id, Fd);
            await ArmAsync(w.Database, "payment_allocation", 2);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, beka, p.Id.ToString(), request.ToString()));
            }
            finally
            {
                await DisarmAsync(w.Database);
            }
            Assert.Equal(asked, await LedgerAsync(w));
            Assert.Equal([request], (await ListedAsync(w, "refund")).Select(r => r.Request));
            Assert.Equal($"/policies/{p.Id}?refund=approved", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, beka, p.Id.ToString(), request.ToString()));
            Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where refund_request_id = @request", new { request }));
            Assert.Empty(await ListedAsync(w, "refund"));
        });

    // ---------------------------------------------------------------- receipt reversals

    /// <summary>
    /// USD, two policies of one client: 5 000.00 owed on A and 3 000.00 on B, both due on the first of today's month; a
    /// receipt today of 21 600.00 GEL at 2.7, allocated 5 000.00 (13 500.00 GEL) and 3 000.00 (8 100.00 GEL). The month's
    /// end at 2.75.
    /// </summary>
    private static async Task<(World.Policy A, World.Policy B, Guid InstA, Guid InstB, Guid Receipt, CurrentUser Accountant, CurrentUser Beka)> ReceivedAsync(World w)
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var beka = await w.UserAsync("underwriter");
        await GroupAsync(w, Fd, beka.Id);
        var holder = await w.ClientAsync();
        await w.RateAsync("USD", MonthEnd(MonthStart), 2.75m);
        var a = await w.PendingPolicyAsync(holder, MonthStart, "USD", 2.7m, 5000m, [(0, 5000m)]);
        var b = await w.PendingPolicyAsync(holder, MonthStart, "USD", 2.7m, 3000m, [(0, 3000m)]);
        foreach (var p in new[] { a, b }) Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        var instA = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = a.Id });
        var instB = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = b.Id });
        var receipt = await w.ReceiptAsync(holder, World.Today, 21600m, reference: "ჩარიცხვა");
        await using var c = await w.Database.OpenAppAsync();
        foreach (var (i, amount, gel) in new[] { (instA, 5000m, 13500m), (instB, 3000m, 8100m) })
            await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @i, @amount, @gel, 2.7, @today)",
                new { receipt, i, amount, gel, today = World.Today });
        return (a, b, instA, instB, receipt, accountant, beka);
    }

    [Fact]
    public async Task Reversal_USD_two_policies_asked_listed_once_with_the_stored_GEL_approved_the_exact_mirror_at_the_stored_rate_8000_owed_and_overdue_at_2_75_is_22000() =>
        await OwnDatabaseAsync(async w =>
        {
            var r = await ReceivedAsync(w);
            var before = await OwedAsync(w, "USD");
            Assert.True(before is null || before.Outstanding == 0);
            Assert.Equal($"/policies/{r.A.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, r.Accountant, r.Receipt.ToString(), r.A.Id.ToString(), "შეცდომა"));
            var request = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @receipt", new { receipt = r.Receipt });
            var listed = Assert.Single((await QueueAsync(w, "reversal")).Rows);
            Assert.Equal((request, r.A.Number, (Guid?)r.Receipt, "21,600.00"), (listed.Id, listed.CardNumber, listed.ReceiptId, FdPanel.AmountText(listed.AmountGel)));
            Assert.Empty(await ListedAsync(w, "refund"));
            Assert.Equal(before, await OwedAsync(w, "USD"));
            Assert.Equal($"/policies/{r.A.Id}?reversal=approved", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, r.Beka, r.A.Id.ToString(), request.ToString()));
            var mirror = Assert.Single(await w.QueryAsync<(Guid Id, DateOnly On, decimal Gel, Guid Request, Guid By)>(
                "select id, received_on, amount_gel, reversal_request_id, created_by from core.payment where reverses_payment_id = @receipt", new { receipt = r.Receipt }));
            Assert.Equal((World.Today, -21600m, request, r.Beka.Id), (mirror.On, mirror.Gel, mirror.Request, mirror.By));
            Assert.Equal([(r.InstA, -5000m, -13500m, 2.7m), (r.InstB, -3000m, -8100m, 2.7m)], await w.QueryAsync<(Guid, decimal, decimal, decimal)>(
                "select installment_id, amount, amount_gel, rate from core.payment_allocation where payment_id = @id order by amount", new { id = mirror.Id }));
            Assert.Empty(await ListedAsync(w, "reversal"));
            Assert.Equal(new PremiumReport.ReceivableRow("USD", 800000, 800000, 2200000, 2.75m, MonthEnd(MonthStart)), await OwedAsync(w, "USD"));
            var after = await LedgerAsync(w);
            Assert.Equal($"/policies/{r.A.Id}?reversal=already", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, r.Beka, r.A.Id.ToString(), request.ToString()));
            Assert.Equal(after, await LedgerAsync(w));
        });

    [Fact]
    public async Task Reversal_the_second_lines_write_fails_nothing_stays_still_listed_nothing_owed_the_retry_carries_it_out_once() =>
        await OwnDatabaseAsync(async w =>
        {
            var r = await ReceivedAsync(w);
            Assert.Equal($"/policies/{r.A.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, r.Accountant, r.Receipt.ToString(), r.A.Id.ToString(), "შეცდომა"));
            var request = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @receipt", new { receipt = r.Receipt });
            var asked = await LedgerAsync(w);
            var owed = await OwedAsync(w, "USD");
            await ArmAsync(w.Database, "payment_allocation", 2);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => PaymentActions.ApproveReceiptReversalRequestAsync(w.App, r.Beka, r.A.Id.ToString(), request.ToString()));
            }
            finally
            {
                await DisarmAsync(w.Database);
            }
            Assert.Equal(asked, await LedgerAsync(w));
            Assert.Equal([request], (await QueueAsync(w, "reversal")).Rows.Select(x => x.Id));
            Assert.Equal(owed, await OwedAsync(w, "USD"));
            Assert.Equal($"/policies/{r.A.Id}?reversal=approved", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, r.Beka, r.A.Id.ToString(), request.ToString()));
            Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where reversal_request_id = @request", new { request }));
            Assert.Empty(await ListedAsync(w, "reversal"));
        });
}
