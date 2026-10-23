using System.Numerics;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/integration/fd-panel.test.ts and fd-panel-db-check / fd-month-report-db-check: the panel's lists (only
/// requests not carried out, oldest first, 50 a page, a cursor that is not a request id dropped) and the month's figures
/// from the monthly report (PremiumReport), on a real database. Its own database: the report counts every policy in it.
/// </summary>
public sealed class FdPanelTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    [Fact]
    public async Task The_month_report_figures_by_hand()
    {
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var march = new DateOnly(2025, 3, 1);
        await w.RateAsync("USD", new DateOnly(2025, 3, 10), 2.7m);
        await w.RateAsync("USD", new DateOnly(2025, 3, 31), 2.8m);
        var gel = await w.ActivePolicyAsync(admin, holder, march);
        await w.PayAsync(admin, gel.Id, "100.00", new DateOnly(2025, 3, 5));
        var usd = await w.PendingPolicyAsync(holder, new DateOnly(2025, 3, 10), "USD", 2.7m, premium: 100m, schedule: [(0, 100m)]);
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, usd.Id.ToString())).Outcome);

        var report = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, "2025-03"));
        Assert.Equal((new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 31)), (report.From, report.To));
        var row = Assert.Single(report.Classes);
        // GEL 365.00 from 01.03 (31 days earned); USD 100.00 at 2.70 = 270.00 from 10.03 (22 days: 270 × 22/365 = 16.27).
        Assert.Equal(("5.1", 2, 63500, 63500, 3100 + 1627, 33400 + 25373), (row.ClassCode, row.PoliciesIssued, (long)row.StatisticalWrittenGel, (long)row.FinancialWrittenGel, (long)row.EarnedGel, (long)row.UnearnedGel));
        Assert.Equal(row.EarnedGel, report.Totals.EarnedGel);
        // The receivable at month end: the 100.00 paid on 05.03 is allocated today, after March — March still owes it all.
        var receivables = report.Receivables.ToDictionary(r => r.Currency);
        Assert.Equal((36500, 9125, 36500), ((long)receivables["GEL"].Outstanding, (long)receivables["GEL"].Overdue, (long)receivables["GEL"].OutstandingGel));
        Assert.Equal((10000, 10000, 28000, 2.8m, new DateOnly(2025, 3, 31)), ((long)receivables["USD"].Outstanding, (long)receivables["USD"].Overdue, (long)receivables["USD"].OutstandingGel, receivables["USD"].Rate, receivables["USD"].RateDate));
        // IBNR: 5 % of the year's net written premium of the class: 635.00 × 5 % = 31.75.
        Assert.Equal(3175, (long)report.ClaimsTotals.IbnrGel);
        Assert.Equal(BigInteger.Zero, report.ClaimsTotals.PaidGel);
        // A month without anything: empty, never an error.
        var empty = await w.App.ReadTxAsync(tx => PremiumReport.BuildAsync(tx, w.Rates, "2024-01"));
        Assert.Empty(empty.Classes);
        Assert.Empty(empty.Receivables);
        Assert.Equal("1,234,567.89", PremiumReport.ReportMoney(123456789));
        Assert.Equal("-0.01", PremiumReport.ReportMoney(-1));
        Assert.Equal(Rates.TodayInTbilisi().ToString("yyyy-MM"), PremiumReport.ReportMonth("2025-13"));
        Assert.Equal("2025-03", PremiumReport.ReportMonth("2025-03"));
        Assert.Equal(Rates.TodayInTbilisi().ToString("yyyy-MM"), PremiumReport.ReportMonth(null));
    }

    private async Task<Guid> RefundRequestAsync(Guid policyId, Guid payer, Guid by, decimal amount, DateTimeOffset at)
    {
        var id = Guid.NewGuid();
        await w.App.AsUserAsync(by, tx => tx.ExecuteAsync("""
            insert core.premium_refund_request (id, policy_id, refunded_on, amount_gel, currency, rate, method, payer_party_id, created_at)
            values (@id, @policyId, @today, @amount, N'GEL', 1, N'bank', @payer, @at)
            """, new { id, policyId, today = World.Today, amount, payer, at }));
        return id;
    }

    [Fact]
    public async Task The_lists_show_only_requests_not_carried_out_oldest_first_a_page_at_a_time()
    {
        var admin = await w.UserAsync();
        var holder = await w.ClientAsync();
        var p = await w.ActivePolicyAsync(admin, holder, new DateOnly(2025, 6, 1));
        var t0 = DateTimeOffset.UtcNow.AddDays(-1);
        var ids = new List<Guid>();
        for (var i = 0; i < 51; i++) ids.Add(await RefundRequestAsync(p.Id, holder, admin.Id, 90071992547408.01m - i, t0.AddMinutes(i)));
        var first = await w.App.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, "refund", null));
        Assert.Equal(FdPanel.ReadState.Ok, first.State);
        Assert.Equal(ids.Take(50), first.Rows.Select(r => r.Id));
        Assert.Equal(ids[49], first.Next);
        Assert.Equal(("90,071,992,547,408.01", p.Id, p.Number, (Guid?)null), (FdPanel.AmountText(first.Rows[0].AmountGel), first.Rows[0].CardId, first.Rows[0].CardNumber, first.Rows[0].ReceiptId));
        Assert.Matches(@"^\d{2}\.\d{2}\.\d{4}$", first.Rows[0].RequestedOn);
        var second = await w.App.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, "refund", first.Next!.Value.ToString()));
        Assert.Equal([ids[50]], second.Rows.Select(r => r.Id));
        Assert.Null(second.Next);

        // A request carried out (a refund payment carries it) leaves the list.
        await w.App.AsUserAsync(admin.Id, tx => tx.ExecuteAsync("""
            insert core.payment (received_on, amount_gel, method, kind, payer_party_id, refund_request_id) values (@today, @amount, N'bank', N'refund', @holder, @request)
            """, new { today = World.Today, amount = -90071992547408.01m, holder, request = ids[0] }));
        var after = await w.App.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, "refund", null));
        Assert.Equal(ids.Skip(1), after.Rows.Select(r => r.Id));
        Assert.Null(after.Next);
        var claims = await w.App.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, "claim", null));
        Assert.Equal((FdPanel.ReadState.Ok, 0), (claims.State, claims.Rows.Count));

        // A reversal request shows its receipt.
        var receipt = await w.ReceiptAsync(holder, new DateOnly(2025, 6, 2), 500m);
        var reversal = Guid.NewGuid();
        await w.App.AsUserAsync(admin.Id, tx => tx.ExecuteAsync("""
            insert core.receipt_reversal_request (id, receipt_id, policy_id, reversed_on, amount_gel, method, payer_party_id, reason)
            values (@reversal, @receipt, @policy, @today, 500, N'bank', @holder, N'მიზეზი')
            """, new { reversal, receipt, policy = p.Id, today = World.Today, holder }));
        var reversals = await w.App.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, "reversal", null));
        Assert.Equal([(reversal, (Guid?)receipt, "500.00")], reversals.Rows.Select(r => (r.Id, r.ReceiptId, FdPanel.AmountText(r.AmountGel))));
    }

    [Fact]
    public async Task A_cursor_that_is_not_a_request_id_is_dropped_and_one_of_no_request_gives_an_empty_page()
    {
        var cursor = FdPanel.ParseCursor(k => k switch { "claim" => "x' or 1=1", "refund" => " 0000000A-0000-4000-8000-000000000001 ", _ => null });
        Assert.Equal((null, "0000000a-0000-4000-8000-000000000001", null), (cursor["claim"], cursor["refund"], cursor["reversal"]));
        var page = await w.App.ReadTxAsync(tx => FdPanel.ReadPendingQueueAsync(tx, "refund", cursor["refund"]));
        Assert.Equal((FdPanel.ReadState.Ok, 0, (Guid?)null), (page.State, page.Rows.Count, page.Next));
    }
}
