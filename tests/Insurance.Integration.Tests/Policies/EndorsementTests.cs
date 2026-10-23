using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/endorsement-regression.ts (acceptance AG-C3): a change through EndorsePolicy and ApprovePolicy.
/// docs/04 r10 — a change is a new policy under the same contract at the change date's rate; docs/04 8a — a reduction
/// lowers the original's schedule only on approval, in the company's chosen order; a reduction larger than the schedule
/// is not approved.
/// </summary>
public sealed class EndorsementTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly End = Start.AddDays(365);
    private static readonly DateOnly Change = Start.AddDays(99);

    private async Task<(CurrentUser User, World.Policy Original)> OriginalAsync(string currency = "GEL", decimal rate = 1m, DateOnly? from = null)
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        return (user, await w.ActivePolicyAsync(user, client, from ?? Start, currency, rate));
    }

    [Fact]
    public async Task E1_plus_100_is_a_new_pending_policy_and_approved_books_on_the_change_date()
    {
        var (user, o1) = await OriginalAsync();
        var e1 = await w.EndorseAsync(user, o1.Id, Change, "100");
        var c1 = (await w.ChangeOfAsync(o1.Id))!.Value;
        Assert.Equal($"/policies/{c1.Id}?issued=1", e1.Redirect);
        Assert.Equal(("pending_approval", o1.ContractId, Change, End), (c1.Status, c1.ContractId, c1.EffectiveFrom, c1.EffectiveTo));
        Assert.Equal([(100m, 100m)], await w.QueryAsync<(decimal, decimal)>("select premium, premium_gel from core.policy_line where policy_id = @id", new { id = c1.Id }));
        Assert.Equal([(Change, 100m)], await w.QueryAsync<(DateOnly, decimal)>("select due_on, amount from core.installment where policy_id = @id", new { id = c1.Id }));
        Assert.Equal(365m, await w.ScheduledAsync(o1.Id));

        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, c1.Id.ToString())).Outcome);
        var row = Assert.Single(await w.PremiumRowsAsync(c1.Id));
        Assert.Equal((100m, Change, Change, End), (row.Amount, row.BookedOn, row.CoverFrom, row.CoverTo));
        Assert.Equal("active", (await w.ChangeOfAsync(o1.Id))!.Value.Status);
        Assert.Equal(365m, await w.ScheduledAsync(o1.Id));
    }

    [Fact]
    public async Task E2_minus_100_reduces_the_original_only_when_approved()
    {
        var (user, o2) = await OriginalAsync();
        await w.EndorseAsync(user, o2.Id, Change, "-100");
        var c2 = (await w.ChangeOfAsync(o2.Id))!.Value;
        Assert.Equal(365m, await w.ScheduledAsync(o2.Id));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.installment where policy_id = @id", new { id = c2.Id }));
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, c2.Id.ToString())).Outcome);
        Assert.Equal(265m, await w.ScheduledAsync(o2.Id));
        Assert.Equal("active", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = o2.Id }));
        Assert.Equal([-100m], (await w.PremiumRowsAsync(c2.Id)).Select(r => r.Amount));
    }

    [Fact]
    public async Task E3_a_reduction_larger_than_the_schedule_is_not_approved()
    {
        var (user, o3) = await OriginalAsync();
        await w.EndorseAsync(user, o3.Id, Change, "-400");
        var c3 = (await w.ChangeOfAsync(o3.Id))!.Value;
        var answer = await PolicyActions.ApprovePolicyAsync(w.App, user, c3.Id.ToString());
        Assert.Equal("reduction-exceeds", answer.Outcome);
        Assert.Equal(PolicyTexts.Endorse.ReductionExceeds, PolicyActions.AnswerText(answer.Outcome));
        Assert.Equal("pending_approval", (await w.ChangeOfAsync(o3.Id))!.Value.Status);
        Assert.Empty(await w.PremiumRowsAsync(c3.Id));
        Assert.Equal(365m, await w.ScheduledAsync(o3.Id));
    }

    [Theory]
    [InlineData("365.00")]
    [InlineData("91.25")]
    public async Task E7_a_paid_or_partly_paid_original_changed_minus_100_keeps_what_was_paid(string amount)
    {
        var (user, o7) = await OriginalAsync();
        Assert.NotNull((await w.PayAsync(user, o7.Id, amount, Start.AddDays(4))).Redirect);
        await w.EndorseAsync(user, o7.Id, Change, "-100");
        var c7 = (await w.ChangeOfAsync(o7.Id))!.Value;
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, c7.Id.ToString())).Outcome);
        Assert.Equal(265m, await w.ScheduledAsync(o7.Id));
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), await w.PaidAsync(o7.Id));
    }

    [Fact]
    public async Task E4_a_usd_change_is_valued_at_the_change_days_rate()
    {
        var d0 = Start.AddDays(10);
        var d1 = Start.AddDays(40);
        await w.RateAsync("USD", d0, 2.7m);
        await w.RateAsync("USD", d1, 2.85m);
        var (user, o4) = await OriginalAsync("USD", 2.7m, d0);
        await w.EndorseAsync(user, o4.Id, d1, "100");
        var c4 = (await w.ChangeOfAsync(o4.Id))!.Value;
        Assert.Equal((2.85m, "USD"), (c4.PremiumRate, c4.PremiumCurrency.Trim()));
        Assert.Equal([(100m, 285m)], await w.QueryAsync<(decimal, decimal)>("select premium, premium_gel from core.policy_line where policy_id = @id", new { id = c4.Id }));
    }

    [Fact]
    public async Task E8_roles_and_identifiers_on_the_actions_themselves()
    {
        var (_, o8) = await OriginalAsync();
        var viewer = await w.UserAsync("viewer");
        var sales = await w.UserAsync("sales");
        async Task<string> Written() => string.Join("|", await w.QueryAsync<string>("""
            select concat((select count(*) from core.policy where contract_id = @c), ';',
                          (select string_agg(status, ',') within group (order by number) from core.policy where contract_id = @c), ';',
                          (select count(*) from core.policy_line l join core.policy p on p.id = l.policy_id where p.contract_id = @c), ';',
                          (select count(*) from core.installment i join core.policy p on p.id = i.policy_id where p.contract_id = @c), ';',
                          (select count(*) from core.installment_adjustment j join core.installment i on i.id = j.installment_id join core.policy p on p.id = i.policy_id where p.contract_id = @c), ';',
                          (select count(*) from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id where p.contract_id = @c), ';',
                          (select string_agg(concat(code, '=', next_value), ',') within group (order by code) from core.number_sequence where code like N'policy:%'))
            """, new { c = o8.ContractId }));

        var before = await Written();
        var viewerEndorse = await w.EndorseAsync(viewer, o8.Id, Change, "-100");
        Assert.Equal((Ka.Common.ReadOnly, null), (viewerEndorse.Message, viewerEndorse.Redirect));
        Assert.Equal(before, await Written());

        var salesEndorse = await w.EndorseAsync(sales, o8.Id, Change, "-100");
        Assert.NotNull(salesEndorse.Redirect);
        var c8 = (await w.ChangeOfAsync(o8.Id))!.Value;
        Assert.Equal("pending_approval", c8.Status);
        var pending = await Written();
        Assert.Equal("forbidden", (await PolicyActions.ApprovePolicyAsync(w.App, sales, c8.Id.ToString())).Outcome);
        Assert.Equal("forbidden", (await PolicyActions.ApprovePolicyAsync(w.App, viewer, c8.Id.ToString())).Outcome);
        Assert.Equal(pending, await Written());
        Assert.Equal(365m, await w.ScheduledAsync(o8.Id));

        const string unknown = "00000000-0000-4000-8000-000000000000";
        var admin = await w.UserAsync();
        Assert.Equal("invalid", (await PolicyActions.ApprovePolicyAsync(w.App, admin, "not-a-policy")).Outcome);
        Assert.Equal("invalid", (await PolicyActions.ApprovePolicyAsync(w.App, admin, unknown)).Outcome);
        foreach (var target in new[] { "not-a-policy", unknown, c8.Id.ToString() })
        {
            var r = await PolicyActions.EndorsePolicyAsync(w.App, w.Rates, admin, World.Form(("policy_id", target), ("change_on", Dates(Change)), ("reason", "x"),
                ("cov_damage_on", "on"), ("cov_damage_delta", "-50")), c => c == "damage");
            Assert.Equal((PolicyTexts.Endorse.Errors.NotActive, null), (r.Message, r.Redirect));
        }
        Assert.Equal(pending, await Written());

        var head = await w.UserAsync("operations_head");
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, head, c8.Id.ToString())).Outcome);
        var approved = await Written();
        var again = await PolicyActions.ApprovePolicyAsync(w.App, head, c8.Id.ToString());
        Assert.Equal(("already", c8.Number), (again.Outcome, again.Number));
        Assert.Equal(approved, await Written());
        Assert.Equal(265m, await w.ScheduledAsync(o8.Id));
    }

    private static string Dates(DateOnly d) => Insurance.Domain.Finance.Dates.ToIso(d);

    [Fact]
    public async Task E9_E10_equal_over_the_unpaid_instalments_and_an_overpaid_remainder()
    {
        await w.SettingAsync("policy.reduction_order", "equal_unpaid");
        try
        {
            var (user, o9) = await OriginalAsync();
            Assert.NotNull((await w.PayAsync(user, o9.Id, "91.25", Start.AddDays(4))).Redirect);
            await w.EndorseAsync(user, o9.Id, Change, "-100");
            Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, (await w.ChangeOfAsync(o9.Id))!.Value.Id.ToString())).Outcome);
            Assert.Equal("91.25/57.91/57.92/57.92", await w.ScheduleTextAsync(o9.Id));
            Assert.Equal(91.25m, await w.PaidAsync(o9.Id));

            var (_, o10) = await OriginalAsync();
            Assert.NotNull((await w.PayAsync(user, o10.Id, "273.75", Start.AddDays(4))).Redirect);
            await w.EndorseAsync(user, o10.Id, Change, "-100");
            Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, (await w.ChangeOfAsync(o10.Id))!.Value.Id.ToString())).Outcome);
            Assert.Equal("91.25/91.25/82.50/0.00", await w.ScheduleTextAsync(o10.Id));
            Assert.Equal(8.75m, await w.PaidAsync(o10.Id) - await w.ScheduledAsync(o10.Id));
        }
        finally
        {
            await w.SettingAsync("policy.reduction_order", null);
        }
    }

    [Fact]
    public async Task Endorse_form_refusals_keep_nothing_and_name_the_field()
    {
        var (user, o) = await OriginalAsync();
        var r = await PolicyActions.EndorsePolicyAsync(w.App, w.Rates, user, World.Form(("policy_id", o.Id.ToString()), ("change_on", Dates(End)), ("reason", "")), _ => false);
        Assert.Equal(PolicyTexts.Endorse.Errors.Date, r.Errors["change_on"]);
        Assert.Equal(PolicyTexts.Endorse.Errors.Reason, r.Errors["reason"]);
        Assert.Equal(PolicyTexts.Endorse.Errors.Coverages, r.Errors["coverages"]);
        Assert.Null(await w.ChangeOfAsync(o.Id));
        // A USD change on a day without a stored rate (and no network) is refused on the date.
        var (_, usd) = await OriginalAsync("USD", 2.5m, Start.AddDays(2));
        var noRate = await w.EndorseAsync(user, usd.Id, Start.AddDays(3), "10");
        Assert.Equal(PolicyTexts.Endorse.Errors.Rate, noRate.Errors["change_on"]);
    }
}

