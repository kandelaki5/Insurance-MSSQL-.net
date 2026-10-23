using System.Globalization;
using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// A new receipt from start to end through the real actions (legacy fd-net-transfer-chain.test.ts "a new receipt, its
/// reversal asked for and approved by the financial director" and "its advance taken by a second policy", the exact
/// refund cases, and precision-fd-panel-chain.test.ts): RecordPaymentAsync records it, AllocateReceiptAsync lets the
/// client's second policy take what is left, ReversePaymentAsync asks for the reversal above the threshold (10 000.00),
/// FdPanel lists the request, ApproveReceiptReversalRequestAsync writes the mirror. The legacy receipt day 20.10 and the
/// request day 03.11 are both today here, and the approval "the next day" (04.11) is the same day: the mirror is dated on
/// the request's day either way, but a later approval day itself is not reproduced (no settable clock).
/// </summary>
public sealed class GapsReceiptChainTests(TestDatabase db) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private readonly World w = new(db);
    private static DateOnly Today => World.Today;
    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    public async Task InitializeAsync()
    {
        await w.SettingAsync("payment.fd_threshold_gel", "10000.00");
        await w.RateAsync("USD", Today, 2.7m);
        await w.RateAsync("EUR", Today, 3.1234m);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<World.Policy> PolicyAsync(CurrentUser admin, Guid holder, string currency, params (int Day, string Amount)[] schedule)
    {
        var rate = currency == "GEL" ? 1m : 2.7m;
        var p = await w.PendingPolicyAsync(holder, MonthStart, currency, rate, schedule.Sum(s => D(s.Amount)), [.. schedule.Select(s => (s.Day, D(s.Amount)))]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        return p;
    }

    private static int NextMonth => MonthStart.AddMonths(1).DayNumber - MonthStart.DayNumber;

    private Task<List<Guid>> InstalmentsAsync(Guid policy) => w.QueryAsync<Guid>("select id from core.installment where policy_id = @policy order by due_on", new { policy });

    private Task<List<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate, DateOnly BookedOn)>> LinesAsync(Guid payment) =>
        w.QueryAsync<(Guid, decimal, decimal, decimal, DateOnly)>("select installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @payment order by amount_gel desc", new { payment });

    private Task<Guid> ReceiptOfAsync(string reference) => w.ScalarAsync<Guid>("select id from core.payment where reference = @reference and kind = N'receipt'", new { reference });

    private Task<List<(Guid Id, DateOnly On, decimal Gel, string Kind, Guid By, Guid? Request)>> MirrorsAsync(Guid receipt) => w.QueryAsync<(Guid, DateOnly, decimal, string, Guid, Guid?)>(
        "select id, received_on, amount_gel, kind, created_by, reversal_request_id from core.payment where reverses_payment_id = @receipt", new { receipt });

    private Task<string> RowsOfAsync(Guid receipt) => w.ScalarAsync<string>("""
        select concat((select count(*) from core.payment where id = @receipt or reverses_payment_id = @receipt), '/',
                      (select count(*) from core.payment_allocation a join core.payment p on p.id = a.payment_id where p.id = @receipt or p.reverses_payment_id = @receipt), '/',
                      (select count(*) from core.receipt_reversal_request where receipt_id = @receipt), '/',
                      (select count(*) from core.receipt_reversal_request_line l join core.receipt_reversal_request q on q.id = l.request_id where q.receipt_id = @receipt))
        """, new { receipt });

    /// <summary>The panel's reversal list, this receipt's rows only: request, card, receipt, stored GEL.</summary>
    private async Task<List<(Guid Request, string Card, Guid? Receipt, string Amount)>> ListedAsync(params Guid[] receipts) =>
        (await w.App.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, "reversal", null))).Rows.Where(r => r.ReceiptId is { } id && receipts.Contains(id))
            .Select(r => (r.Id, r.CardNumber, r.ReceiptId, FdPanel.AmountText(r.AmountGel))).ToList();

    private Task<string?> ReverseAsync(CurrentUser user, Guid receipt, World.Policy from, string reason = "შეცდომით ჩაირიცხა") =>
        PaymentActions.ReversePaymentAsync(w.App, user, receipt.ToString(), from.Id.ToString(), reason);

    private Task<Guid> RequestOfAsync(Guid receipt) => w.ScalarAsync<Guid>("select top (1) id from core.receipt_reversal_request where receipt_id = @receipt order by created_at", new { receipt });

    private Task<string?> ApproveAsync(CurrentUser user, World.Policy from, Guid request) =>
        PaymentActions.ApproveReceiptReversalRequestAsync(w.App, user, from.Id.ToString(), request.ToString());

    // ---------------------------------------------------------------- one policy, a new receipt and its reversal

    [Fact]
    public async Task GEL_90071992547408_01_over_two_instalments_recorded_asked_refused_to_the_accountant_approved_exact_mirror_once_the_debt_back()
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var p = await PolicyAsync(admin, await w.ClientAsync(), "GEL", (0, "90071992547408.00"), (NextMonth, "0.01"));
        var inst = await InstalmentsAsync(p.Id);
        // 1. The receipt: 90071992547408.00 to the first instalment, 0.01 to the second, oldest first; nothing left.
        Assert.Equal($"/policies/{p.Id}?paid=0", (await w.PayAsync(accountant, p.Id, "90071992547408.01", Today, reference: "R-NEW")).Redirect);
        var r = await ReceiptOfAsync("R-NEW");
        Assert.Equal([(Today, 90071992547408.01m, accountant.Id)], await w.QueryAsync<(DateOnly, decimal, Guid)>("select received_on, amount_gel, created_by from core.payment where id = @r", new { r }));
        Assert.Equal([(inst[0], 90071992547408.00m, 90071992547408.00m, 1m, Today), (inst[1], 0.01m, 0.01m, 1m, Today)], await LinesAsync(r));
        var original = await LinesAsync(r);
        // 2. Its reversal above 10 000.00: a request with both lines as stored; no money moves.
        Assert.Equal($"/policies/{p.Id}?reversal=requested", await ReverseAsync(accountant, r, p));
        var request = await RequestOfAsync(r);
        Assert.Equal([(Today, 90071992547408.01m, "შეცდომით ჩაირიცხა", accountant.Id)], await w.QueryAsync<(DateOnly, decimal, string, Guid)>(
            "select reversed_on, amount_gel, reason, created_by from core.receipt_reversal_request where id = @request", new { request }));
        Assert.Equal([(inst[0], 90071992547408.00m, 90071992547408.00m, 1m), (inst[1], 0.01m, 0.01m, 1m)], await w.QueryAsync<(Guid, decimal, decimal, decimal)>(
            "select installment_id, amount, amount_gel, rate from core.receipt_reversal_request_line where request_id = @request order by amount desc", new { request }));
        Assert.Empty(await MirrorsAsync(r));
        Assert.Equal(0m, await w.OwedAsync(p.Id));
        // 3. The accountant may not approve; 4. the admin approves: the mirror on the request's day, both lines negated.
        Assert.Equal($"/policies/{p.Id}?reversal=not-allowed", await ApproveAsync(accountant, p, request));
        Assert.Empty(await MirrorsAsync(r));
        Assert.Equal($"/policies/{p.Id}?reversal=approved", await ApproveAsync(admin, p, request));
        var m = Assert.Single(await MirrorsAsync(r));
        Assert.Equal((Today, -90071992547408.01m, "reversal", admin.Id, (Guid?)request), (m.On, m.Gel, m.Kind, m.By, m.Request));
        Assert.Equal([(inst[1], -0.01m, -0.01m, 1m, Today), (inst[0], -90071992547408.00m, -90071992547408.00m, 1m, Today)], await LinesAsync(m.Id));
        Assert.Equal(original, await LinesAsync(r));
        // 5. Again: already. 6. The debt back to the tetri; the reversed receipt is no advance.
        Assert.Equal($"/policies/{p.Id}?reversal=already", await ApproveAsync(admin, p, request));
        Assert.Single(await MirrorsAsync(r));
        Assert.Equal(90071992547408.01m, await w.OwedAsync(p.Id));
        Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, p.Holder)));
    }

    [Fact]
    public async Task USD_the_mirror_is_the_stored_GEL_at_the_receipt_days_2_7()
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var p = await PolicyAsync(admin, await w.ClientAsync(), "USD", (0, "5000.00"), (NextMonth, "100.00"));
        var inst = await InstalmentsAsync(p.Id);
        // 10 000.01 GEL at 2.7 = 3 703.707… → 3 703.71 USD on the first instalment, the line taking all 10 000.01 GEL.
        Assert.Equal($"/policies/{p.Id}?paid=0", (await w.PayAsync(accountant, p.Id, "10000.01", Today, reference: "R-USD")).Redirect);
        var r = await ReceiptOfAsync("R-USD");
        Assert.Equal([(inst[0], 3703.71m, 10000.01m, 2.7m, Today)], await LinesAsync(r));
        Assert.Equal($"/policies/{p.Id}?reversal=requested", await ReverseAsync(accountant, r, p));
        Assert.Equal($"/policies/{p.Id}?reversal=approved", await ApproveAsync(admin, p, await RequestOfAsync(r)));
        var m = Assert.Single(await MirrorsAsync(r));
        Assert.Equal(-10000.01m, m.Gel);
        Assert.Equal([(inst[0], -3703.71m, -10000.01m, 2.7m, Today)], await LinesAsync(m.Id));
        Assert.Equal(5100m, await w.OwedAsync(p.Id));
    }

    [Fact]
    public async Task Stale_one_tetri_moved_between_the_receipts_lines_after_the_request_nothing_reversed()
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var p = await PolicyAsync(admin, await w.ClientAsync(), "GEL", (0, "90071992547408.00"), (NextMonth, "0.01"));
        var inst = await InstalmentsAsync(p.Id);
        await w.PayAsync(accountant, p.Id, "90071992547408.01", Today, reference: "R-STALE");
        var r = await ReceiptOfAsync("R-STALE");
        Assert.Equal($"/policies/{p.Id}?reversal=requested", await ReverseAsync(accountant, r, p));
        await using (var c = await w.Database.OpenAppAsync())
            await c.ExecuteAsync("""
                insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@r, @i2, -0.01, -0.01, 1, @today);
                insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@r, @i1, 0.01, 0.01, 1, @today);
                """, new { r, i1 = inst[0], i2 = inst[1], today = Today });
        Assert.Equal($"/policies/{p.Id}?reversal=stale", await ApproveAsync(admin, p, await RequestOfAsync(r)));
        Assert.Empty(await MirrorsAsync(r));

        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.receipt_reversal_request where receipt_id = @r", new { r }));
    }

    [Fact]
    public async Task A_fault_on_the_second_mirror_line_leaves_nothing_the_retry_approves_once_with_both_lines()
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var q = await PolicyAsync(admin, await w.ClientAsync(), "GEL", (0, "90071992547408.00"), (NextMonth, "0.01"));
        await w.PayAsync(accountant, q.Id, "90071992547408.01", Today, reference: "R-FAULT2");
        var f = await ReceiptOfAsync("R-FAULT2");
        Assert.Equal($"/policies/{q.Id}?reversal=requested", await ReverseAsync(accountant, f, q));
        var request = await RequestOfAsync(f);
        var before = await RowsOfAsync(f);
        await ArmAsync(w.Database, "payment_allocation", 2);
        try
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => ApproveAsync(admin, q, request));
            Assert.Contains("injected failure", e.Message);
        }
        finally
        {
            await DisarmAsync(w.Database);
        }
        Assert.Equal(before, await RowsOfAsync(f));
        Assert.Equal($"/policies/{q.Id}?reversal=approved", await ApproveAsync(admin, q, request));
        var m = Assert.Single(await MirrorsAsync(f));
        Assert.Equal(2, (await LinesAsync(m.Id)).Count);
    }

    // ---------------------------------------------------------------- the advance taken by the client's second policy

    private sealed record Cross(CurrentUser Admin, CurrentUser Accountant, World.Policy A, World.Policy B, Guid IA, Guid IB, Guid R);

    /// <summary>A (GEL) owing <paramref name="a"/> and B (<paramref name="currency"/>) owing <paramref name="b"/>, one client; <paramref name="received"/> recorded today for A.</summary>
    private async Task<Cross> CrossAsync(string a, string b, string received, string currency = "GEL", bool requestFirst = false, bool take = true)
    {
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var pa = await PolicyAsync(admin, holder, "GEL", (0, a));
        var pb = await PolicyAsync(admin, holder, currency, (NextMonth, b));
        var reference = "R-CROSS-" + Guid.NewGuid().ToString("N")[..6];
        var left = (D(received) - D(a)) * 100;
        Assert.Equal($"/policies/{pa.Id}?paid={left:0}", (await w.PayAsync(accountant, pa.Id, received, Today, reference: reference)).Redirect);
        var r = await ReceiptOfAsync(reference);
        var ia = (await InstalmentsAsync(pa.Id)).Single();
        var ib = (await InstalmentsAsync(pb.Id)).Single();
        Assert.Equal([(ia, D(a), D(a), 1m, Today)], await LinesAsync(r));
        async Task TakeAsync()
        {
            var advance = Assert.Single(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, holder)));
            Assert.Equal((r, D(received), D(received) - D(a)), (advance.Id, advance.AmountGel, advance.AvailableGel));
            Assert.Equal($"/policies/{pb.Id}?advance=allocated&gel={left:0}&left=0", await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, accountant, pb.Id.ToString(), r.ToString()));
        }
        async Task AskAsync() => Assert.Equal($"/policies/{pa.Id}?reversal=requested", await ReverseAsync(accountant, r, pa));
        if (take)
        {
            if (requestFirst) { await AskAsync(); await TakeAsync(); }
            else { await TakeAsync(); await AskAsync(); }
        }
        // One receipt; its first line as recorded.
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @holder", new { holder }));
        return new Cross(admin, accountant, pa, pb, ia, ib, r);
    }

    [Theory]
    [InlineData("10000.00", "0.01", "10000.01")]
    [InlineData("90071992547408.00", "0.01", "90071992547408.01")]
    [InlineData("9999999999999999.98", "0.01", "9999999999999999.99")]
    public async Task GEL_received_for_A_B_takes_what_is_left_the_reversal_listed_once_with_its_whole_stored_GEL_approved_both_debts_back_again_already(string a, string b, string received)
    {
        var x = await CrossAsync(a, b, received);
        Assert.Equal([(x.IA, D(a), D(a), 1m, Today), (x.IB, D(b), D(b), 1m, Today)], await LinesAsync(x.R));
        var request = await RequestOfAsync(x.R);
        Assert.Equal([(1, x.IA, D(a)), (2, x.IB, D(b))], await w.QueryAsync<(int, Guid, decimal)>(
            "select position, installment_id, amount from core.receipt_reversal_request_line where request_id = @request order by position", new { request }));
        // Waiting: listed once, the whole receipt's GEL; no money moves.
        Assert.Equal([(request, x.A.Number, (Guid?)x.R, PremiumReport.ReportMoney(ReportMath.Tetri(D(received))))], await ListedAsync(x.R));
        Assert.Equal((0m, 0m), (await w.OwedAsync(x.A.Id), await w.OwedAsync(x.B.Id)));
        Assert.Equal($"/policies/{x.A.Id}?reversal=not-allowed", await ApproveAsync(x.Accountant, x.A, request));
        Assert.Empty(await MirrorsAsync(x.R));
        Assert.Equal($"/policies/{x.A.Id}?reversal=approved", await ApproveAsync(x.Admin, x.A, request));
        var m = Assert.Single(await MirrorsAsync(x.R));
        Assert.Equal((Today, -D(received), x.Admin.Id, (Guid?)request), (m.On, m.Gel, m.By, m.Request));
        Assert.Equal([(x.IB, -D(b), -D(b), 1m, Today), (x.IA, -D(a), -D(a), 1m, Today)], await LinesAsync(m.Id));
        Assert.Empty(await ListedAsync(x.R));
        Assert.Equal((D(a), D(b)), (await w.OwedAsync(x.A.Id), await w.OwedAsync(x.B.Id)));
        Assert.Equal($"/policies/{x.A.Id}?reversal=already", await ApproveAsync(x.Admin, x.A, request));
        Assert.Single(await MirrorsAsync(x.R));
        Assert.Empty(await ListedAsync(x.R));
        Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, x.A.Holder)));
    }

    [Fact]
    public async Task Two_requests_of_two_clients_each_listed_once_oldest_first_approving_one_takes_only_it_off()
    {
        var one = await CrossAsync("10000.00", "0.01", "10000.01");
        var two = await CrossAsync("90071992547408.00", "0.01", "90071992547408.01");
        var q1 = await RequestOfAsync(one.R);
        var q2 = await RequestOfAsync(two.R);
        Assert.Equal([(q1, "10,000.01"), (q2, "90,071,992,547,408.01")], (await ListedAsync(one.R, two.R)).Select(r => (r.Request, r.Amount)));
        Assert.Equal($"/policies/{one.A.Id}?reversal=approved", await ApproveAsync(one.Admin, one.A, q1));
        Assert.Equal([(q2, "90,071,992,547,408.01")], (await ListedAsync(one.R, two.R)).Select(r => (r.Request, r.Amount)));
    }

    [Fact]
    public async Task Stale_the_request_asked_before_B_took_the_advance_nothing_reversed_the_request_stays_listed_once()
    {
        var x = await CrossAsync("10000.00", "0.01", "10000.01", requestFirst: true);
        var request = await RequestOfAsync(x.R);
        Assert.Equal([(1, x.IA, 10000m)], await w.QueryAsync<(int, Guid, decimal)>(
            "select position, installment_id, amount from core.receipt_reversal_request_line where request_id = @request", new { request }));
        var before = await RowsOfAsync(x.R);
        Assert.Equal($"/policies/{x.A.Id}?reversal=stale", await ApproveAsync(x.Admin, x.A, request));
        Assert.Equal(before, await RowsOfAsync(x.R));
        Assert.Equal([request], (await ListedAsync(x.R)).Select(r => r.Request));
        Assert.Equal((0m, 0m), (await w.OwedAsync(x.A.Id), await w.OwedAsync(x.B.Id)));
    }

    [Fact]
    public async Task A_failure_on_the_mirrors_second_line_Bs_nothing_stays_still_listed_the_retry_reverses_once()
    {
        var x = await CrossAsync("10000.00", "0.01", "10000.01");
        var request = await RequestOfAsync(x.R);
        var before = await RowsOfAsync(x.R);
        await ArmAsync(w.Database, "payment_allocation", 2);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ApproveAsync(x.Admin, x.A, request));
        }
        finally
        {
            await DisarmAsync(w.Database);
        }
        Assert.Equal(before, await RowsOfAsync(x.R));
        Assert.Equal([request], (await ListedAsync(x.R)).Select(r => r.Request));
        Assert.Equal((0m, 0m), (await w.OwedAsync(x.A.Id), await w.OwedAsync(x.B.Id)));
        Assert.Equal($"/policies/{x.A.Id}?reversal=approved", await ApproveAsync(x.Admin, x.A, request));
        var m = Assert.Single(await MirrorsAsync(x.R));
        Assert.Equal([(x.IB, -0.01m), (x.IA, -10000m)], (await LinesAsync(m.Id)).Select(l => (l.Installment, l.Amount)));
        Assert.Empty(await ListedAsync(x.R));
        Assert.Equal((10000m, 0.01m), (await w.OwedAsync(x.A.Id), await w.OwedAsync(x.B.Id)));
    }

    [Fact]
    public async Task USD_10270_GEL_for_A_270_left_B_USD_100_takes_it_at_the_receipt_days_2_7_listed_10270_approved_at_the_stored_2_7_B_owes_USD_100_again()
    {
        var x = await CrossAsync("10000.00", "100.00", "10270.00", "USD");
        Assert.Equal([(x.IA, 10000m, 10000m, 1m, Today), (x.IB, 100m, 270m, 2.7m, Today)], await LinesAsync(x.R));
        var request = await RequestOfAsync(x.R);
        Assert.Equal(10270m, await w.ScalarAsync<decimal>("select amount_gel from core.receipt_reversal_request where id = @request", new { request }));
        Assert.Equal([(1, x.IA, 10000m, 10000m, 1m), (2, x.IB, 100m, 270m, 2.7m)], await w.QueryAsync<(int, Guid, decimal, decimal, decimal)>(
            "select position, installment_id, amount, amount_gel, rate from core.receipt_reversal_request_line where request_id = @request order by position", new { request }));
        Assert.Equal(["10,270.00"], (await ListedAsync(x.R)).Select(r => r.Amount));
        Assert.Equal($"/policies/{x.A.Id}?reversal=approved", await ApproveAsync(x.Admin, x.A, request));
        var m = Assert.Single(await MirrorsAsync(x.R));
        Assert.Equal((Today, -10270m), (m.On, m.Gel));
        Assert.Equal([(x.IB, -100m, -270m, 2.7m, Today), (x.IA, -10000m, -10000m, 1m, Today)], await LinesAsync(m.Id));
        Assert.Equal((10000m, 100m), (await w.OwedAsync(x.A.Id), await w.OwedAsync(x.B.Id)));
        Assert.Empty(await ListedAsync(x.R));
    }

    [Fact]
    public async Task Who_may_the_viewers_forged_allocation_writes_nothing_the_accountant_and_the_admin_denied_personally_may_not_approve_a_group_gives_it_to_an_underwriter()
    {
        var x = await CrossAsync("10000.00", "0.01", "10000.01", take: false);
        var viewer = await w.UserAsync("viewer");
        Assert.Equal($"/policies/{x.B.Id}?advance=read-only", await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, viewer, x.B.Id.ToString(), x.R.ToString()));
        Assert.Single(await LinesAsync(x.R));
        Assert.Equal($"/policies/{x.B.Id}?advance=allocated&gel=1&left=0", await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, x.Accountant, x.B.Id.ToString(), x.R.ToString()));
        Assert.Equal($"/policies/{x.A.Id}?reversal=requested", await ReverseAsync(x.Accountant, x.R, x.A));
        var request = await RequestOfAsync(x.R);
        Assert.Equal($"/policies/{x.A.Id}?reversal=not-allowed", await ApproveAsync(x.Accountant, x.A, request));
        await DecideAsync(w, x.Admin.Id, "payment.large.authorize", "deny");
        Assert.Equal($"/policies/{x.A.Id}?reversal=not-allowed", await ApproveAsync(x.Admin, x.A, request));
        Assert.Empty(await MirrorsAsync(x.R));
        var underwriter = await w.UserAsync("underwriter");
        await GroupAsync(w, "payment.large.authorize", underwriter.Id);
        Assert.Equal($"/policies/{x.A.Id}?reversal=approved", await ApproveAsync(underwriter, x.A, request));
        Assert.Equal([(-10000.01m, underwriter.Id)], (await MirrorsAsync(x.R)).Select(m => (m.Gel, m.By)));
        Assert.Equal((10000m, 0.01m), (await w.OwedAsync(x.A.Id), await w.OwedAsync(x.B.Id)));
    }

    // ---------------------------------------------------------------- a premium refund worked out exactly, in whole tetri

    /// <summary>A policy: instalment n of the amount, held as given by one receipt of 9999999999999999.99 received today; adjustments (n, change, day offset).</summary>
    private async Task<(CurrentUser Admin, CurrentUser Accountant, World.Policy P, List<Guid> Inst)> ExactAsync(string currency, string threshold,
        (string Amount, string Held, string? HeldGel)[] insts, (int Inst, string Change, int Day)[]? adjustments = null)
    {
        await w.SettingAsync("payment.fd_threshold_gel", threshold);
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var p = await PolicyAsync(admin, await w.ClientAsync(), currency, [.. insts.Select((x, i) => (i, x.Amount))]);
        var inst = await InstalmentsAsync(p.Id);
        var receipt = await w.ReceiptAsync(p.Holder, Today, 9999999999999999.99m, reference: "ჩარიცხვა");
        await using var c = await w.Database.OpenAppAsync();
        for (var i = 0; i < insts.Length; i++)
            await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @i, @amount, @gel, @rate, @today)",
                new { receipt, i = inst[i], amount = D(insts[i].Held), gel = D(insts[i].HeldGel ?? insts[i].Held), rate = currency == "GEL" ? 1m : 2.7m, today = Today });
        foreach (var (i, change, day) in adjustments ?? [])
            await c.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@i, @on, @change, N'შემცირება')",
                new { i = inst[i], on = Today.AddDays(day), change = D(change) });
        return (admin, accountant, p, inst);
    }

    private Task<string?> RefundAsync(CurrentUser user, World.Policy p) => PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, user, p.Id.ToString(), "bank", "დაბრუნება");

    private Task<List<(decimal Gel, Guid By, Guid? Request)>> RefundsAsync(World.Policy p) => w.QueryAsync<(decimal, Guid, Guid?)>(
        "select amount_gel, created_by, refund_request_id from core.payment where kind = N'refund' and payer_party_id = @h", new { h = p.Holder });

    private Task<List<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate)>> RefundLinesAsync(World.Policy p) => w.QueryAsync<(Guid, decimal, decimal, decimal)>("""
        select a.installment_id, a.amount, a.amount_gel, a.rate from core.payment_allocation a join core.payment r on r.id = a.payment_id join core.installment i on i.id = a.installment_id
        where r.kind = N'refund' and r.payer_party_id = @h order by i.due_on desc
        """, new { h = p.Holder });

    private Task<Guid> RefundRequestAsync(World.Policy p) => w.ScalarAsync<Guid>("select id from core.premium_refund_request where policy_id = @id", new { id = p.Id });

    [Fact]
    public async Task Exact_refund_held_02_on_an_instalment_of_01_gives_back_exactly_0_01_held_01_on_00_0_01()
    {
        foreach (var (amount, held) in new[] { ("90071992547408.01", "90071992547408.02"), ("90071992547408.00", "90071992547408.01") })
        {
            var x = await ExactAsync("GEL", "10000.00", [(amount, held, null)]);
            Assert.Null(await RefundAsync(x.Accountant, x.P));
            Assert.Equal([(-0.01m, x.Accountant.Id, (Guid?)null)], await RefundsAsync(x.P));
            Assert.Equal([(x.Inst[0], -0.01m, -0.01m, 1m)], await RefundLinesAsync(x.P));
        }
    }

    [Theory]
    [InlineData("90071992547408.01")]
    [InlineData("9999999999999999.99")]
    public async Task Exact_refund_a_whole_holding_of_the_largest_amounts_a_request_for_exactly_it_approved_paid_back_to_the_tetri(string amount)
    {
        var x = await ExactAsync("GEL", "10000.00", [(amount, amount, null)], [(0, "-" + amount, 0)]);
        Assert.Equal($"/policies/{x.P.Id}?refund=requested", await RefundAsync(x.Accountant, x.P));
        var request = await RefundRequestAsync(x.P);
        Assert.Equal([(Today, D(amount), "GEL", 1m)], await w.QueryAsync<(DateOnly, decimal, string, decimal)>(
            "select refunded_on, amount_gel, rtrim(currency), rate from core.premium_refund_request where id = @request", new { request }));
        Assert.Equal([(1, x.Inst[0], D(amount), D(amount))], await w.QueryAsync<(int, Guid, decimal, decimal)>(
            "select position, installment_id, amount, amount_gel from core.premium_refund_request_line where request_id = @request", new { request }));
        Assert.Empty(await RefundsAsync(x.P));
        Assert.Equal($"/policies/{x.P.Id}?refund=approved", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, x.Admin, x.P.Id.ToString(), request.ToString()));
        Assert.Equal([(-D(amount), x.Admin.Id, (Guid?)request)], await RefundsAsync(x.P));
        Assert.Equal([(x.Inst[0], -D(amount), -D(amount), 1m)], await RefundLinesAsync(x.P));
    }

    [Theory]
    [InlineData("10000.00", "90071992537408.01", "10000.00", "10000.01")]
    [InlineData("5000.00", "90071992542408.01", "5000.00", "5000.01")]
    public async Task Exact_refund_the_threshold_strictly_above_on_the_largest_amounts(string threshold, string owed, string direct, string asked)
    {
        var at = await ExactAsync("GEL", threshold, [(owed, "90071992547408.01", null)]);
        Assert.Null(await RefundAsync(at.Accountant, at.P));
        Assert.Equal([-D(direct)], (await RefundsAsync(at.P)).Select(r => r.Gel));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.premium_refund_request where policy_id = @id", new { id = at.P.Id }));
        var above = await ExactAsync("GEL", threshold, [(owed, "90071992547408.02", null)]);
        Assert.Equal($"/policies/{above.P.Id}?refund=requested", await RefundAsync(above.Accountant, above.P));
        Assert.Equal(D(asked), await w.ScalarAsync<decimal>("select amount_gel from core.premium_refund_request where policy_id = @id", new { id = above.P.Id }));
        Assert.Empty(await RefundsAsync(above.P));
        await w.SettingAsync("payment.fd_threshold_gel", "10000.00");
    }

    [Fact]
    public async Task Exact_refund_USD_33359997239780_01_at_2_7_is_90071992547406_03_a_request_approved_as_planned()
    {
        var x = await ExactAsync("USD", "10000.00", [("33359997239780.01", "33359997239780.01", "90071992547406.03")], [(0, "-33359997239780.01", 0)]);
        Assert.Equal($"/policies/{x.P.Id}?refund=requested", await RefundAsync(x.Accountant, x.P));
        var request = await RefundRequestAsync(x.P);
        Assert.Equal([(90071992547406.03m, "USD", 2.7m)], await w.QueryAsync<(decimal, string, decimal)>("select amount_gel, rtrim(currency), rate from core.premium_refund_request where id = @request", new { request }));
        Assert.Equal($"/policies/{x.P.Id}?refund=approved", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, x.Admin, x.P.Id.ToString(), request.ToString()));
        Assert.Equal([(x.Inst[0], -33359997239780.01m, -90071992547406.03m, 2.7m)], await RefundLinesAsync(x.P));
    }

    [Fact]
    public async Task Exact_refund_newest_instalment_first_each_line_the_step_between_running_sums_USD_0_01_twice_at_2_7_is_0_03_then_0_02()
    {
        var x = await ExactAsync("USD", "10000.00", [("1000.00", "1000.01", "2700.03"), ("1000.00", "1000.01", "2700.03")]);
        Assert.Null(await RefundAsync(x.Accountant, x.P));
        Assert.Equal([-0.05m], (await RefundsAsync(x.P)).Select(r => r.Gel));
        Assert.Equal([(x.Inst[1], -0.01m, -0.03m, 2.7m), (x.Inst[0], -0.01m, -0.02m, 2.7m)], await RefundLinesAsync(x.P));
    }

    [Fact]
    public async Task Exact_refund_only_what_is_in_force_on_the_day_H23_70_back_not_100_EUR_10_at_3_1234_is_31_23()
    {
        // −20.00 booked today counts; −30.00 booked ten days later not yet.
        var gel = await ExactAsync("GEL", "10000.00", [("100.00", "150.00", null)], [(0, "-20.00", 0), (0, "-30.00", 10)]);
        Assert.Null(await RefundAsync(gel.Accountant, gel.P));
        Assert.Equal([-70m], (await RefundsAsync(gel.P)).Select(r => r.Gel));
        var eur = await ExactAsync("EUR", "10000.00", [("100.00", "110.00", "297.00")]);
        Assert.Null(await RefundAsync(eur.Accountant, eur.P));
        Assert.Equal([(eur.Inst[0], -10m, -31.23m, 3.1234m)], await RefundLinesAsync(eur.P));
    }

    [Fact]
    public async Task Exact_refund_one_tetri_more_held_since_the_request_is_stale_nothing_written()
    {
        var x = await ExactAsync("GEL", "10000.00", [("90071992547408.01", "90071992547408.01", null)], [(0, "-90071992547408.01", 0)]);
        Assert.Equal($"/policies/{x.P.Id}?refund=requested", await RefundAsync(x.Accountant, x.P));
        var other = await w.ReceiptAsync(x.P.Holder, Today, 0.01m);
        await using (var c = await w.Database.OpenAppAsync())
            await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@other, @i, 0.01, 0.01, 1, @today)",
                new { other, i = x.Inst[0], today = Today });
        Assert.Equal($"/policies/{x.P.Id}?refund=stale", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, x.Admin, x.P.Id.ToString(), (await RefundRequestAsync(x.P)).ToString()));
        Assert.Empty(await RefundsAsync(x.P));
    }

    [Fact]
    public async Task Exact_refund_the_second_line_fails_on_approval_nothing_stays_approved_again_once_a_third_time_already()
    {
        var x = await ExactAsync("USD", "0.01", [("1000.00", "1000.01", "2700.03"), ("1000.00", "1000.01", "2700.03")]);
        Assert.Equal($"/policies/{x.P.Id}?refund=requested", await RefundAsync(x.Accountant, x.P));
        var request = await RefundRequestAsync(x.P);
        await ArmAsync(w.Database, "payment_allocation", 2);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, x.Admin, x.P.Id.ToString(), request.ToString()));
        }
        finally
        {
            await DisarmAsync(w.Database);
            await w.SettingAsync("payment.fd_threshold_gel", "10000.00");
        }
        Assert.Empty(await RefundsAsync(x.P));
        Assert.Equal($"/policies/{x.P.Id}?refund=approved", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, x.Admin, x.P.Id.ToString(), request.ToString()));
        Assert.Equal([(x.Inst[1], -0.01m, -0.03m, 2.7m), (x.Inst[0], -0.01m, -0.02m, 2.7m)], await RefundLinesAsync(x.P));
        Assert.Equal($"/policies/{x.P.Id}?refund=already", await PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, x.Admin, x.P.Id.ToString(), request.ToString()));
        Assert.Single(await RefundsAsync(x.P));
    }
}

