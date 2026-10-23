using System.Globalization;
using Dapper;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// A premium refund and a premium receipt's reversal above the financial director's threshold, through the real
/// RefundOverpaymentAsync / ReversePaymentAsync and their approvals (legacy fd-net-transfer-chain.test.ts, the cases
/// FdRequestTests leaves out): the GEL actually paid back or the receipt's stored GEL decides, never a second conversion;
/// the plan in exact tetri over many instalments; who approves; stale plans; a failure at a line; the limit as a
/// setting. The payment actions read the real Tbilisi day, so the legacy refund day 03.11.2026 (and 04.11) is today here,
/// with that day's rate stored as the legacy world had it; the receipt of 20.10 is received today too.
/// </summary>
public sealed class GapsFdRefundReversalTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static DateOnly Today => World.Today;
    private const string Fd = "payment.large.authorize";

    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    private sealed record Overpaid(CurrentUser Admin, CurrentUser Accountant, World.Policy P, IReadOnlyList<Guid> Inst, Guid Receipt);

    /// <summary>
    /// A policy in <paramref name="currency"/> with an instalment of 1 000.00 per excess (due on the first of the month, the
    /// second, …), each paid 1 000.00 + its excess by one bank receipt at 2.7 (GEL: 1), as stored; today's rate as given.
    /// </summary>
    private async Task<Overpaid> OverpaidAsync(string currency, string[] excesses, string threshold = "10000.00", decimal? todayRate = null)
    {
        await w.SettingAsync("payment.fd_threshold_gel", threshold);
        if (todayRate is { } r) await w.RateAsync(currency, Today, r);
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var count = Math.Max(excesses.Length, 1);
        var atReceipt = currency == "GEL" ? 1m : 2.7m;
        var p = await w.PendingPolicyAsync(holder, MonthStart, currency, atReceipt, 1000m * count, [.. Enumerable.Range(0, count).Select(i => (i, 1000m))]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        var inst = await w.QueryAsync<Guid>("select id from core.installment where policy_id = @id order by due_on", new { id = p.Id });
        var settled = excesses.Select(x => 1000m + D(x)).ToList();
        var gel = settled.Select(a => Math.Round(a * atReceipt, 2, MidpointRounding.AwayFromZero)).ToList();
        // Nothing overpaid: the instalment stays unpaid, no receipt at all.
        var receipt = gel.Count == 0 ? Guid.Empty : await w.ReceiptAsync(holder, Today, gel.Sum(), reference: "ჩარიცხვა");
        for (var i = 0; i < settled.Count; i++) await AllocateAsync(receipt, inst[i], settled[i], gel[i], atReceipt);
        return new Overpaid(admin, accountant, p, inst, receipt);
    }

    private async Task AllocateAsync(Guid payment, Guid installment, decimal amount, decimal amountGel, decimal rate)
    {
        await using var c = await w.Database.OpenAppAsync();
        await c.ExecuteAsync("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@payment, @installment, @amount, @amountGel, @rate, @today)",
            new { payment, installment, amount, amountGel, rate, today = Today });
    }

    private Task<string?> RefundAsync(CurrentUser user, World.Policy p) => PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, user, p.Id.ToString(), "bank", "დაბრუნება");

    private Task<string?> ApproveRefundAsync(CurrentUser user, Guid policy, Guid request) =>
        PaymentActions.ApprovePremiumRefundRequestAsync(w.App, w.Rates, user, policy.ToString(), request.ToString());

    private Task<Guid> RefundRequestAsync(World.Policy p) => w.ScalarAsync<Guid>("select top (1) id from core.premium_refund_request where policy_id = @id order by created_at desc", new { id = p.Id });

    private Task<List<(DateOnly On, decimal Gel, Guid By, Guid? Request)>> RefundsAsync(World.Policy p) => w.QueryAsync<(DateOnly, decimal, Guid, Guid?)>(
        "select received_on, amount_gel, created_by, refund_request_id from core.payment where kind = N'refund' and payer_party_id = @h", new { h = p.Holder });

    /// <summary>The refund's lines in the order they are written, newest instalment first: instalment, amount, GEL, rate.</summary>
    private Task<List<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate)>> RefundLinesAsync(World.Policy p) => w.QueryAsync<(Guid, decimal, decimal, decimal)>("""
        select a.installment_id, a.amount, a.amount_gel, a.rate from core.payment_allocation a join core.payment r on r.id = a.payment_id
        join core.installment i on i.id = a.installment_id
        where r.kind = N'refund' and r.payer_party_id = @h order by i.due_on desc
        """, new { h = p.Holder });

    private Task<List<(int Position, Guid Installment, decimal Amount, decimal AmountGel)>> PlanAsync(Guid request) => w.QueryAsync<(int, Guid, decimal, decimal)>(
        "select position, installment_id, amount, amount_gel from core.premium_refund_request_line where request_id = @request order by position", new { request });

    private string Refunded(World.Policy p, string outcome) => $"/policies/{p.Id}?refund={outcome}";

    // ---------------------------------------------------------------- refund: what is paid back decides

    [Theory]
    [InlineData("9999.99", false)]
    [InlineData("10000.00", false)]
    [InlineData("10000.01", true)]
    public async Task GEL_paid_on_1000_due_the_excess_paid_back_today_is_above_10000_or_not(string excess, bool needed)
    {
        var o = await OverpaidAsync("GEL", [excess]);
        var asked = await RefundAsync(o.Accountant, o.P);
        Guid? request = null;
        if (needed)
        {
            Assert.Equal(Refunded(o.P, "requested"), asked);
            Assert.Empty(await RefundsAsync(o.P));
            request = await RefundRequestAsync(o.P);
            Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(o.Admin, o.P.Id, request.Value));
        }
        else Assert.Null(asked);
        var by = needed ? o.Admin.Id : o.Accountant.Id;
        Assert.Equal([(Today, -D(excess), by, request)], await RefundsAsync(o.P));
        Assert.Equal([(o.Inst[0], -D(excess), -D(excess), 1m)], await RefundLinesAsync(o.P));
        Assert.Equal([(o.Inst[0], 1000m + D(excess), 1000m + D(excess))], await w.QueryAsync<(Guid, decimal, decimal)>(
            "select installment_id, amount, amount_gel from core.payment_allocation where payment_id = @r", new { r = o.Receipt }));
    }

    [Theory]
    [InlineData("2.7", "9999.99", false)]
    [InlineData("2.8", "10370.36", true)]
    public async Task USD_3703_70_paid_back_at_the_refund_days_rate_above_10000_or_not_the_receipt_as_it_was(string rate, string gel, bool needed)
    {
        var o = await OverpaidAsync("USD", ["3703.70"], todayRate: D(rate));
        Assert.Equal(12699.99m, await w.ScalarAsync<decimal>("select amount_gel from core.payment where id = @r", new { r = o.Receipt }));
        var asked = await RefundAsync(o.Accountant, o.P);
        if (needed)
        {
            Assert.Equal(Refunded(o.P, "requested"), asked);
            Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(o.Admin, o.P.Id, await RefundRequestAsync(o.P)));
        }
        else Assert.Null(asked);
        Assert.Equal([-D(gel)], (await RefundsAsync(o.P)).Select(r => r.Gel));
        Assert.Equal([(o.Inst[0], -3703.70m, -D(gel), D(rate))], await RefundLinesAsync(o.P));
        Assert.Equal([(4703.70m, 12699.99m, 2.7m)], await w.QueryAsync<(decimal, decimal, decimal)>("select amount, amount_gel, rate from core.payment_allocation where payment_id = @r", new { r = o.Receipt }));
    }

    [Fact]
    public async Task A_refund_of_USD_2_50_at_2_002_is_minus_5_01_and_over_two_instalments_minus_10_01_newest_line_5_01_the_last_5_00()
    {
        var one = await OverpaidAsync("USD", ["2.50"], todayRate: 2.002m);
        Assert.Null(await RefundAsync(one.Accountant, one.P));
        Assert.Equal([-5.01m], (await RefundsAsync(one.P)).Select(r => r.Gel));
        Assert.Equal([(one.Inst[0], -2.50m, -5.01m, 2.002m)], await RefundLinesAsync(one.P));
        var two = await OverpaidAsync("USD", ["2.50", "2.50"], todayRate: 2.002m);
        Assert.Null(await RefundAsync(two.Accountant, two.P));
        Assert.Equal([-10.01m], (await RefundsAsync(two.P)).Select(r => r.Gel));
        Assert.Equal([(two.Inst[1], -2.50m, -5.01m, 2.002m), (two.Inst[0], -2.50m, -5.00m, 2.002m)], await RefundLinesAsync(two.P));
    }

    // ---------------------------------------------------------------- refund over many instalments: running sums within the refund

    private static IEnumerable<(decimal, decimal)> Steps(int n, decimal first, decimal second) => Enumerable.Range(0, n).Select(i => (-0.01m, i % 2 == 0 ? first : second));

    private async Task<(decimal Refund, List<(decimal Amount, decimal Gel)> Lines)> RefundLinesOfAsync(string currency, decimal rate, string[] excesses)
    {
        var o = await OverpaidAsync(currency, excesses, todayRate: currency == "GEL" ? null : rate);
        Assert.Null(await RefundAsync(o.Accountant, o.P));
        return ((await RefundsAsync(o.P)).Single().Gel, (await RefundLinesAsync(o.P)).Select(l => (l.Amount, l.AmountGel)).ToList());
    }

    private static List<decimal> Running(IEnumerable<(decimal, decimal Gel)> lines)
    {
        var n = 0m;
        return lines.Select(l => n += l.Gel).ToList();
    }

    [Fact]
    public async Task Ten_instalments_USD_0_01_over_each_at_2_5_minus_0_25_lines_0_03_and_0_02_in_turn_running_sums_none_beyond()
    {
        var (refund, lines) = await RefundLinesOfAsync("USD", 2.5m, [.. Enumerable.Repeat("0.01", 10)]);
        Assert.Equal(-0.25m, refund);
        Assert.Equal(Steps(10, -0.03m, -0.02m), lines);
        Assert.Equal([-0.03m, -0.05m, -0.08m, -0.10m, -0.13m, -0.15m, -0.18m, -0.20m, -0.23m, -0.25m], Running(lines));
    }

    [Fact]
    public async Task Twelve_instalments_USD_0_01_over_each_at_2_5_minus_0_30_the_eleventh_running_sum_minus_0_28()
    {
        var (refund, lines) = await RefundLinesOfAsync("USD", 2.5m, [.. Enumerable.Repeat("0.01", 12)]);
        Assert.Equal(-0.30m, refund);
        Assert.Equal(Steps(12, -0.03m, -0.02m), lines);
        Assert.Equal([-0.25m, -0.28m, -0.30m], Running(lines).Skip(9));
    }

    [Fact]
    public async Task One_two_and_three_lines_GEL_0_07_EUR_0_01_twice_at_3_1234_USD_three_times_0_01_at_2_5()
    {
        var g = await RefundLinesOfAsync("GEL", 1m, ["0.07"]);
        Assert.Equal(-0.07m, g.Refund);
        Assert.Equal([(-0.07m, -0.07m)], g.Lines);
        var eur = await RefundLinesOfAsync("EUR", 3.1234m, ["0.01", "0.01"]);
        Assert.Equal(-0.06m, eur.Refund);
        Assert.Equal([(-0.01m, -0.03m), (-0.01m, -0.03m)], eur.Lines);
        var usd = await RefundLinesOfAsync("USD", 2.5m, ["0.01", "0.01", "0.01"]);
        Assert.Equal(-0.08m, usd.Refund);
        Assert.Equal([(-0.01m, -0.03m), (-0.01m, -0.02m), (-0.01m, -0.03m)], usd.Lines);
    }

    [Fact]
    public async Task Whole_amounts_newest_first_USD_1_2_3_at_2_5_and_GEL_0_01_0_02_0_03()
    {
        var usd = await RefundLinesOfAsync("USD", 2.5m, ["1.00", "2.00", "3.00"]);
        Assert.Equal(-15.00m, usd.Refund);
        Assert.Equal([(-3.00m, -7.50m), (-2.00m, -5.00m), (-1.00m, -2.50m)], usd.Lines);
        var gel = await RefundLinesOfAsync("GEL", 1m, ["0.01", "0.02", "0.03"]);
        Assert.Equal(-0.06m, gel.Refund);
        Assert.Equal([(-0.03m, -0.03m), (-0.02m, -0.02m), (-0.01m, -0.01m)], gel.Lines);
    }

    // ---------------------------------------------------------------- refund requests

    [Fact]
    public async Task GEL_10000_01_paid_back_a_request_with_its_plan_approved_by_the_admin_written_once_linked()
    {
        var o = await OverpaidAsync("GEL", ["10000.01"]);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        Assert.Equal([(Today, 10000.01m, "GEL", 1m, "bank", "დაბრუნება", o.P.Holder, o.Accountant.Id)], await w.QueryAsync<(DateOnly, decimal, string, decimal, string, string, Guid, Guid)>(
            "select refunded_on, amount_gel, rtrim(currency), rate, method, reference, payer_party_id, created_by from core.premium_refund_request where id = @request", new { request }));
        Assert.Equal([(1, o.Inst[0], 10000.01m, 10000.01m)], await PlanAsync(request));
        Assert.Empty(await RefundsAsync(o.P));
        Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(o.Admin, o.P.Id, request));
        Assert.Equal([(Today, -10000.01m, o.Admin.Id, (Guid?)request)], await RefundsAsync(o.P));
        Assert.Equal([(o.Inst[0], -10000.01m, -10000.01m, 1m)], await RefundLinesAsync(o.P));
    }

    [Fact]
    public async Task Nothing_overpaid_no_refund_no_request()
    {
        var o = await OverpaidAsync("GEL", []);
        Assert.Null(await RefundAsync(o.Accountant, o.P));
        Assert.Empty(await RefundsAsync(o.P));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.premium_refund_request where policy_id = @id", new { id = o.P.Id }));
    }

    [Fact]
    public async Task Ten_instalments_USD_0_01_at_2_5_with_the_threshold_at_0_20_a_request_for_0_25_approved_exactly_as_planned()
    {
        var o = await OverpaidAsync("USD", [.. Enumerable.Repeat("0.01", 10)], "0.20", 2.5m);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        var steps = Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? 0.03m : 0.02m).ToList();
        Assert.Equal(steps.Select((g, i) => (i + 1, o.Inst[9 - i], 0.01m, g)), await PlanAsync(request));
        Assert.Equal([(0.25m, "USD", 2.5m)], await w.QueryAsync<(decimal, string, decimal)>("select amount_gel, rtrim(currency), rate from core.premium_refund_request where id = @request", new { request }));
        Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(o.Admin, o.P.Id, request));
        Assert.Equal(steps.Select((g, i) => (o.Inst[9 - i], -0.01m, -g, 2.5m)), await RefundLinesAsync(o.P));
        Assert.Equal([-0.25m], (await RefundsAsync(o.P)).Select(r => r.Gel));
    }

    [Fact]
    public async Task Half_a_tetri_USD_2_50_at_2_002_is_5_01_above_a_threshold_of_5_00_a_request_approved_minus_5_01()
    {
        var o = await OverpaidAsync("USD", ["2.50"], "5.00", 2.002m);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        Assert.Equal([(1, o.Inst[0], 2.50m, 5.01m)], await PlanAsync(request));
        Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(o.Admin, o.P.Id, request));
        Assert.Equal([(o.Inst[0], -2.50m, -5.01m, 2.002m)], await RefundLinesAsync(o.P));
    }

    [Fact]
    public async Task EUR_3201_65_over_at_3_1234_is_10000_03_a_request_approved_minus_10000_03()
    {
        var o = await OverpaidAsync("EUR", ["3201.65"], "10000.00", 3.1234m);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        Assert.Equal([(10000.03m, "EUR", 3.1234m)], await w.QueryAsync<(decimal, string, decimal)>("select amount_gel, rtrim(currency), rate from core.premium_refund_request where id = @request", new { request }));
        Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(o.Admin, o.P.Id, request));
        Assert.Equal([-10000.03m], (await RefundsAsync(o.P)).Select(r => r.Gel));
    }

    [Fact]
    public async Task Refund_who_approves_the_accountant_and_a_viewer_refused_the_admins_own_removal_a_group_gives_it_to_an_underwriter()
    {
        var o = await OverpaidAsync("GEL", ["10000.01"]);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        Assert.Equal(Refunded(o.P, "not-allowed"), await ApproveRefundAsync(o.Accountant, o.P.Id, request));
        Assert.Equal(Refunded(o.P, "not-allowed"), await ApproveRefundAsync(await w.UserAsync("viewer"), o.P.Id, request));
        await DecideAsync(w, o.Admin.Id, Fd, "deny");
        Assert.Equal(Refunded(o.P, "not-allowed"), await ApproveRefundAsync(o.Admin, o.P.Id, request));
        Assert.Empty(await RefundsAsync(o.P));
        var underwriter = await w.UserAsync("underwriter");
        await GroupAsync(w, Fd, underwriter.Id);
        Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(underwriter, o.P.Id, request));
        Assert.Equal([underwriter.Id], (await RefundsAsync(o.P)).Select(r => r.By));
        Assert.Equal(o.Accountant.Id, await w.ScalarAsync<Guid>("select created_by from core.premium_refund_request where id = @request", new { request }));
    }

    [Fact]
    public async Task Refund_ones_own_request_the_accountant_without_the_authorisation_refused_with_it_approves_its_own()
    {
        var o = await OverpaidAsync("GEL", ["10000.01"]);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        Assert.Equal(Refunded(o.P, "not-allowed"), await ApproveRefundAsync(o.Accountant, o.P.Id, request));
        await DecideAsync(w, o.Accountant.Id, Fd, "allow");
        Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(o.Accountant, o.P.Id, request));
        Assert.Equal([o.Accountant.Id], (await RefundsAsync(o.P)).Select(r => r.By));
    }

    [Fact]
    public async Task Refund_another_policy_or_a_made_up_request_state_nothing_written()
    {
        var o = await OverpaidAsync("GEL", ["10000.01"]);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        var other = await w.ActivePolicyAsync(o.Admin, await w.ClientAsync(), MonthStart);
        Assert.Equal($"/policies/{other.Id}?refund=state", await ApproveRefundAsync(o.Admin, other.Id, request));
        Assert.Equal(Refunded(o.P, "state"), await ApproveRefundAsync(o.Admin, o.P.Id, Guid.NewGuid()));
        Assert.Empty(await RefundsAsync(o.P));
    }

    [Fact]
    public async Task Refund_stale_the_refund_days_rate_corrected_an_endorsement_that_takes_the_excess_the_same_GEL_split_differently()
    {
        // USD 3 703.71 over at 2.7 = 10 000.017 → 10 000.02: a request; the day's rate corrected to 2.8 — stale.
        var usd = await OverpaidAsync("USD", ["3703.71"], "10000.00", 2.7m);
        Assert.Equal(Refunded(usd.P, "requested"), await RefundAsync(usd.Accountant, usd.P));
        var r1 = await RefundRequestAsync(usd.P);
        Assert.Equal(10000.02m, await w.ScalarAsync<decimal>("select amount_gel from core.premium_refund_request where id = @r1", new { r1 }));
        await w.RateAsync("USD", Today, 2.8m);
        var before = await LedgerAsync(w);
        Assert.Equal(Refunded(usd.P, "stale"), await ApproveRefundAsync(usd.Admin, usd.P.Id, r1));
        Assert.Equal(before, await LedgerAsync(w));

        // The schedule grows by 5 000.00 (a dated adjustment): the excess is 5 000.01 now — stale.
        var gel = await OverpaidAsync("GEL", ["10000.01"]);
        Assert.Equal(Refunded(gel.P, "requested"), await RefundAsync(gel.Accountant, gel.P));
        var r2 = await RefundRequestAsync(gel.P);
        await w.App.AsUserAsync(gel.Admin.Id, tx => tx.ExecuteAsync("insert core.installment_adjustment (installment_id, booked_on, change, reason) values (@i, @today, 5000, N'ტესტი')",
            new { i = gel.Inst[0], today = Today }));
        Assert.Equal(Refunded(gel.P, "stale"), await ApproveRefundAsync(gel.Admin, gel.P.Id, r2));
        Assert.Empty(await RefundsAsync(gel.P));

        // Two instalments over by USD 2.50 each at 2.002 (lines 5.01 / 5.00, 10.01, threshold 10.00); then 1.00 of the excess
        // moves from one to the other: still 5.00 USD, still 10.01 GEL, but the lines differ — stale.
        var split = await OverpaidAsync("USD", ["2.50", "2.50"], "10.00", 2.002m);
        Assert.Equal(Refunded(split.P, "requested"), await RefundAsync(split.Accountant, split.P));
        var r3 = await RefundRequestAsync(split.P);
        Assert.Equal([(1, split.Inst[1], 2.50m, 5.01m), (2, split.Inst[0], 2.50m, 5.00m)], await PlanAsync(r3));
        await AllocateAsync(split.Receipt, split.Inst[0], 1.00m, 0m, 2.7m);
        await AllocateAsync(split.Receipt, split.Inst[1], -1.00m, 0m, 2.7m);
        before = await LedgerAsync(w);
        Assert.Equal(Refunded(split.P, "stale"), await ApproveRefundAsync(split.Admin, split.P.Id, r3));
        Assert.Equal(before, await LedgerAsync(w));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Refund_a_failure_at_line_k_of_three_nothing_of_the_refund_stays_the_retry_writes_it_once(int after)
    {
        var o = await OverpaidAsync("USD", ["2.50", "2.50", "2.50"], "10.00", 2.002m);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        await ArmAsync(w.Database, "payment_allocation", after + 1);
        try
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => ApproveRefundAsync(o.Admin, o.P.Id, request));
            Assert.Contains("injected failure", e.Message);
        }
        finally
        {
            await DisarmAsync(w.Database);
        }
        Assert.Empty(await RefundsAsync(o.P));
        Assert.Equal(Refunded(o.P, "approved"), await ApproveRefundAsync(o.Admin, o.P.Id, request));
        // 2.50 / 5.00 / 7.50 USD → 5.01 / 10.01 / 15.02 GEL: lines 5.01, 5.00, 5.01.
        Assert.Equal([-5.01m, -5.00m, -5.01m], (await RefundLinesAsync(o.P)).Select(l => l.AmountGel));
        Assert.Single(await RefundsAsync(o.P));
    }

    [Fact]
    public async Task Refund_the_limit_is_the_setting_at_5000_GEL_5000_00_is_paid_back_at_once_and_5000_01_is_a_request()
    {
        var at = await OverpaidAsync("GEL", ["5000.00"], "5000.00");
        Assert.Null(await RefundAsync(at.Accountant, at.P));
        Assert.Equal([(Today, -5000.00m, at.Accountant.Id, (Guid?)null)], await RefundsAsync(at.P));
        var above = await OverpaidAsync("GEL", ["5000.01"], "5000.00");
        Assert.Equal(Refunded(above.P, "requested"), await RefundAsync(above.Accountant, above.P));
        Assert.Empty(await RefundsAsync(above.P));
        Assert.Equal(5000.01m, await w.ScalarAsync<decimal>("select amount_gel from core.premium_refund_request where policy_id = @id", new { id = above.P.Id }));
    }

    [Fact]
    public async Task Refund_a_right_withdrawn_after_the_request_the_groups_grant_taken_away_then_the_admins_own_removal_refused()
    {
        var o = await OverpaidAsync("GEL", ["10000.01"]);
        var underwriter = await w.UserAsync("underwriter");
        var group = await GroupAsync(w, Fd, underwriter.Id);
        Assert.Equal(Refunded(o.P, "requested"), await RefundAsync(o.Accountant, o.P));
        var request = await RefundRequestAsync(o.P);
        await w.App.AsUserAsync(o.Admin.Id, tx => tx.ExecuteAsync("delete from core.user_group_permission where group_id = @group", new { group }));
        Assert.Equal(Refunded(o.P, "not-allowed"), await ApproveRefundAsync(underwriter, o.P.Id, request));
        await DecideAsync(w, o.Admin.Id, Fd, "deny");
        Assert.Equal(Refunded(o.P, "not-allowed"), await ApproveRefundAsync(o.Admin, o.P.Id, request));
        Assert.Empty(await RefundsAsync(o.P));
    }

    // ---------------------------------------------------------------- a receipt's reversal

    private sealed record Received(CurrentUser Admin, CurrentUser Accountant, World.Policy A, World.Policy B, Guid InstA, Guid InstB, Guid Receipt);

    /// <summary>A receipt received today, its GEL <paramref name="gel"/>, allocated as given (to A's or B's instalment of 100 000.00); the threshold given.</summary>
    private async Task<Received> ReceivedAsync(string gel, (char Policy, string Amount, string Gel, decimal Rate)[] lines, string currency = "GEL", string threshold = "10000.00")
    {
        await w.SettingAsync("payment.fd_threshold_gel", threshold);
        var admin = await w.UserAsync();
        var accountant = await w.UserAsync("accountant");
        var holder = await w.ClientAsync();
        var rate = currency == "GEL" ? 1m : 2.7m;
        var a = await w.PendingPolicyAsync(holder, MonthStart, currency, rate, 100000m, [(0, 100000m)]);
        var b = await w.PendingPolicyAsync(holder, MonthStart, currency, rate, 100000m, [(0, 100000m)]);
        foreach (var p in new[] { a, b }) Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        var instA = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = a.Id });
        var instB = await w.ScalarAsync<Guid>("select id from core.installment where policy_id = @id", new { id = b.Id });
        var receipt = await w.ReceiptAsync(holder, Today, D(gel), reference: "ჩარიცხვა");
        foreach (var l in lines) await AllocateAsync(receipt, l.Policy == 'A' ? instA : instB, D(l.Amount), D(l.Gel), l.Rate);
        return new Received(admin, accountant, a, b, instA, instB, receipt);
    }

    private Task<string?> ReverseAsync(CurrentUser user, Received r) => PaymentActions.ReversePaymentAsync(w.App, user, r.Receipt.ToString(), r.A.Id.ToString(), "შეცდომა");

    private Task<string?> ApproveReversalAsync(CurrentUser user, Guid policy, Guid request) =>
        PaymentActions.ApproveReceiptReversalRequestAsync(w.App, user, policy.ToString(), request.ToString());

    private Task<Guid> ReversalRequestAsync(Received r, int n = 0) => w.ScalarAsync<Guid>(
        "select id from core.receipt_reversal_request where receipt_id = @receipt order by created_at offset @n rows fetch next 1 rows only", new { receipt = r.Receipt, n });

    private string Reversed(Received r, string outcome) => $"/policies/{r.A.Id}?reversal={outcome}";

    private Task<List<(DateOnly On, decimal Gel, string Reference, Guid By, Guid? Request)>> MirrorsAsync(Received r) => w.QueryAsync<(DateOnly, decimal, string, Guid, Guid?)>(
        "select received_on, amount_gel, reference, created_by, reversal_request_id from core.payment where reverses_payment_id = @receipt", new { receipt = r.Receipt });

    private Task<List<(Guid Installment, decimal Amount, decimal AmountGel, decimal Rate)>> MirrorLinesAsync(Received r) => w.QueryAsync<(Guid, decimal, decimal, decimal)>("""
        select a.installment_id, a.amount, a.amount_gel, a.rate from core.payment_allocation a join core.payment m on m.id = a.payment_id
        where m.reverses_payment_id = @receipt order by a.amount_gel
        """, new { receipt = r.Receipt });

    [Fact]
    public async Task Reversal_GEL_10000_01_stored_a_request_with_the_receipt_and_its_line_approved_by_the_admin_the_exact_mirror_linked_once()
    {
        var r = await ReceivedAsync("10000.01", [('A', "10000.01", "10000.01", 1m)]);
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        var request = await ReversalRequestAsync(r);
        Assert.Equal([(r.Receipt, r.A.Id, Today, 10000.01m, "bank", r.A.Holder, "შეცდომა", r.Accountant.Id)], await w.QueryAsync<(Guid, Guid, DateOnly, decimal, string, Guid, string, Guid)>(
            "select receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason, created_by from core.receipt_reversal_request where id = @request", new { request }));
        Assert.Equal([(1, r.InstA, 10000.01m, 10000.01m, 1m)], await w.QueryAsync<(int, Guid, decimal, decimal, decimal)>(
            "select position, installment_id, amount, amount_gel, rate from core.receipt_reversal_request_line where request_id = @request", new { request }));
        Assert.Empty(await MirrorsAsync(r));
        Assert.Equal(Reversed(r, "approved"), await ApproveReversalAsync(r.Admin, r.A.Id, request));
        Assert.Equal([(Today, -10000.01m, "შეცდომა", r.Admin.Id, (Guid?)request)], await MirrorsAsync(r));
        Assert.Equal([(r.InstA, -10000.01m, -10000.01m, 1m)], await MirrorLinesAsync(r));
        Assert.Equal([(10000.01m, 10000.01m)], await w.QueryAsync<(decimal, decimal)>("select amount, amount_gel from core.payment_allocation where payment_id = @id", new { id = r.Receipt }));
    }

    [Fact]
    public async Task Reversal_the_boundary_9999_99_and_10000_00_at_once_at_5000_00_5000_00_at_once_5000_01_a_request_a_threshold_not_an_amount_nothing_written()
    {
        foreach (var gel in new[] { "9999.99", "10000.00" })
        {
            var r = await ReceivedAsync(gel, [('A', gel, gel, 1m)]);
            Assert.Null(await ReverseAsync(r.Accountant, r));
            Assert.Equal([(-D(gel), (Guid?)null)], (await MirrorsAsync(r)).Select(m => (m.Gel, m.Request)));
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.receipt_reversal_request where receipt_id = @id", new { id = r.Receipt }));
        }
        var at = await ReceivedAsync("5000.00", [('A', "5000.00", "5000.00", 1m)], threshold: "5000.00");
        Assert.Null(await ReverseAsync(at.Accountant, at));
        Assert.Single(await MirrorsAsync(at));
        var above = await ReceivedAsync("5000.01", [('A', "5000.01", "5000.01", 1m)], threshold: "5000.00");
        Assert.Equal(Reversed(above, "requested"), await ReverseAsync(above.Accountant, above));
        Assert.Empty(await MirrorsAsync(above));
        var words = await ReceivedAsync("100.00", [('A', "100.00", "100.00", 1m)], threshold: "ათი ათასი");
        var before = await LedgerAsync(w);
        var e = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ReverseAsync(words.Accountant, words));
        Assert.Contains("payment.fd_threshold_gel", e.Message);
        Assert.Equal(before, await LedgerAsync(w));
        await w.SettingAsync("payment.fd_threshold_gel", "10000.00");
    }

    [Fact]
    public async Task Reversal_USD_the_stored_GEL_counts_never_todays_rate_3703_71_as_10000_01_at_2_7_a_request_approved_at_the_stored_2_7()
    {
        // The reversal and its approval take no rates at all (no Rates argument): the receipt's stored GEL and rate are mirrored.
        var r = await ReceivedAsync("10000.01", [('A', "3703.71", "10000.01", 2.7m)], "USD");
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        Assert.Equal(Reversed(r, "approved"), await ApproveReversalAsync(r.Admin, r.A.Id, await ReversalRequestAsync(r)));
        Assert.Equal([(r.InstA, -3703.71m, -10000.01m, 2.7m)], await MirrorLinesAsync(r));
    }

    [Fact]
    public async Task Reversal_USD_3703_70_received_as_10000_00_at_2_7_reversed_when_USD_is_2_8_minus_10000_00_at_the_stored_rate_not_above()
    {
        await w.RateAsync("USD", Today, 2.8m);
        var r = await ReceivedAsync("10000.00", [('A', "3703.70", "10000.00", 2.7m)], "USD");
        Assert.Null(await ReverseAsync(r.Accountant, r));
        Assert.Equal([(Today, -10000.00m)], (await MirrorsAsync(r)).Select(m => (m.On, m.Gel)));
        Assert.Equal([(r.InstA, -3703.70m, -10000.00m, 2.7m)], await MirrorLinesAsync(r));
    }

    [Fact]
    public async Task Reversal_one_receipt_allocated_to_two_policies_the_request_plans_both_lines_the_mirror_reverses_both()
    {
        var r = await ReceivedAsync("12000.00", [('A', "7000.00", "7000.00", 1m), ('B', "5000.00", "5000.00", 1m)]);
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        var request = await ReversalRequestAsync(r);
        Assert.Equal([(1, r.InstA, 7000m, 7000m, 1m), (2, r.InstB, 5000m, 5000m, 1m)], await w.QueryAsync<(int, Guid, decimal, decimal, decimal)>(
            "select position, installment_id, amount, amount_gel, rate from core.receipt_reversal_request_line where request_id = @request order by position", new { request }));
        Assert.Equal(Reversed(r, "approved"), await ApproveReversalAsync(r.Admin, r.A.Id, request));
        Assert.Equal([(r.InstA, -7000m, -7000m, 1m), (r.InstB, -5000m, -5000m, 1m)], await MirrorLinesAsync(r));
    }

    [Fact]
    public async Task Reversal_who_approves_the_accountant_and_a_viewer_refused_the_admins_own_removal_a_group_a_right_withdrawn_after_the_request()
    {
        var r = await ReceivedAsync("10000.01", [('A', "10000.01", "10000.01", 1m)]);
        var underwriter = await w.UserAsync("underwriter");
        var group = await GroupAsync(w, Fd, underwriter.Id);
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        var request = await ReversalRequestAsync(r);
        Assert.Equal(Reversed(r, "not-allowed"), await ApproveReversalAsync(r.Accountant, r.A.Id, request));
        Assert.Equal(Reversed(r, "not-allowed"), await ApproveReversalAsync(await w.UserAsync("viewer"), r.A.Id, request));
        await DecideAsync(w, r.Admin.Id, Fd, "deny");
        Assert.Equal(Reversed(r, "not-allowed"), await ApproveReversalAsync(r.Admin, r.A.Id, request));
        await w.App.AsUserAsync(r.Admin.Id, tx => tx.ExecuteAsync("delete from core.user_group_permission where group_id = @group", new { group }));
        Assert.Equal(Reversed(r, "not-allowed"), await ApproveReversalAsync(underwriter, r.A.Id, request));
        Assert.Empty(await MirrorsAsync(r));
        await w.App.AsUserAsync(r.Admin.Id, tx => tx.ExecuteAsync("insert core.user_group_permission (group_id, permission) values (@group, @Fd)", new { group, Fd }));
        Assert.Equal(Reversed(r, "approved"), await ApproveReversalAsync(underwriter, r.A.Id, request));
        Assert.Equal([underwriter.Id], (await MirrorsAsync(r)).Select(m => m.By));
        Assert.Equal(r.Accountant.Id, await w.ScalarAsync<Guid>("select created_by from core.receipt_reversal_request where id = @request", new { request }));
    }

    [Fact]
    public async Task Reversal_ones_own_request_the_accountant_without_the_authorisation_refused_with_it_approves_its_own()
    {
        var r = await ReceivedAsync("10000.01", [('A', "10000.01", "10000.01", 1m)]);
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        var request = await ReversalRequestAsync(r);
        Assert.Equal(Reversed(r, "not-allowed"), await ApproveReversalAsync(r.Accountant, r.A.Id, request));
        await DecideAsync(w, r.Accountant.Id, Fd, "allow");
        Assert.Equal(Reversed(r, "approved"), await ApproveReversalAsync(r.Accountant, r.A.Id, request));
        Assert.Equal([r.Accountant.Id], (await MirrorsAsync(r)).Select(m => m.By));
    }

    [Fact]
    public async Task Reversal_another_policy_or_a_made_up_request_state_a_second_request_for_a_receipt_already_reversed_state()
    {
        var r = await ReceivedAsync("10000.01", [('A', "10000.01", "10000.01", 1m)]);
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        var first = await ReversalRequestAsync(r);
        var other = await w.ActivePolicyAsync(r.Admin, await w.ClientAsync(), MonthStart);
        Assert.Equal($"/policies/{other.Id}?reversal=state", await ApproveReversalAsync(r.Admin, other.Id, first));
        Assert.Equal(Reversed(r, "state"), await ApproveReversalAsync(r.Admin, r.A.Id, Guid.NewGuid()));
        Assert.Empty(await MirrorsAsync(r));
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        var second = await ReversalRequestAsync(r, 1);
        Assert.Equal(Reversed(r, "approved"), await ApproveReversalAsync(r.Admin, r.A.Id, first));
        Assert.Equal(Reversed(r, "state"), await ApproveReversalAsync(r.Admin, r.A.Id, second));
        Assert.Single(await MirrorsAsync(r));
    }

    [Fact]
    public async Task Reversal_stale_a_new_allocation_from_the_receipt_after_the_request_the_same_GEL_split_differently_nothing_written()
    {
        var r = await ReceivedAsync("12000.00", [('A', "11000.00", "11000.00", 1m)]);
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        await AllocateAsync(r.Receipt, r.InstB, 1000m, 1000m, 1m);
        var before = await LedgerAsync(w);
        Assert.Equal(Reversed(r, "stale"), await ApproveReversalAsync(r.Admin, r.A.Id, await ReversalRequestAsync(r)));
        Assert.Equal(before, await LedgerAsync(w));

        var s = await ReceivedAsync("12000.00", [('A', "7000.00", "7000.00", 1m), ('B', "5000.00", "5000.00", 1m)]);
        Assert.Equal(Reversed(s, "requested"), await ReverseAsync(s.Accountant, s));
        await AllocateAsync(s.Receipt, s.InstA, -1000m, -1000m, 1m);
        await AllocateAsync(s.Receipt, s.InstB, 1000m, 1000m, 1m);
        before = await LedgerAsync(w);
        Assert.Equal(Reversed(s, "stale"), await ApproveReversalAsync(s.Admin, s.A.Id, await ReversalRequestAsync(s)));
        Assert.Equal(before, await LedgerAsync(w));
        Assert.Empty(await MirrorsAsync(s));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Reversal_a_failure_at_mirror_line_k_of_three_nothing_of_the_reversal_stays_the_retry_writes_it_once(int after)
    {
        var r = await ReceivedAsync("12000.00", [('A', "6000.00", "6000.00", 1m), ('B', "4000.00", "4000.00", 1m), ('A', "2000.00", "2000.00", 1m)]);
        Assert.Equal(Reversed(r, "requested"), await ReverseAsync(r.Accountant, r));
        var request = await ReversalRequestAsync(r);
        await ArmAsync(w.Database, "payment_allocation", after + 1);
        try
        {
            var e = await Assert.ThrowsAnyAsync<Exception>(() => ApproveReversalAsync(r.Admin, r.A.Id, request));
            Assert.Contains("injected failure", e.Message);
        }
        finally
        {
            await DisarmAsync(w.Database);
        }
        Assert.Empty(await MirrorsAsync(r));
        Assert.Equal(Reversed(r, "approved"), await ApproveReversalAsync(r.Admin, r.A.Id, request));
        Assert.Equal([-6000m, -4000m, -2000m], (await MirrorLinesAsync(r)).Select(l => l.AmountGel));
        Assert.Single(await MirrorsAsync(r));
    }
}
