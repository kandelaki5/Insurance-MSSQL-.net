using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Cancelling a policy through CancelPolicy (docs/04 r6, r11, r14; review H03) and issuing one through IssuePolicy, on a
/// real database: the reversal rows tied to their originals, valued from the original's GEL; the schedule reduced by dated
/// adjustments; the reinsurer's share mirrored; the form's refusals; the issue's validation, suspension, rate and series.
/// </summary>
public sealed class CancelIssueTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public async Task A_usd_policy_with_two_lines_cancelled_reverses_each_line_from_its_own_gel_and_mirrors_the_cession()
    {
        var head = await w.UserAsync("operations_head");
        await w.RateAsync("USD", Start, 2.7m);
        var treaty = await w.TreatyAsync("quota_share", 30m, 20m, "GEL", "6.2");
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), Start, "USD", 2.7m, premium: 730m, schedule: [(0, 1095m)]);
        // A second line in class 6.2 (liability): 365.00 USD = 985.50 GEL.
        await w.QueryAsync<int>("insert core.policy_line (policy_id, class_code, premium, premium_gel) values (@id, N'6.2', 365, 985.50); select 1", new { id = p.Id });
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, head, p.Id.ToString())).Outcome);
        // Cancelled from day 300: 65 of 365 days returned.
        var cancelOn = Start.AddDays(300);
        Assert.Equal($"/policies/{p.Id}?cancelled=1", (await w.CancelAsync(head, p.Id, cancelOn, "მიზეზი")).Redirect);
        var rows = await w.QueryAsync<(string Kind, string ClassCode, decimal Amount, decimal AmountGel, DateOnly BookedOn, DateOnly CoverFrom, DateOnly CoverTo, bool Linked)>("""
            select t.kind, l.class_code, t.amount, t.amount_gel, t.booked_on, t.cover_from, t.cover_to,
                   convert(bit, case when t.cancels_transaction_id = (select o.id from core.premium_transaction o where o.policy_line_id = t.policy_line_id and o.kind = N'issue') then 1 else 0 end)
            from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id where l.policy_id = @id and t.kind = N'cancellation' order by l.class_code
            """, new { id = p.Id });
        // 730 × 65/365 = 130.00 USD (351.00 GEL from 1971.00); 365 × 65/365 = 65.00 USD (175.50 GEL from 985.50).
        Assert.Equal([("cancellation", "5.1", -130m, -351m, cancelOn, cancelOn, Start.AddDays(365), true), ("cancellation", "6.2", -65m, -175.50m, cancelOn, cancelOn, Start.AddDays(365), true)], rows);
        Assert.Equal(("cancelled", cancelOn, "მიზეზი"), (await w.QueryAsync<(string, DateOnly, string)>("select status, cancelled_on, cancel_reason from core.policy where id = @id", new { id = p.Id })).Single());
        // The schedule shrinks to the retained 900.00 USD by a dated adjustment; nothing is deleted.
        Assert.Equal([(cancelOn, -195m)], await w.QueryAsync<(DateOnly, decimal)>("select j.booked_on, j.change from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id", new { id = p.Id }));
        Assert.Equal(900m, await w.ScheduledAsync(p.Id));
        // The 30 % liability cession is mirrored on its cancellation: −30 % of the returned 175.50 GEL.
        var cessions = await w.QueryAsync<(decimal Amount, decimal AmountGel, decimal CommissionGel, bool Cancels)>("""
            select c.amount, c.amount_gel, c.commission_gel, convert(bit, case when c.cancels_cession_id is null then 0 else 1 end) from core.premium_cession c where c.treaty_id = @treaty order by c.amount desc
            """, new { treaty });
        Assert.Equal([(109.50m, 295.65m, 59.13m, false), (-19.50m, -52.65m, -10.53m, true)], cessions);

        // Cancelled again: only an active policy is cancelled.
        Assert.Equal(PolicyTexts.Cancel.Errors.NotActive, (await w.CancelAsync(head, p.Id, cancelOn)).Message);
        Assert.Equal(2, (await w.PremiumRowsAsync(p.Id)).Count(r => r.Kind == "cancellation"));
    }

    [Fact]
    public async Task The_cancel_forms_refusals()
    {
        var admin = await w.UserAsync();
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), Start);
        Assert.Equal(Ka.Common.ReadOnly, (await w.CancelAsync(await w.UserAsync("viewer"), p.Id, Start.AddDays(10))).Message);
        var bad = await PolicyActions.CancelPolicyAsync(w.App, admin, World.Form(("policy_id", p.Id.ToString()), ("cancel_on", "10/02/2026"), ("reason", "")));
        Assert.Equal((PolicyTexts.Cancel.Errors.Date, PolicyTexts.Cancel.Errors.Reason), (bad.Errors["cancel_on"], bad.Errors["reason"]));
        Assert.Equal(PolicyTexts.Cancel.Errors.OutsideTerm, (await w.CancelAsync(admin, p.Id, Start.AddDays(365))).Errors["cancel_on"]);
        Assert.Equal(PolicyTexts.Cancel.Errors.OutsideTerm, (await w.CancelAsync(admin, p.Id, Start.AddDays(-1))).Errors["cancel_on"]);
        Assert.Equal(PolicyTexts.Cancel.Errors.NotActive, (await PolicyActions.CancelPolicyAsync(w.App, admin, World.Form(("policy_id", "x"), ("cancel_on", "2026-02-01"), ("reason", "r")))).Message);
        var pending = await w.PendingPolicyAsync(await w.ClientAsync(), Start);
        Assert.Equal(PolicyTexts.Cancel.Errors.NotActive, (await w.CancelAsync(admin, pending.Id, Start.AddDays(10))).Message);
        Assert.DoesNotContain(await w.PremiumRowsAsync(p.Id), r => r.Kind == "cancellation");
        // The first day of the cover is allowed: everything returned, nothing retained.
        Assert.NotNull((await w.CancelAsync(admin, p.Id, Start)).Redirect);
        Assert.Equal(-365m, (await w.PremiumRowsAsync(p.Id)).Single(r => r.Kind == "cancellation").Amount);
        Assert.Equal(0m, await w.ScheduledAsync(p.Id));
    }

    private Dictionary<string, string> IssueForm(string taxId, params (string, string)[] extra)
    {
        var form = World.Form(("product_id", ""), ("policyholder_id_number", taxId), ("make", "Toyota"), ("model", "Corolla"), ("year", "2021"), ("plate", "AA-001-AA"),
            ("value", "35000"), ("effective_from", "2026-10-01"), ("effective_to", "2027-10-01"), ("currency", "GEL"), ("cov_damage_premium", "700"), ("cov_damage_limit", "35000"),
            ("installments", "4"));
        foreach (var (k, v) in extra) form[k] = v;
        return form;
    }

    [Fact]
    public async Task Issue_writes_a_pending_policy_under_a_new_contract_with_its_schedule()
    {
        var user = await w.UserAsync("sales");
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await w.ClientAsync(taxId);
        var (product, _) = await w.CascoAsync();
        var form = IssueForm(taxId, ("product_id", product.ToString()), ("cov_theft_premium", "300"), ("cancel_grace_days", "15"));
        var outcome = await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, form, code => code == "theft");
        var id = await w.ScalarAsync<Guid>("select p.id from core.policy p join core.contract c on c.id = p.contract_id where c.policyholder_id = @holder", new { holder });
        Assert.Equal($"/policies/{id}?issued=1", outcome.Redirect);
        var policy = (await w.QueryAsync<(string Status, string Number, DateOnly From, DateOnly To, int? Grace, string Segment, Guid CreatedBy)>(
            "select status, number, effective_from, effective_to, cancel_grace_days, segment, created_by from core.policy where id = @id", new { id })).Single();
        Assert.Equal(("pending_approval", new DateOnly(2026, 10, 1), new DateOnly(2027, 10, 1), (int?)15, "corporate", user.Id), (policy.Status, policy.From, policy.To, policy.Grace, policy.Segment, policy.CreatedBy));
        Assert.StartsWith("CAS", policy.Number);
        Assert.Equal([(250m, new DateOnly(2026, 10, 1)), (250m, new DateOnly(2027, 1, 1)), (250m, new DateOnly(2027, 4, 1)), (250m, new DateOnly(2027, 7, 1))],
            await w.QueryAsync<(decimal, DateOnly)>("select amount, due_on from core.installment where policy_id = @id order by due_on", new { id }));
        Assert.Equal([700m, 300m], await w.QueryAsync<decimal>("select l.premium from core.policy_line l join core.product_coverage c on c.id = l.product_coverage_id where l.policy_id = @id order by c.sort_order", new { id }));
        Assert.Equal("Toyota Corolla AA-001-AA", await w.ScalarAsync<string>("select description from core.insured_object where policy_id = @id", new { id }));
        Assert.Empty(await w.PremiumRowsAsync(id));
    }

    [Fact]
    public async Task Issue_refusals_name_their_fields_and_write_nothing()
    {
        var user = await w.UserAsync("sales");
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await w.ClientAsync(taxId);
        var (product, _) = await w.CascoAsync();
        var pid = ("product_id", product.ToString());
        Assert.Equal(Ka.Common.ReadOnly, (await PolicyActions.IssuePolicyAsync(w.App, w.Rates, await w.UserAsync("viewer"), IssueForm(taxId, pid), _ => false)).Message);
        Assert.Equal(PolicyTexts.Errors.Coverages, (await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, IssueForm(taxId), _ => false)).Message);
        var bad = await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, IssueForm("000000000", pid, ("make", ""), ("vin", "123"), ("installments", "2"), ("cov_damage_premium", "0.001")), _ => false);
        Assert.Equal(PolicyTexts.Errors.Policyholder, bad.Errors["policyholder_id_number"]);
        Assert.Equal(PolicyTexts.Errors.Make, bad.Errors["make"]);
        Assert.Equal(PolicyTexts.Errors.Vin, bad.Errors["vin"]);
        Assert.Equal(PolicyTexts.Errors.Installments, bad.Errors["installments"]);
        Assert.Equal(PolicyTexts.Errors.Premium, bad.Errors["cov_damage_premium"]);
        // 11 payments only on a term of exactly twelve months.
        Assert.Equal(PolicyTexts.Errors.InstallmentsEleven, (await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, IssueForm(taxId, pid, ("installments", "11"), ("effective_to", "2027-09-30")), _ => false)).Errors["installments"]);
        // A USD start day without a rate.
        Assert.Equal(PolicyTexts.Errors.Rate, (await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, IssueForm(taxId, pid, ("currency", "USD"), ("effective_from", "2026-10-03")), _ => false)).Errors["currency"]);
        // A suspended client.
        var s = await w.SuspendAsync(holder, user.Id);
        Assert.Equal(AmlTexts.Suspension.Blocked, (await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, IssueForm(taxId, pid), _ => false)).Errors["policyholder_id_number"]);
        await w.LiftAsync(s);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.contract where policyholder_id = @holder", new { holder }));
    }
}

/// <summary>A number series that has run out: nothing is written and the form says so (its own database: the contract series is changed).</summary>
public sealed class IssueSeriesExhaustedTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    [Fact]
    public async Task An_exhausted_contract_series_writes_nothing()
    {
        var user = await w.UserAsync("sales");
        var taxId = (300_000_000 + Random.Shared.Next(600_000_000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var holder = await w.ClientAsync(taxId);
        var (product, _) = await w.CascoAsync();
        await using (var c = await w.Database.OpenAdminAsync())
            await Dapper.SqlMapper.ExecuteAsync(c, "update core.number_sequence set next_value = 1000000, padding = 6 where code = N'contract'");
        var form = World.Form(("product_id", product.ToString()), ("policyholder_id_number", taxId), ("make", "Toyota"), ("model", "Corolla"), ("plate", "AA-001-AA"),
            ("value", "35000"), ("effective_from", "2026-10-01"), ("effective_to", "2027-10-01"), ("currency", "GEL"), ("cov_damage_premium", "700"));
        Assert.Equal(Ka.Common.SeriesExhausted, (await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, form, _ => false)).Message);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.contract where policyholder_id = @holder", new { holder }));
    }
}