/// <summary>Legacy endorsement-regression E5/E6: a change in a closed month is booked on the first open day. Its own database: a month is closed.</summary>
public sealed class EndorsementClosedMonthTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    [Fact]
    public async Task E5_E6_a_change_of_a_policy_starting_in_a_closed_month_is_booked_on_the_first_open_day_once()
    {
        var user = await w.UserAsync();
        var lastClosed = new DateOnly(2025, 12, 1);
        var start = new DateOnly(2026, 1, 1);
        await w.CloseMonthAsync(lastClosed, user.Id);
        var client = await w.ClientAsync();
        var o5 = await w.ActivePolicyAsync(user, client, lastClosed);
        Assert.Equal(start, (await w.PremiumRowsAsync(o5.Id)).Single().BookedOn);
        var change5 = lastClosed.AddDays(10);
        await w.EndorseAsync(user, o5.Id, change5, "-100");
        var c5 = (await w.ChangeOfAsync(o5.Id))!.Value;
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, c5.Id.ToString())).Outcome);
        Assert.Equal((change5, lastClosed.AddDays(365)), (c5.EffectiveFrom, c5.EffectiveTo));
        var row = Assert.Single(await w.PremiumRowsAsync(c5.Id));
        Assert.Equal((-100m, start, change5), (row.Amount, row.BookedOn, row.CoverFrom));
        var adjustments = await w.QueryAsync<(DateOnly BookedOn, decimal Change)>("select j.booked_on, j.change from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id", new { id = o5.Id });
        Assert.NotEmpty(adjustments);
        Assert.All(adjustments, a => Assert.Equal(start, a.BookedOn));
        Assert.Equal(-100m, adjustments.Sum(a => a.Change));

        var again = await PolicyActions.ApprovePolicyAsync(w.App, user, c5.Id.ToString());
        Assert.Equal("already", again.Outcome);
        Assert.Single(await w.PremiumRowsAsync(c5.Id));
        Assert.Equal(adjustments.Count, await w.ScalarAsync<int>("select count(*) from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id", new { id = o5.Id }));
    }
}
