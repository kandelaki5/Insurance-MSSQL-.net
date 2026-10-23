using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/endorsement-regression.ts (acceptance AG-C3): a change through EndorsePolicy and ApprovePolicy, under the
/// rules of the old program's later PRs #207/#218/#225 (owner, 13–14.10.2026; docs/04 r10, docs/05 a15/a16). No month is
/// closed in this database, so the originals' months are open and an approved change corrects its original: its premium goes
/// on the original's lines from the original's start, the original's schedule is laid out again as if issued with the new
/// premium, and the change's record is named ROOT/შესწორება-n (ROOT/მოთხოვნა-n until approved). A reduction larger than the
/// schedule is not approved. A change standing as a policy of its own (its original's month closed) is in
/// EndorsementOwnPolicyTests; the closed-period refusals in EndorsementClosedMonthTests.
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
    public async Task E1_plus_100_in_the_originals_open_month_corrects_the_original_from_its_start()
    {
        var (user, o1) = await OriginalAsync();
        var e1 = await w.EndorseAsync(user, o1.Id, Change, "100");
        var c1 = (await w.ChangeOfAsync(o1.Id))!.Value;
        Assert.Equal($"/policies/{c1.Id}?issued=1", e1.Redirect);
        Assert.Equal(("pending_approval", o1.ContractId, Change, End, $"{o1.Number}/მოთხოვნა-1"), (c1.Status, c1.ContractId, c1.EffectiveFrom, c1.EffectiveTo, c1.Number));
        Assert.Equal([(100m, 100m)], await w.QueryAsync<(decimal, decimal)>("select premium, premium_gel from core.policy_line where policy_id = @id", new { id = c1.Id }));
        Assert.Equal([(Change, 100m)], await w.QueryAsync<(DateOnly, decimal)>("select due_on, amount from core.installment where policy_id = @id", new { id = c1.Id }));
        Assert.Equal(365m, await w.ScheduledAsync(o1.Id));

        var answer = await PolicyActions.ApprovePolicyAsync(w.App, user, c1.Id.ToString());
        Assert.Equal(("done", $"{o1.Number}/შესწორება-1"), (answer.Outcome, answer.Number));
        // The premium is on the original's line, booked on the original's day with its cover; the change carries none.
        Assert.Empty(await w.PremiumRowsAsync(c1.Id));
        Assert.Equal([(100m, Start, Start, End), (365m, Start, Start, End)], (await w.PremiumRowsAsync(o1.Id)).Select(r => (r.Amount, r.BookedOn, r.CoverFrom, r.CoverTo)).OrderBy(r => r.Amount));
        // The original's schedule as if issued at 465.00; the change's own 100.00 instalment goes, its amount is the original's.
        Assert.Equal("116.25/116.25/116.25/116.25", await w.ScheduleTextAsync(o1.Id));
        Assert.Equal(0m, await w.ScheduledAsync(c1.Id));
        Assert.Equal([$"{PolicyTexts.Endorse.CorrectionReason} {o1.Number}/შესწორება-1"], (await w.QueryAsync<string>("""
            select distinct j.reason from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id in (@a, @b)
            """, new { a = o1.Id, b = c1.Id })));
        Assert.Equal(("active", o1.Number), ((await w.ChangeOfAsync(o1.Id))!.Value.Status, await w.ScalarAsync<string>("select number from core.policy where id = @id", new { id = o1.Id })));
    }

    [Fact]
    public async Task E2_minus_100_corrects_the_original_only_when_approved_its_schedule_laid_out_again()
    {
        var (user, o2) = await OriginalAsync();
        await w.EndorseAsync(user, o2.Id, Change, "-100");
        var c2 = (await w.ChangeOfAsync(o2.Id))!.Value;
        Assert.Equal(365m, await w.ScheduledAsync(o2.Id));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.installment where policy_id = @id", new { id = c2.Id }));
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, c2.Id.ToString())).Outcome);
        // Each instalment keeps its share (owner, 13.10.2026: "as if issued anew"), not the company's reduction order.
        Assert.Equal("66.25/66.25/66.25/66.25", await w.ScheduleTextAsync(o2.Id));
        Assert.Equal("active", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = o2.Id }));
        Assert.Empty(await w.PremiumRowsAsync(c2.Id));
        Assert.Equal([-100m, 365m], (await w.PremiumRowsAsync(o2.Id)).Select(r => r.Amount).Order());
    }

    [Fact]
    public async Task Two_corrections_lay_the_schedule_out_by_its_first_shares_and_number_on_from_the_original()
    {
        // Legacy PR #218 L225:3259 in this world's figures: 365.00 → 300.00 → 465.00. Between them 4 × 75.00, then 4 × 116.25 —
        // by the first 4 × 91.25, not by the first correction's amounts (review PR218-F2).
        var (user, o) = await OriginalAsync();
        await w.EndorseAsync(user, o.Id, Change, "-65");
        var first = (await w.ChangeOfAsync(o.Id))!.Value;
        Assert.Equal(("done", $"{o.Number}/შესწორება-1"), await ApproveAsync(user, first.Id));
        Assert.Equal("75.00/75.00/75.00/75.00", await w.ScheduleTextAsync(o.Id));
        await w.EndorseAsync(user, o.Id, Change, "165");
        var second = await w.QueryAsync<(Guid Id, string Number)>("select id, number from core.policy where replaces_policy_id = @id and status = N'pending_approval'", new { id = o.Id });
        Assert.Equal($"{o.Number}/მოთხოვნა-1", Assert.Single(second).Number);
        Assert.Equal(("done", $"{o.Number}/შესწორება-2"), await ApproveAsync(user, second[0].Id));
        Assert.Equal("116.25/116.25/116.25/116.25", await w.ScheduleTextAsync(o.Id));
        Assert.Equal(465m, (await w.PremiumRowsAsync(o.Id)).Sum(r => r.Amount));
    }

    private async Task<(string, string)> ApproveAsync(CurrentUser user, Guid id)
    {
        var a = await PolicyActions.ApprovePolicyAsync(w.App, user, id.ToString());
        return (a.Outcome, a.Number);
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
        // The request ROOT/მოთხოვნა-1 took its number on approval; neither entry nor approval took one of the series.
        Assert.Equal(("already", $"{o8.Number}/მოთხოვნა-1", $"{o8.Number}/შესწორება-1"), (again.Outcome, c8.Number, again.Number));
        Assert.Equal(approved, await Written());
        Assert.Equal(before.Split(';')[^1], approved.Split(';')[^1]);
        Assert.Equal(265m, await w.ScheduledAsync(o8.Id));
    }

    private static string Dates(DateOnly d) => Insurance.Domain.Finance.Dates.ToIso(d);

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

