using System.Numerics;
using Insurance.Application.Services;
using static Insurance.Integration.Tests.Policies.Gaps;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// The annual 11-payment plan through the real actions on a real database (legacy eleven-writer.test.ts and
/// eleven-approve-receipt.test.ts; owner 13.10.2026, docs/05 a16): a yearly policy pays the first and the last month on
/// its start, then ten monthly. PolicyActions.IssuePolicyAsync writes it (underwriter), ApprovePolicyAsync puts it in
/// force (head of operations), RecordPaymentAsync takes the first 200.00 (the underwriter, given the receipt permission
/// as the legacy store gave it), and the real monthly report reads it. The legacy scenario ran from 01.10.2026 with
/// "today" 07.10.2026; here the policy starts on the first of today's month and the receipt is received and entered
/// today, so the day counts are the month's own: on a 31-day month (October, as in the legacy test) the figures are the
/// legacy literals, checked as such.
/// </summary>
public sealed class GapsElevenTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    private static Dictionary<string, string> Form(Guid product, string taxId, DateOnly from, string damage, string theft) => World.Form(
        ("product_id", product.ToString()), ("policyholder_id_number", taxId), ("make", "ტესტი"), ("model", "თერთმეტი"), ("plate", "TE-011-AP"), ("value", "30000"),
        ("currency", "GEL"), ("effective_from", Iso(from)), ("effective_to", Iso(from.AddYears(1))),
        ("cov_damage_premium", damage), ("cov_damage_limit", "30000"), ("cov_damage_deductible", "0"),
        ("cov_theft_premium", theft), ("cov_theft_limit", "30000"), ("cov_theft_deductible", "0"), ("installments", "11"));

    private static string TaxId() => (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The real issue action from the form's values: the new policy's id (the fixture fails if it was refused).</summary>
    private static async Task<Guid> IssueAsync(World w, CurrentUser user, Guid holder, string taxId, DateOnly from, string damage, string theft)
    {
        var (product, _) = await w.CascoAsync();
        var outcome = await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, Form(product, taxId, from, damage, theft), code => code is "damage" or "theft");
        Assert.True(outcome.Redirect is not null, $"{outcome.Message} {string.Join(", ", outcome.Errors.Select(e => $"{e.Key}={e.Value}"))}");
        var id = await w.ScalarAsync<Guid>("select p.id from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder", new { holder });
        Assert.Equal($"/policies/{id}?issued=1", outcome.Redirect);
        return id;
    }

    private static Task<List<(DateOnly DueOn, decimal Amount)>> InstalmentsAsync(World w, Guid id) =>
        w.QueryAsync<(DateOnly, decimal)>("select due_on, amount from core.installment where policy_id = @id order by due_on", new { id });

    private static IEnumerable<(DateOnly, decimal)> Monthly(DateOnly from, int count, decimal amount) => Enumerable.Range(1, count).Select(k => (from.AddMonths(k), amount));

    [Fact]
    public async Task Eleven_writer_900_plus_300_the_lines_900_and_300_eleven_instalments_200_on_the_start_then_ten_of_100()
    {
        var underwriter = await w.UserAsync("underwriter");
        var taxId = TaxId();
        var holder = await w.ClientAsync(taxId);
        var from = MonthStart;
        var id = await IssueAsync(w, underwriter, holder, taxId, from, "900", "300");
        // First financial check, by hand: the two lines as written and the eleven instalments.
        Assert.Equal([("damage", 900m, 900m), ("theft", 300m, 300m)], await w.QueryAsync<(string, decimal, decimal)>("""
            select c.code, l.premium, l.premium_gel from core.policy_line l join core.product_coverage c on c.id = l.product_coverage_id where l.policy_id = @id order by c.sort_order
            """, new { id }));
        Assert.Equal([(from, 200m), .. Monthly(from, 10, 100m)], await InstalmentsAsync(w, id));
        // Then: the policy written once, pending, at 1.000000, by the underwriter; nothing approved, received or adjusted.
        Assert.Equal([("pending_approval", "GEL", 1m, from, from.AddYears(1), underwriter.Id)], await w.QueryAsync<(string, string, decimal, DateOnly, DateOnly, Guid)>(
            "select status, premium_currency, premium_rate, effective_from, effective_to, created_by from core.policy where id = @id", new { id }));
        Assert.Empty(await w.PremiumRowsAsync(id));
        Assert.Equal(0, await w.ScalarAsync<int>("""
            select (select count(*) from core.payment where payer_party_id = @holder) + (select count(*) from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id)
            """, new { holder, id }));
    }

    [Fact]
    public async Task Eleven_writer_a_premium_that_does_not_divide_by_twelve_700_plus_300_166_66_nine_of_83_33_the_last_83_37()
    {
        var underwriter = await w.UserAsync("underwriter");
        var taxId = TaxId();
        var holder = await w.ClientAsync(taxId);
        var from = MonthStart;
        var id = await IssueAsync(w, underwriter, holder, taxId, from, "700", "300");
        var rows = await InstalmentsAsync(w, id);
        Assert.Equal([(from, 166.66m), .. Monthly(from, 9, 83.33m), (from.AddMonths(10), 83.37m)], rows);
        Assert.Equal(1000m, rows.Sum(r => r.Amount));
    }

    /// <summary>The legacy chain: issued by the underwriter, approved by the head, 200.00 received today by the underwriter (ELEVEN-0001).</summary>
    private static async Task<(Guid Policy, Guid Holder, CurrentUser Underwriter, CurrentUser Head, PolicyActions.ApproveAnswer Approved, PaymentActions.FormAnswer Paid)> ChainAsync(World w)
    {
        var underwriter = await w.UserAsync("underwriter");
        var head = await w.UserAsync("operations_head");
        await DecideAsync(w, underwriter.Id, "premium.receipt.create", "allow");
        var taxId = TaxId();
        var holder = await w.ClientAsync(taxId);
        var id = await IssueAsync(w, underwriter, holder, taxId, MonthStart, "900", "300");
        var approved = await PolicyActions.ApprovePolicyAsync(w.App, head, id.ToString());
        var paid = await w.PayAsync(underwriter, id, "200.00", World.Today, reference: "ELEVEN-0001");
        return (id, holder, underwriter, head, approved, paid);
    }

    [Fact]
    public async Task Eleven_approve_receipt_approved_then_200_received_two_premium_rows_one_receipt_one_allocation_owed_1000_nothing_overdue()
    {
        var (id, holder, underwriter, head, approved, paid) = await ChainAsync(w);
        var from = MonthStart;
        var today = World.Today;
        var number = await w.ScalarAsync<string>("select number from core.policy where id = @id", new { id });
        Assert.Equal(new PolicyActions.ApproveAnswer("done", number), approved);
        Assert.Equal($"/policies/{id}?paid=0", paid.Redirect);
        // First financial check after the receipt, by hand: the premium rows, the receipt, its one allocation, what is owed.
        Assert.Equal([(900m, 900m, from, from, from.AddYears(1), head.Id), (300m, 300m, from, from, from.AddYears(1), head.Id)],
            await w.QueryAsync<(decimal, decimal, DateOnly, DateOnly, DateOnly, Guid)>("""
                select t.amount, t.amount_gel, t.booked_on, t.cover_from, t.cover_to, t.created_by from core.premium_transaction t
                join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id and t.kind = N'issue' order by t.amount desc
                """, new { id }));
        var receipt = Assert.Single(await w.QueryAsync<(Guid Id, DateOnly ReceivedOn, decimal AmountGel, string Method, Guid Payer, string Reference, Guid CreatedBy)>(
            "select id, received_on, amount_gel, method, payer_party_id, reference, created_by from core.payment where payer_party_id = @holder", new { holder }));
        Assert.Equal((today, 200m, "bank", holder, "ELEVEN-0001", underwriter.Id), (receipt.ReceivedOn, receipt.AmountGel, receipt.Method, receipt.Payer, receipt.Reference, receipt.CreatedBy));
        Assert.Equal([(from, 200m, 200m, 1m, today)], await w.QueryAsync<(DateOnly, decimal, decimal, decimal, DateOnly)>("""
            select i.due_on, a.amount, a.amount_gel, a.rate, a.booked_on from core.payment_allocation a join core.installment i on i.id = a.installment_id where a.payment_id = @r
            """, new { r = receipt.Id }));
        Assert.Equal(1000m, await w.OwedAsync(id));
        // Nothing due by today is left unpaid (the next instalment is due on the first of next month).
        Assert.Equal(0m, await w.ScalarAsync<decimal>("""
            select coalesce(sum(i.amount), 0) - coalesce((select sum(a.amount) from core.payment_allocation a join core.installment k on k.id = a.installment_id
                                                          where k.policy_id = @id and k.due_on <= @today), 0)
            from core.installment i where i.policy_id = @id and i.due_on <= @today
            """, new { id, today }));
        // The schedule as written, untouched: the ten later instalments hold nothing; no adjustment, no cession.
        Assert.Equal([(from, 200m), .. Monthly(from, 10, 100m)], await InstalmentsAsync(w, id));
        Assert.Equal(0, await w.ScalarAsync<int>("""
            select (select count(*) from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id)
                 + (select count(*) from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id)
            """, new { id }));
        Assert.Equal([("active", head.Id, underwriter.Id, 1m)], await w.QueryAsync<(string, Guid, Guid, decimal)>(
            "select status, approved_by, created_by, premium_rate from core.policy where id = @id", new { id }));
        // The real readers change nothing: the eleven instalments as written, no advance.
        Assert.Equal([200m, .. Enumerable.Repeat(100m, 10)], (await w.App.ReadTxAsync(tx => Billing.LoadScheduleAsync(tx, id))).Select(i => i.Amount));
        Assert.Empty(await w.App.ReadTxAsync(tx => Billing.LoadAdvancesAsync(tx, holder)));
    }

    [Fact]
    public async Task Eleven_full_month_report_one_policy_written_1200_earned_101_92_UPR_1098_08_owed_1000_nothing_overdue_IBNR_60_the_month_before_empty() =>
        await OwnDatabaseAsync(async w =>
        {
            var (_, _, _, _, approved, paid) = await ChainAsync(w);
            Assert.Equal("done", approved.Outcome);
            Assert.NotNull(paid.Redirect);
            var from = MonthStart;
            var to = MonthEnd(from);
            var days = to.DayNumber - from.DayNumber + 1;
            // By hand (tetri), per premium row: round(90000 × days / 365) + round(30000 × days / 365); on 31 days 7644 + 2548.
            BigInteger earned = ReportMath.DivRound(new BigInteger(90000) * days, 365) + ReportMath.DivRound(new BigInteger(30000) * days, 365);
            if (days == 31) Assert.Equal(new BigInteger(10192), earned);
            var before = await LedgerAsync(w);
            var month = await MonthReportAsync(w, from);
            var c = Assert.Single(month.Classes);
            Assert.Equal(("5.1", 1, new BigInteger(120000), new BigInteger(120000), earned, 120000 - earned), (c.ClassCode, c.PoliciesIssued, c.StatisticalWrittenGel, c.FinancialWrittenGel, c.EarnedGel, c.UnearnedGel));
            if (days == 31) Assert.Equal(new BigInteger(109808), c.UnearnedGel);
            Assert.Equal((BigInteger.Zero, BigInteger.Zero, BigInteger.Zero), (c.CededWrittenGel, c.CededUnearnedGel, c.RiCommissionGel));
            Assert.Equal(new PremiumReport.Totals(1, 120000, 120000, earned, 120000 - earned, 0, 0, 0), month.Totals);
            Assert.Equal([new PremiumReport.ReceivableRow("GEL", 100000, 0, 100000, 1m, to)], month.Receivables);
            var claims = Assert.Single(month.Claims);
            Assert.Equal(("5.1", BigInteger.Zero, BigInteger.Zero, new BigInteger(6000)), (claims.ClassCode, claims.PaidGel, claims.RbnsGel, claims.IbnrGel));
            Assert.Equal(new PremiumReport.ClaimsTotals(0, 0, 6000, 0, 0, 0), month.ClaimsTotals);
            Assert.Equal(BigInteger.Zero, month.TreatyPremiumGel);
            // The month before the policy's start: nothing recognised.
            var previous = await MonthReportAsync(w, from.AddMonths(-1));
            Assert.Empty(previous.Classes);
            Assert.Empty(previous.Receivables);
            Assert.Empty(previous.Claims);
            Assert.Equal(new PremiumReport.Totals(0, 0, 0, 0, 0, 0, 0, 0), previous.Totals);
            // The report only reads.
            Assert.Equal(before, await LedgerAsync(w));
        });
}
