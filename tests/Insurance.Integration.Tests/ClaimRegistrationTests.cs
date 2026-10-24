using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests;

/// <summary>
/// Registering a loss event and its claims on the real SQL Server (legacy scripts/loss-event-check.ts A–D and I,
/// scripts/integration/claim-cession-writer.test.ts, claim-xl-writer.test.ts, claim-cession-invalid-plan.test.ts,
/// facultative-claim-chain.test.ts; docs/04 rule 18 for a cancelled policy).
/// </summary>
public sealed class ClaimRegistrationTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2100;

    private Task<string> CounterAsync() => w.ScalarAsync<string>("select convert(nvarchar(20), next_value) from core.number_sequence where code = N'claim'");

    private async Task<(CurrentUser User, ClaimWorld.Policy Policy, int Year)> PolicyAsync(decimal damageDeductible = 200m, decimal equipmentDeductible = 200m, string currency = "GEL", decimal premium = 0m)
    {
        var y = Interlocked.Increment(ref year);
        var user = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(user.Id, new DateOnly(y, 1, 1), [
            new("damage", 20000m, damageDeductible, premium), new("equipment", 2000m, equipmentDeductible), new("theft", 20000m, damageDeductible), new("assistance", 500m)], currency: currency);
        return (user, policy, y);
    }

    private static PostedForm Registration(ClaimWorld.Policy p, DateOnly occurred, DateOnly reported, string? key, params (string Coverage, string Reserve)[] parts)
    {
        var f = new List<(string, string)> { ("policy_number", p.Number), ("occurred_on", ClaimWorld.D(occurred)), ("reported_on", ClaimWorld.D(reported)), ("description", "ავარია") };
        if (key is not null) f.Add(("request_key", key));
        foreach (var (coverage, reserve) in parts)
        {
            f.Add(($"line:{p.LineOf(coverage)}", "on"));
            f.Add(($"reserve:{p.LineOf(coverage)}", reserve));
        }
        return ClaimWorld.Form([.. f]);
    }

    [Fact]
    public async Task One_registration_over_three_coverages_is_one_event_with_claims_N_N2_N3_and_two_classes()
    {
        var (u, p, y) = await PolicyAsync();
        var form = ClaimWorld.Form(("policy_number", p.Number.ToLowerInvariant()), ("occurred_on", $"{y}-03-03"), ("reported_on", $"{y}-03-05"), ("occurred_place", "თბილისი"),
            ("description", " ავარია "), ($"line:{p.LineOf("damage")}", "on"), ($"reserve:{p.LineOf("damage")}", "3000"), ($"claimed:{p.LineOf("damage")}", "3500.5"),
            ($"line:{p.LineOf("equipment")}", "on"), ($"reserve:{p.LineOf("equipment")}", "500"), ($"line:{p.LineOf("assistance")}", "on"), ($"reserve:{p.LineOf("assistance")}", "150"));
        var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, form);
        Assert.EndsWith("?registered=1", outcome.RedirectTo);
        var first = Guid.Parse(outcome.RedirectTo!.Split('/')[2].Split('?')[0]);
        var rows = await w.RowsAsync<(string Number, string Event, string Class, decimal? Claimed, string Currency, string? Place, string Description)>("""
            select c.number, v.number, l.class_code, c.claimed_amount, c.currency, c.occurred_place, c.description
            from core.claim c join core.loss_event v on v.id = c.loss_event_id join core.policy_line l on l.id = c.policy_line_id
            where c.policy_id = @policy order by c.created_at, c.number
            """, new { policy = p.Id });
        var ev = rows[0].Event;
        Assert.Equal([ev, $"{ev}-2", $"{ev}-3"], rows.Select(r => r.Number));
        Assert.Equal(["5.1", "5.1", "14"], rows.Select(r => r.Class));
        Assert.Equal([3500.50m, null, null], rows.Select(r => r.Claimed));
        Assert.All(rows, r => Assert.Equal(("GEL", "თბილისი", "ავარია"), (r.Currency.Trim(), r.Place, r.Description)));
        Assert.Equal(first, (await w.ClaimsOfEventAsync(first))[0]);
        Assert.Equal([(new DateOnly(y, 3, 5), 3000m, ClaimTexts.Fields.InitialReserve), (new DateOnly(y, 3, 5), 500m, ClaimTexts.Fields.InitialReserve), (new DateOnly(y, 3, 5), 150m, ClaimTexts.Fields.InitialReserve)],
            await w.RowsAsync<(DateOnly, decimal, string)>("select r.booked_on, r.change, r.reason from core.claim_reserve r join core.claim c on c.id = r.claim_id where c.policy_id = @policy order by c.created_at, c.number",
                new { policy = p.Id }));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.loss_event where policy_id = @policy", new { policy = p.Id }));
    }

    [Fact]
    public async Task One_coverage_over_its_limit_writes_nothing_and_takes_no_number()
    {
        var (u, p, y) = await PolicyAsync();
        var counter = await CounterAsync();
        var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 3, 3), new DateOnly(y, 3, 4), null, ("damage", "3000"), ("equipment", "5000")));
        Assert.Null(outcome.RedirectTo);
        Assert.Equal(ClaimTexts.Errors.Reserve, outcome.Errors[$"reserve:{p.LineOf("equipment")}"]);
        Assert.False(outcome.Errors.ContainsKey($"reserve:{p.LineOf("damage")}"));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim where policy_id = @policy", new { policy = p.Id }));
        Assert.Equal(counter, await CounterAsync());
    }

    [Fact]
    public async Task The_same_form_twice_in_a_row_or_at_the_same_moment_registers_one_event()
    {
        var (u, p, y) = await PolicyAsync();
        var key = Guid.NewGuid().ToString();
        var form = Registration(p, new DateOnly(y, 3, 3), new DateOnly(y, 3, 4), key, ("damage", "1000"));
        var first = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, form);
        var second = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, form);
        Assert.Equal(first.RedirectTo, second.RedirectTo);
        var key2 = Guid.NewGuid().ToString();
        var both = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 3, 3), new DateOnly(y, 3, 4), key2, ("assistance", "100")))));
        Assert.NotNull(both[0].RedirectTo);
        Assert.Equal(both[0].RedirectTo, both[1].RedirectTo);
        Assert.Equal(2, await w.ScalarAsync<int>("select count(*) from core.loss_event where request_key in @keys", new { keys = new[] { Guid.Parse(key), Guid.Parse(key2) } }));
    }

    [Fact]
    public async Task A_claim_on_another_policys_event_and_a_coverage_twice_in_one_event_are_refused()
    {
        var (u, p, y) = await PolicyAsync();
        var (_, other, _) = await PolicyAsync();
        var claims = await w.RegisterAsync(u, p, new DateOnly(y, 3, 3), new DateOnly(y, 3, 4), ("damage", "1000"));
        var ev = await w.ScalarAsync<Guid>("select loss_event_id from core.claim where id = @id", new { id = claims[0] });
        async Task<string?> Insert(Guid policy, Guid line)
        {
            try
            {
                await w.ExecAsync("""
                    insert core.claim (number, loss_event_id, policy_id, policy_line_id, currency, occurred_on, reported_on, description)
                    values (@number, @ev, @policy, @line, N'GEL', @on, @on, N'x')
                    """, new { number = "X" + ClaimWorld.Tag(), ev, policy, line, on = new DateOnly(y, 3, 4) });
                return "inserted";
            }
            catch (Exception e) { return DbError.From(e)?.Code; }
        }
        Assert.Equal("LE001", await Insert(other.Id, other.LineOf("damage")));
        Assert.Equal(DbError.UniqueViolation, await Insert(p.Id, p.LineOf("damage")));
        var twice = await ClaimActions.AddEventCoverageAsync(w.App, w.Rates, u, ClaimWorld.Form(("claim_id", claims[0].ToString()), ("policy_line_id", p.LineOf("damage").ToString()),
            ("reported_on", $"{y}-03-05"), ("initial_reserve", "10")));
        Assert.Equal(ClaimTexts.Errors.CoverageTwice, twice.Errors["policy_line_id"]);
        // The same line twice in one form.
        var dup = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, ClaimWorld.Form(("policy_number", p.Number), ("occurred_on", $"{y}-03-03"), ("reported_on", $"{y}-03-04"),
            ("description", "x"), ($"line:{p.LineOf("theft")}", "on"), ($"line:{p.LineOf("theft")}", "on")));
        Assert.Equal(ClaimTexts.Errors.Coverage, dup.Errors["policy_line_id"]);
    }

    [Fact]
    public async Task The_original_single_coverage_form_still_registers_one_claim_numbered_as_its_event()
    {
        var (u, p, y) = await PolicyAsync();
        var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, ClaimWorld.Form(("policy_number", p.Number), ("policy_line_id", p.LineOf("damage").ToString()),
            ("occurred_on", $"{y}-03-03"), ("reported_on", $"{y}-03-04"), ("description", "x"), ("initial_reserve", "1000"), ("claimed_amount", "1200")));
        var claim = Guid.Parse(outcome.RedirectTo!.Split('/')[2].Split('?')[0]);
        Assert.Equal([(true, 1200m)], await w.RowsAsync<(bool, decimal?)>(
            "select convert(bit, case when c.number = v.number then 1 else 0 end), c.claimed_amount from core.claim c join core.loss_event v on v.id = c.loss_event_id where c.id = @claim", new { claim }));
        var missing = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, ClaimWorld.Form(("policy_number", p.Number), ("occurred_on", $"{y}-03-03"), ("reported_on", $"{y}-03-04"), ("description", "x")));
        Assert.Equal(ClaimTexts.Errors.Coverage, missing.Errors["policy_line_id"]);
    }

    [Fact]
    public async Task Field_checks_of_a_registration()
    {
        var (u, p, y) = await PolicyAsync();
        var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, ClaimWorld.Form(("policy_number", "NONE"), ("occurred_on", "x"), ("reported_on", ""), ("description", " ")));
        Assert.Equal(ClaimTexts.Errors.Policy, outcome.Errors["policy_number"]);
        Assert.Equal(ClaimTexts.Errors.Coverage, outcome.Errors["policy_line_id"]);
        Assert.Equal(ClaimTexts.Errors.OccurredOn, outcome.Errors["occurred_on"]);
        Assert.Equal(ClaimTexts.Errors.ReportedOn, outcome.Errors["reported_on"]);
        Assert.Equal(ClaimTexts.Errors.Description, outcome.Errors["description"]);
        // Outside the term (the end day is not covered), reported before it occurred, no reserve.
        var outside = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y + 1, 1, 1), new DateOnly(y + 1, 1, 2), null, ("damage", "100")));
        Assert.Equal(ClaimTexts.Errors.OccurredOn, outside.Errors["occurred_on"]);
        var early = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 3, 3), new DateOnly(y, 3, 2), null, ("damage", "0")));
        Assert.Equal(ClaimTexts.Errors.ReportedOn, early.Errors["reported_on"]);
        Assert.Equal(ClaimTexts.Errors.Reserve, early.Errors[$"reserve:{p.LineOf("damage")}"]);
        // A draft policy takes no claim.
        await w.ExecAsync("update core.policy set status = N'draft', approved_at = null, approved_by = null where id = @id", new { id = p.Id });
        Assert.Equal(ClaimTexts.Errors.Policy, (await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 3, 3), new DateOnly(y, 3, 4), null, ("damage", "100")))).Errors["policy_number"]);
    }

    [Fact]
    public async Task A_cancelled_policy_takes_a_loss_before_its_cancel_date_late_reported_but_not_one_on_or_after_it()
    {
        var (u, p, y) = await PolicyAsync(premium: 365m);
        var cancelOn = new DateOnly(y, 6, 1);
        await w.App.AsUserAsync(u.Id, tx => PolicyCancel.CancelPolicyInTxAsync(tx, p.Id, cancelOn, "ტესტი", u.Id));
        Assert.Equal("cancelled", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = p.Id }));
        var onTheDay = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, cancelOn, new DateOnly(y, 6, 2), null, ("damage", "100")));
        Assert.Equal(ClaimTexts.Errors.OccurredOn, onTheDay.Errors["occurred_on"]);
        var after = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 7, 1), new DateOnly(y, 7, 2), null, ("damage", "100")));
        Assert.Equal(ClaimTexts.Errors.OccurredOn, after.Errors["occurred_on"]);
        Assert.NotNull((await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, cancelOn.AddDays(-1), new DateOnly(y, 8, 1), null, ("damage", "100")))).RedirectTo);
    }

    [Fact]
    public async Task A_report_day_in_a_closed_month_is_refused_and_no_number_is_taken()
    {
        var (u, p, y) = await PolicyAsync();
        await w.CloseMonthAsync(u.Id, new DateOnly(y, 2, 1));
        var counter = await CounterAsync();
        var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 2, 3), new DateOnly(y, 2, 4), null, ("damage", "100")));
        Assert.Equal(ClaimTexts.Errors.PeriodClosed, outcome.Errors["reported_on"]);
        Assert.Equal(counter, await CounterAsync());
        Assert.NotNull((await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 2, 3), new DateOnly(y, 3, 4), null, ("damage", "100")))).RedirectTo);
    }

    [Fact]
    public async Task A_number_series_that_ran_out_refuses_with_the_message_and_writes_nothing()
    {
        var (u, p, y) = await PolicyAsync();
        var (next, padding) = (await w.RowsAsync<(long, int)>("select next_value, padding from core.number_sequence where code = N'claim'")).Single();
        await w.ExecAsync("update core.number_sequence set padding = 1, next_value = 10 where code = N'claim'");
        try
        {
            var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 3, 3), new DateOnly(y, 3, 4), null, ("damage", "100")));
            Assert.Equal(Ka.Common.SeriesExhausted, outcome.Message);
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim where policy_id = @id", new { id = p.Id }));
        }
        finally
        {
            await w.ExecAsync("update core.number_sequence set padding = @padding, next_value = @next where code = N'claim'", new { padding, next });
        }
    }

    [Fact]
    public async Task Adding_a_coverage_checks_the_day_the_reserve_and_the_line()
    {
        var (u, p, y) = await PolicyAsync();
        var claim = (await w.RegisterAsync(u, p, new DateOnly(y, 3, 3), new DateOnly(y, 3, 4), ("damage", "1000")))[0];
        PostedForm Add(string line, string on, string reserve) => ClaimWorld.Form(("claim_id", claim.ToString()), ("policy_line_id", line), ("reported_on", on), ("initial_reserve", reserve));
        Assert.Equal(ClaimTexts.Errors.Coverage, (await ClaimActions.AddEventCoverageAsync(w.App, w.Rates, u, Add("x", $"{y}-03-05", "10"))).Errors["policy_line_id"]);
        Assert.Equal(ClaimTexts.Errors.ReportedOn, (await ClaimActions.AddEventCoverageAsync(w.App, w.Rates, u, Add(p.LineOf("equipment").ToString(), "x", "10"))).Errors["reported_on"]);
        Assert.Equal(ClaimTexts.Errors.ReportedOn, (await ClaimActions.AddEventCoverageAsync(w.App, w.Rates, u, Add(p.LineOf("equipment").ToString(), $"{y}-03-02", "10"))).Errors["reported_on"]);
        Assert.Equal(ClaimTexts.Errors.Reserve, (await ClaimActions.AddEventCoverageAsync(w.App, w.Rates, u, Add(p.LineOf("equipment").ToString(), $"{y}-03-05", "2000.01"))).Errors["initial_reserve"]);
        Assert.Equal(ClaimTexts.Errors.Coverage, (await ClaimActions.AddEventCoverageAsync(w.App, w.Rates, u, Add(Guid.NewGuid().ToString(), $"{y}-03-05", "10"))).Errors["policy_line_id"]);
        var added = await ClaimActions.AddEventCoverageAsync(w.App, w.Rates, u, Add(p.LineOf("assistance").ToString(), $"{y}-03-05", "100"));
        Assert.EndsWith("?registered=1", added.RedirectTo);
        Assert.Equal(2, (await w.ClaimsOfEventAsync(claim)).Count);
    }

    // ---- reinsurance shares fixed on registration

    private async Task<Guid> TreatyAsync(int y, string kind, decimal? percent, string currency = "GEL", decimal? priority = null, decimal? limit = null,
        DateOnly? from = null, DateOnly? to = null, string status = "active", string classCode = "5.1")
    {
        var id = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent, priority, treaty_limit, status)
            values (@id, @number, @name, @reinsurer, @kind, @from, @to, @currency, @percent, @priority, @limit, @status);
            insert core.ri_treaty_class (treaty_id, class_code) values (@id, @classCode);
            """, new
        {
            id, number = $"{kind[..2].ToUpperInvariant()}-{ClaimWorld.Tag()}", name = $"{kind} {percent}", reinsurer = await w.PartyAsync(), kind, from = from ?? new DateOnly(y, 1, 1),
            to = to ?? new DateOnly(y, 12, 31), currency, percent, priority, limit, status, classCode,
        });
        return id;
    }

    private Task CloseTreatyAsync(Guid id) => w.ExecAsync("update core.ri_treaty set status = N'closed' where id = @id", new { id });

    private async Task<List<(Guid Treaty, decimal Share, decimal? Priority, decimal? Limit)>> CessionsAsync(Guid claim) =>
        await w.RowsAsync<(Guid, decimal, decimal?, decimal?)>("select treaty_id, share_percent, priority, treaty_limit from core.claim_cession where claim_id = @claim", new { claim });

    private Task<List<(Guid Treaty, long InReserve, long InPaid)>> SharesAsync(Guid claim, DateOnly? asOf = null) =>
        w.App.ReadTxAsync(async tx => (await ReinsuranceStore.ClaimSharesAsync(tx, claim, asOf)).Select(s => (s.TreatyId, s.InReserve, s.InPaid)).ToList());

    [Fact]
    public async Task A_proportional_share_is_taken_from_the_lines_booked_cession_not_from_the_treaties_in_force_now()
    {
        var y = Interlocked.Increment(ref year);
        var u = await w.UserAsync("claims");
        var old = await TreatyAsync(y, "quota_share", 30m);
        var p1 = await w.PolicyAsync(u.Id, new DateOnly(y, 1, 1), [new("damage", 20000m, 0m, 365m)]);
        await CloseTreatyAsync(old);
        var twenty = await TreatyAsync(y, "quota_share", 20m);
        await w.PolicyAsync(u.Id, new DateOnly(y, 2, 1), [new("damage", 20000m, 0m, 365m)]);
        await CloseTreatyAsync(twenty);
        await TreatyAsync(y, "quota_share", 50m);
        var claim = (await w.RegisterAsync(u, p1, new DateOnly(y, 7, 15), new DateOnly(y, 7, 16), ("damage", "1000")))[0];
        Assert.Equal([(old, 30m, (decimal?)null, (decimal?)null)], await CessionsAsync(claim));
        Assert.Equal([(old, 30000L, 0L)], await SharesAsync(claim, new DateOnly(y, 7, 31)));
    }

    [Fact]
    public async Task One_GEL_excess_of_loss_in_force_on_the_loss_day_is_written_once_and_shares_above_its_priority()
    {
        var y = Interlocked.Increment(ref year);
        var u = await w.UserAsync("claims");
        var xl = await TreatyAsync(y, "excess_of_loss", null, priority: 100m, limit: 500m, from: new DateOnly(y, 7, 1), to: new DateOnly(y, 12, 31));
        await TreatyAsync(y, "excess_of_loss", null, priority: 10m, limit: 50m, from: new DateOnly(y, 1, 1), to: new DateOnly(y, 6, 30));
        await TreatyAsync(y, "excess_of_loss", null, priority: 10m, limit: 50m, status: "closed");
        var p = await w.PolicyAsync(u.Id, new DateOnly(y, 1, 1), [new("damage", 20000m, 0m, 365m)]);
        var claim = (await w.RegisterAsync(u, p, new DateOnly(y, 7, 15), new DateOnly(y, 7, 16), ("damage", "1000")))[0];
        Assert.Equal([(xl, 0m, (decimal?)100m, (decimal?)500m)], await CessionsAsync(claim));
        Assert.Equal([(xl, 50000L, 0L)], await SharesAsync(claim, new DateOnly(y, 7, 31)));
        await w.ApproveAsync(u, claim, "1000", new DateOnly(y, 7, 16));
        var a = await w.UserAsync("accountant");
        Assert.NotNull((await w.PayAsync(a, claim, "200", new DateOnly(y, 7, 20), new DateOnly(y, 7, 20))).RedirectTo);
        Assert.Equal([(xl, 40000L, 10000L)], await SharesAsync(claim, new DateOnly(y, 7, 31)));
    }

    [Fact]
    public async Task A_treaty_combination_the_rules_do_not_allow_refuses_the_registration_with_nothing_written()
    {
        async Task Refused(ClaimWorld.Policy p, int y, string problem)
        {
            var u = await w.UserAsync("claims");
            var counter = await CounterAsync();
            var outcome = await ClaimActions.RegisterClaimAsync(w.App, w.Rates, u, Registration(p, new DateOnly(y, 7, 15), new DateOnly(y, 7, 16), null, ("damage", "1000")));
            Assert.Equal(ReinsuranceTexts.Problems[problem], outcome.Message);
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim where policy_id = @id", new { id = p.Id }));
            Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.loss_event where policy_id = @id", new { id = p.Id }));
            Assert.Equal(counter, await CounterAsync());
        }
        var actor = await w.UserAsync("admin");
        // An old claim: the USD policy's claim in USD (H46 setting claim.currency = policy). The GEL claim on a USD coverage
        // meeting a GEL treaty is ClaimGelOnForeignPolicyTests' case.
        await w.OldClaimsAsync();

        var y1 = Interlocked.Increment(ref year);
        await w.RateAsync("USD", new DateOnly(y1, 1, 1), 2.7m);
        await TreatyAsync(y1, "excess_of_loss", null, priority: 100m, limit: 500m);
        await Refused(await w.PolicyAsync(actor.Id, new DateOnly(y1, 1, 1), [new("damage", 20000m, 0m)], currency: "USD", rate: 2.7m), y1, "currency-mismatch");

        var y2 = Interlocked.Increment(ref year);
        await TreatyAsync(y2, "excess_of_loss", null, priority: 100m, limit: 500m);
        await TreatyAsync(y2, "excess_of_loss", null, priority: 500m, limit: 500m);
        await Refused(await w.PolicyAsync(actor.Id, new DateOnly(y2, 1, 1), [new("damage", 20000m, 0m)]), y2, "xol-layers");

        var y3 = Interlocked.Increment(ref year);
        await TreatyAsync(y3, "quota_share", 30m, to: new DateOnly(y3, 1, 31));
        var p3 = await w.PolicyAsync(actor.Id, new DateOnly(y3, 1, 1), [new("damage", 20000m, 0m, 365m)]);
        await TreatyAsync(y3, "excess_of_loss", null, priority: 100m, limit: 500m, from: new DateOnly(y3, 2, 1));
        await Refused(p3, y3, "xol-with-proportional");

        var y4 = Interlocked.Increment(ref year);
        var a = await TreatyAsync(y4, "quota_share", 60m);
        var p4 = await w.PolicyAsync(actor.Id, new DateOnly(y4, 1, 1), [new("damage", 20000m, 0m, 365m)]);
        await CloseTreatyAsync(a);
        await TreatyAsync(y4, "quota_share", 60m);
        // A second issue row of the line (a deliberately invalid input: old or hand-made rows), ceded 60 % to the second treaty.
        await w.App.AsUserAsync(actor.Id, async tx =>
        {
            var row = Guid.NewGuid();
            await tx.ExecuteAsync("""
                insert core.premium_transaction (id, policy_line_id, kind, amount, amount_gel, booked_on, cover_from, cover_to, created_by)
                values (@row, @line, N'issue', 10, 10, @on, @on, @to, @actor)
                """, new { row, line = p4.LineOf("damage"), on = new DateOnly(y4, 1, 1), to = new DateOnly(y4 + 1, 1, 1), actor = actor.Id });
            await ReinsuranceStore.CedeIssuedPremiumAsync(tx, row, actor.Id);
            return 0;
        });
        await Refused(p4, y4, "over-ceded");

        // The allowed plans are still written: the 30 % quota alone; one GEL excess of loss alone.
        var y5 = Interlocked.Increment(ref year);
        var quota = await TreatyAsync(y5, "quota_share", 30m);
        var p5 = await w.PolicyAsync(actor.Id, new DateOnly(y5, 1, 1), [new("damage", 20000m, 0m, 365m)]);
        var c5 = (await w.RegisterAsync(actor, p5, new DateOnly(y5, 7, 15), new DateOnly(y5, 7, 16), ("damage", "1000")))[0];
        Assert.Equal([(quota, 30m, (decimal?)null, (decimal?)null)], await CessionsAsync(c5));
    }

    [Fact]
    public async Task A_facultative_share_placed_by_hand_reaches_the_claim_with_the_quota_and_the_reader_splits_by_both()
    {
        var y = Interlocked.Increment(ref year);
        var u = await w.UserAsync("admin");
        var quota = await TreatyAsync(y, "quota_share", 20m);
        var fac = await TreatyAsync(y, "facultative", 30m);
        var p = await w.PolicyAsync(u.Id, new DateOnly(y, 1, 1), [new("damage", 20000m, 0m, 365m)], days: 365);
        Assert.Equal(ReinsuranceStore.FacultativeOutcome.Placed, await w.App.AsUserAsync(u.Id, tx => ReinsuranceStore.PlaceFacultativeAsync(tx, p.Id, fac, u.Id)));
        Assert.Equal([(quota, 73.00m), (fac, 109.50m)], await w.RowsAsync<(Guid, decimal)>(
            "select c.treaty_id, c.amount from core.premium_cession c join core.premium_transaction t on t.id = c.premium_transaction_id where t.policy_line_id = @line order by c.created_at",
            new { line = p.LineOf("damage") }));
        await CloseTreatyAsync(quota);
        await CloseTreatyAsync(fac);
        await TreatyAsync(y, "quota_share", 50m);
        var claim = (await w.RegisterAsync(u, p, new DateOnly(y, 7, 15), new DateOnly(y, 7, 16), ("damage", "1000")))[0];
        Assert.Equal(new[] { (fac, 30m), (quota, 20m) }.OrderBy(t => t.Item1), (await CessionsAsync(claim)).Select(c => (c.Treaty, c.Share)).OrderBy(t => t.Item1));
        Assert.Equal([(fac, 30000L, 0L), (quota, 20000L, 0L)], await SharesAsync(claim, new DateOnly(y, 7, 19)));
        await w.ApproveAsync(u, claim, "1000", new DateOnly(y, 7, 16));
        await w.PayAsync(u, claim, "200", new DateOnly(y, 7, 20), new DateOnly(y, 7, 20));
        Assert.Equal([(fac, 24000L, 6000L), (quota, 16000L, 4000L)], await SharesAsync(claim, new DateOnly(y, 7, 31)));
        Assert.Equal([(fac, 30000L, 0L), (quota, 20000L, 0L)], await SharesAsync(claim, new DateOnly(y, 7, 19)));
    }
}