/// <summary>
/// No change and no new policy in a closed period (owner, 13.10.2026; legacy PRs #207, #212, #210 boundaries): the form refuses
/// a date in a closed month, and approval refuses one whose month has closed since entry — nothing is written and the request
/// keeps its number. Before these rules (legacy endorsement-regression E5/E6) both were booked on the first open day. Its own
/// database: months are closed, one after the other, inside the one test.
/// </summary>
public sealed class EndorsementClosedMonthTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);

    private async Task<string> WrittenAsync(Guid contract) => string.Join("|", await w.QueryAsync<string>("""
        select concat((select string_agg(concat(number, ':', status), ',') within group (order by number) from core.policy where contract_id = @contract), ';',
                      (select count(*) from core.premium_transaction t join core.policy_line l on l.id = t.policy_line_id join core.policy p on p.id = l.policy_id where p.contract_id = @contract), ';',
                      (select count(*) from core.installment_adjustment j join core.installment i on i.id = j.installment_id join core.policy p on p.id = i.policy_id where p.contract_id = @contract))
        """, new { contract }));

    [Fact]
    public async Task A_date_in_a_closed_month_is_refused_at_entry_and_at_approval_and_nothing_is_written()
    {
        var user = await w.UserAsync();
        var client = await w.ClientAsync();
        // An original from 01.01.2026, approved while January is open.
        var o = await w.ActivePolicyAsync(user, client, new DateOnly(2026, 1, 1));
        // A change entered on 20.01 with January open; then January closes before the approval.
        await w.EndorseAsync(user, o.Id, new DateOnly(2026, 1, 20), "-100");
        var pending = (await w.ChangeOfAsync(o.Id))!.Value;
        await w.CloseMonthAsync(new DateOnly(2026, 1, 1), user.Id);
        var before = await WrittenAsync(o.ContractId);
        var refused = await PolicyActions.ApprovePolicyAsync(w.App, user, pending.Id.ToString());
        Assert.Equal(("period-closed", $"{o.Number}/მოთხოვნა-1"), (refused.Outcome, refused.Number));
        Assert.Equal(PolicyTexts.Endorse.PeriodClosed, PolicyActions.AnswerText(refused.Outcome));
        Assert.Equal(before, await WrittenAsync(o.ContractId));

        // Entry: the last closed day is refused on its field, with no transaction; the first open day is not.
        var lastClosed = await w.EndorseAsync(user, o.Id, new DateOnly(2026, 1, 31), "-50");
        Assert.Equal((PolicyTexts.Endorse.Errors.PeriodClosed, null), (lastClosed.Errors.GetValueOrDefault("change_on"), lastClosed.Redirect));
        Assert.Equal(before, await WrittenAsync(o.ContractId));
        var firstOpen = await w.EndorseAsync(user, o.Id, new DateOnly(2026, 2, 1), "-50");
        Assert.NotNull(firstOpen.Redirect);
        Assert.Equal(2, await w.ScalarAsync<int>("select count(*) from core.policy where replaces_policy_id = @id and status = N'pending_approval'", new { id = o.Id }));

        // A new policy starting in the closed January: the form refuses it on the start date; one entered before the close is
        // refused at approval with the owner's words and stays pending, with no premium written.
        var (product, _) = await w.CascoAsync();
        var taxId = await w.ScalarAsync<string>("select tax_id from core.party where id = @client", new { client });
        IReadOnlyDictionary<string, string> Form(string from, string to) => World.Form(("product_id", product.ToString()), ("policyholder_id_number", taxId),
            ("make", "Toyota"), ("model", "Prius"), ("plate", "PC-001-CL"), ("value", "20000"), ("effective_from", from), ("effective_to", to), ("currency", "GEL"),
            ("cov_damage_premium", "365"), ("installments", "1"));
        var issued = await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, Form("2026-01-15", "2027-01-15"), _ => false);
        Assert.Equal((PolicyTexts.Errors.PeriodClosed, null), (issued.Errors.GetValueOrDefault("effective_from"), issued.Redirect));
        Assert.NotNull((await PolicyActions.IssuePolicyAsync(w.App, w.Rates, user, Form("2026-02-01", "2027-02-01"), _ => false)).Redirect);
        var late = await w.PendingPolicyAsync(client, new DateOnly(2026, 1, 15));
        var answer = await PolicyActions.ApprovePolicyAsync(w.App, user, late.Id.ToString());
        Assert.Equal(("policy-period-closed", late.Number), (answer.Outcome, answer.Number));
        Assert.Equal("შეუძლებელია პოლისის დამტკიცება, თვე დახურულია", PolicyActions.AnswerText(answer.Outcome));
        Assert.Empty(await w.PremiumRowsAsync(late.Id));
        Assert.Equal("pending_approval", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = late.Id }));
    }
}