/// <summary>
/// The monthly report around a receipt's advance taken by another policy and its reversal (legacy
/// precision-month-report.test.ts, its seventh case), through the real actions, on a database of its own. The legacy
/// receipt was recorded in October and everything after it booked in November, so October stayed as reported; here all
/// of it is booked today and the figures are read for today's month: owed nothing while the request waits (and after a
/// failed approval), then both debts back. "October as it was" (the month boundary) is not reproduced.
/// </summary>
public sealed class GapsMonthReportReversalTests
{
    [Fact]
    public async Task A_receipts_advance_taken_by_another_policy_its_reversal_asked_failed_once_approved_the_month_owes_nothing_while_it_waits_then_both_back() =>
        await OwnDatabaseAsync(async w =>
        {
            var admin = await w.UserAsync();
            var accountant = await w.UserAsync("accountant");
            var holder = await w.ClientAsync();
            var from = MonthStart;
            var a = await w.PendingPolicyAsync(holder, from, premium: 10000m, schedule: [(0, 10000m)]);
            var b = await w.PendingPolicyAsync(holder, from, premium: 0.01m, schedule: [(from.AddMonths(1).DayNumber - from.DayNumber, 0.01m)]);
            foreach (var p in new[] { a, b }) Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
            Assert.Equal($"/policies/{a.Id}?paid=1", (await w.PayAsync(accountant, a.Id, "10000.01", World.Today, reference: "R-X")).Redirect);
            var r = await w.ScalarAsync<Guid>("select id from core.payment where reference = N'R-X'");
            var month = await MonthReportAsync(w, from);
            // a settled, b's 0.01 owed (due next month, not overdue); written 10 000.01.
            Assert.Equal((System.Numerics.BigInteger.Parse("1000001"), System.Numerics.BigInteger.One, System.Numerics.BigInteger.Zero),
                (month.Totals.FinancialWrittenGel, month.Receivables.Single().Outstanding, month.Receivables.Single().Overdue));
            Assert.Equal($"/policies/{b.Id}?advance=allocated&gel=1&left=0", await PaymentActions.AllocateReceiptAsync(w.App, w.Rates, accountant, b.Id.ToString(), r.ToString()));
            Assert.Equal($"/policies/{a.Id}?reversal=requested", await PaymentActions.ReversePaymentAsync(w.App, accountant, r.ToString(), a.Id.ToString(), "შეცდომით"));
            var q = await w.ScalarAsync<Guid>("select id from core.receipt_reversal_request where receipt_id = @r", new { r });
            var waiting = (await MonthReportAsync(w, from)).Receivables.Single();
            Assert.Equal((System.Numerics.BigInteger.Zero, System.Numerics.BigInteger.Zero), (waiting.Outstanding, waiting.Overdue));
            await ArmAsync(w.Database, "payment_allocation", 2);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, a.Id.ToString(), q.ToString()));
            }
            finally
            {
                await DisarmAsync(w.Database);
            }
            Assert.Equal(waiting, (await MonthReportAsync(w, from)).Receivables.Single());
            Assert.Equal($"/policies/{a.Id}?reversal=approved", await PaymentActions.ApproveReceiptReversalRequestAsync(w.App, admin, a.Id.ToString(), q.ToString()));
            var after = await MonthReportAsync(w, from);
            // Both debts back: a's 10 000.00 (due on the month's first, overdue at its end) and b's 0.01 (due next month).
            Assert.Equal(new PremiumReport.ReceivableRow("GEL", 1000001, 1000000, 1000001, 1m, MonthEnd(from)), after.Receivables.Single());
            Assert.Equal(month.Totals, after.Totals);
            Assert.Empty((await MonthReportAsync(w, from.AddMonths(-1))).Receivables);
        });
}