/// <summary>
/// A change whose original's own month is already closed stands as a policy of its own (owner, 13.10.2026; docs/05 a16;
/// legacy PR #207, L225:2419): numbered ROOT/1, then ROOT/2 — a change of ROOT/1 too — on its own date and its own lines; a
/// reduction lowers the original's schedule in the company's chosen order (legacy endorsement-regression E9/E10). Its own
/// database: January is closed after the originals are approved.
/// </summary>
public sealed class EndorsementOwnPolicyTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly Change = Start.AddDays(99);

    [Fact]
    public async Task A_change_of_a_closed_months_original_is_its_own_policy_numbered_from_the_original_in_the_companys_order()
    {
        var user = await w.UserAsync();
        await w.SettingAsync("policy.reduction_order", "equal_unpaid");
        try
        {
            var o9 = await w.ActivePolicyAsync(user, await w.ClientAsync(), Start);
            var o10 = await w.ActivePolicyAsync(user, await w.ClientAsync(), Start);
            Assert.NotNull((await w.PayAsync(user, o9.Id, "91.25", Start.AddDays(4))).Redirect);
            Assert.NotNull((await w.PayAsync(user, o10.Id, "273.75", Start.AddDays(4))).Redirect);
            await w.CloseMonthAsync(Start, user.Id);

            await w.EndorseAsync(user, o9.Id, Change, "-100");
            var c9 = (await w.ChangeOfAsync(o9.Id))!.Value;
            var a9 = await PolicyActions.ApprovePolicyAsync(w.App, user, c9.Id.ToString());
            Assert.Equal(("done", $"{o9.Number}/1"), (a9.Outcome, a9.Number));
            Assert.Equal("91.25/57.91/57.92/57.92", await w.ScheduleTextAsync(o9.Id));
            Assert.Equal(91.25m, await w.PaidAsync(o9.Id));
            // Its own premium row on its own line, on its own date, with its own cover.
            var row = Assert.Single(await w.PremiumRowsAsync(c9.Id));
            Assert.Equal((-100m, Change, Change), (row.Amount, row.BookedOn, row.CoverFrom));
            Assert.Equal([$"{PolicyTexts.Endorse.ScheduleReason} {o9.Number}/1"], await w.QueryAsync<string>("""
                select distinct j.reason from core.installment_adjustment j join core.installment i on i.id = j.installment_id where i.policy_id = @id
                """, new { id = o9.Id }));

            await w.EndorseAsync(user, o10.Id, Change, "-100");
            Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, (await w.ChangeOfAsync(o10.Id))!.Value.Id.ToString())).Outcome);
            Assert.Equal("91.25/91.25/82.50/0.00", await w.ScheduleTextAsync(o10.Id));
            Assert.Equal(8.75m, await w.PaidAsync(o10.Id) - await w.ScheduledAsync(o10.Id));

            // A change of ROOT/1 is ROOT/2, never ROOT/1/1 (owner, docs/05 a16; L225:3386) — once ROOT/1's own month (April)
            // is closed too, so that it does not correct ROOT/1 itself.
            foreach (var month in new[] { 2, 3, 4 }) await w.CloseMonthAsync(new DateOnly(2026, month, 1), user.Id);
            await w.EndorseAsync(user, c9.Id, new DateOnly(2026, 5, 20), "10");
            var c9b = (await w.QueryAsync<(Guid Id, string Number)>("select id, number from core.policy where replaces_policy_id = @id", new { id = c9.Id })).Single();
            Assert.Equal($"{o9.Number}/მოთხოვნა-1", c9b.Number);
            Assert.Equal(("done", $"{o9.Number}/2"), await ApproveAsync(user, c9b.Id));
        }
        finally
        {
            await w.SettingAsync("policy.reduction_order", null);
        }
    }

    private async Task<(string, string)> ApproveAsync(CurrentUser user, Guid id)
    {
        var a = await PolicyActions.ApprovePolicyAsync(w.App, user, id.ToString());
        return (a.Outcome, a.Number);
    }
}
