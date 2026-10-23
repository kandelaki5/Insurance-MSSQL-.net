using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;

namespace Insurance.Integration.Tests;

/// <summary>
/// Reserve requests and their approval by someone else on the real SQL Server (legacy scripts/integration/claim-reserve-approval.test.ts,
/// scripts/claim-reserve-db-check.ts), and the adjuster's decisions moving the reserve. An approved GEL claim of 1 000.00 with its
/// reserve 1 000.00 booked 02.10; "today" 03.11 of the test's own year.
/// </summary>
public sealed class ClaimReserveTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2080;
    private const string Reason = "ხარჯთაღრიცხვა გაიზარდა";

    private sealed record World(CurrentUser Claims, ClaimWorld.Policy Policy, Guid Claim, DateOnly Today, int Year);

    private async Task<World> WorldAsync()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 99999999999999m, 100m)]);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "1000")))[0];
        await w.ApproveAsync(claims, claim, "1000", new DateOnly(y, 10, 2));
        return new World(claims, policy, claim, new DateOnly(y, 11, 3), y);
    }

    private Task<string?> AskAsync(World x, CurrentUser who, string level = "1200.50", string? bookedOn = null, string reason = Reason) =>
        ClaimActions.ChangeReserveAsync(w.App, who, ClaimWorld.Form(("claim_id", x.Claim.ToString()), ("booked_on", bookedOn ?? ClaimWorld.D(x.Today)), ("new_level", level),
            ("reason", reason)), x.Today);

    private async Task<Guid> LastRequestAsync(World x) =>
        await w.ScalarAsync<Guid>("select top (1) id from core.claim_reserve_request where claim_id = @c order by created_at desc", new { c = x.Claim });

    private async Task<string?> ApproveAsync(World x, CurrentUser who, Guid? request = null, Guid? claim = null) =>
        await ClaimActions.ApproveReserveRequestAsync(w.App, who, ClaimWorld.Form(("claim_id", (claim ?? x.Claim).ToString()), ("request_id", (request ?? await LastRequestAsync(x)).ToString())));

    private static string Back(World x, string outcome) => $"/claims/{x.Claim}?reserve={outcome}";

    private Task<List<(DateOnly BookedOn, decimal Change, Guid? CreatedBy, Guid? RequestId)>> MovementsAsync(World x) =>
        w.RowsAsync<(DateOnly, decimal, Guid?, Guid?)>("select booked_on, change, created_by, request_id from core.claim_reserve where claim_id = @c order by created_at", new { c = x.Claim });

    private (DateOnly, decimal, Guid?, Guid?) Opening(World x) => (new DateOnly(x.Year, 10, 2), 1000m, x.Claims.Id, null);

    [Fact]
    public async Task A_request_records_base_level_and_difference_and_moves_nothing_until_another_user_approves_it()
    {
        var x = await WorldAsync();
        Assert.Equal(Back(x, "requested"), await AskAsync(x, x.Claims));
        Assert.Equal([(x.Today, 1000m, 1200.50m, 200.50m, Reason, x.Claims.Id)], await w.RowsAsync<(DateOnly, decimal, decimal, decimal, string, Guid)>(
            "select booked_on, base_level, new_level, change, reason, created_by from core.claim_reserve_request where claim_id = @c", new { c = x.Claim }));
        Assert.Equal([Opening(x)], await MovementsAsync(x));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.audit_log where table_name = N'claim_reserve_request' and action = N'insert' and user_id = @u", new { u = x.Claims.Id }));
        var request = await LastRequestAsync(x);
        var admin = await w.UserAsync("admin");
        Assert.Equal(Back(x, "approved"), await ApproveAsync(x, admin));
        Assert.Equal([Opening(x), (x.Today, 200.50m, admin.Id, request)], await MovementsAsync(x));
        Assert.Equal(Back(x, "already"), await ApproveAsync(x, admin));
        Assert.Equal(2, (await MovementsAsync(x)).Count);
    }

    [Fact]
    public async Task No_one_approves_their_own_request_the_admin_included_another_person_with_the_permission_does()
    {
        var x = await WorldAsync();
        var admin = await w.UserAsync("admin");
        Assert.Equal(Back(x, "requested"), await AskAsync(x, admin));
        Assert.Equal(Back(x, "own"), await ApproveAsync(x, admin));
        Assert.Equal([Opening(x)], await MovementsAsync(x));
        var underwriter = await w.UserAsync("underwriter");
        await w.GroupAsync("claim.reserve.approve", underwriter.Id);
        Assert.Equal(Back(x, "approved"), await ApproveAsync(x, underwriter));
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("accountant", false)]
    [InlineData("claims", false)]
    [InlineData("operations_head", false)]
    [InlineData("underwriter", false)]
    [InlineData("sales", false)]
    [InlineData("viewer", false)]
    public async Task Who_may_approve_by_role(string role, bool may)
    {
        var x = await WorldAsync();
        await AskAsync(x, x.Claims);
        Assert.Equal(Back(x, may ? "approved" : "approve-not-allowed"), await ApproveAsync(x, await w.UserAsync(role)));
        Assert.Equal(may ? 2 : 1, (await MovementsAsync(x)).Count);
    }

    [Fact]
    public async Task A_group_gives_it_a_personal_removal_beats_the_group_and_payment_rights_do_not_give_it()
    {
        var x = await WorldAsync();
        await AskAsync(x, x.Claims);
        var accountant = await w.UserAsync("accountant");
        await w.GrantAsync(accountant.Id, "claim.payment.record", "allow");
        Assert.Equal(Back(x, "approve-not-allowed"), await ApproveAsync(x, accountant));
        var underwriter = await w.UserAsync("underwriter");
        await w.GroupAsync("claim.reserve.approve", underwriter.Id);
        await w.GrantAsync(underwriter.Id, "claim.reserve.approve", "deny");
        Assert.Equal(Back(x, "approve-not-allowed"), await ApproveAsync(x, underwriter));
        await w.ExecAsync("delete from core.user_permission where user_id = @u", new { u = underwriter.Id });
        Assert.Equal(Back(x, "approved"), await ApproveAsync(x, underwriter));
    }

    [Fact]
    public async Task A_viewer_cannot_ask_and_any_writer_can_but_only_asks()
    {
        var x = await WorldAsync();
        Assert.Equal(Back(x, "not-allowed"), await AskAsync(x, await w.UserAsync("viewer")));
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_reserve_request where claim_id = @c", new { c = x.Claim }));
        Assert.Equal(Back(x, "requested"), await AskAsync(x, await w.UserAsync("sales")));
        Assert.Equal([Opening(x)], await MovementsAsync(x));
    }

    [Fact]
    public async Task A_reserve_moved_since_the_request_is_stale_and_a_new_request_from_750_is_approved()
    {
        var x = await WorldAsync();
        await AskAsync(x, x.Claims);
        var accountant = await w.UserAsync("accountant");
        Assert.NotNull((await w.PayAsync(accountant, x.Claim, "250.00", x.Today, x.Today)).RedirectTo);
        var admin = await w.UserAsync("admin");
        Assert.Equal(Back(x, "stale"), await ApproveAsync(x, admin));
        Assert.Equal(2, (await MovementsAsync(x)).Count);
        Assert.Equal(Back(x, "requested"), await AskAsync(x, x.Claims, "900.00"));
        var request = await LastRequestAsync(x);
        Assert.Equal(Back(x, "approved"), await ApproveAsync(x, admin, request));
        Assert.Equal((x.Today, 150m, (Guid?)admin.Id, (Guid?)request), (await MovementsAsync(x))[^1]);
    }

    [Fact]
    public async Task A_request_dated_in_a_month_closed_before_the_approval_is_period_closed_twice()
    {
        var x = await WorldAsync();
        Assert.Equal(Back(x, "requested"), await AskAsync(x, x.Claims, bookedOn: ClaimWorld.D(new DateOnly(x.Year, 10, 30))));
        await w.CloseMonthAsync(x.Claims.Id, new DateOnly(x.Year, 10, 1));
        var admin = await w.UserAsync("admin");
        for (var i = 0; i < 2; i++) Assert.Equal(Back(x, "period-closed"), await ApproveAsync(x, admin));
        Assert.Equal([Opening(x)], await MovementsAsync(x));
    }

    [Fact]
    public async Task A_closed_claim_another_claims_request_or_a_made_up_id_is_refused()
    {
        var x = await WorldAsync();
        await AskAsync(x, x.Claims);
        var admin = await w.UserAsync("admin");
        await ClaimActions.DecideClaimAsync(w.App, x.Claims, ClaimWorld.Form(("claim_id", x.Claim.ToString()), ("decision", "close")), x.Today);
        Assert.Equal(Back(x, "state"), await ApproveAsync(x, admin));
        Assert.Equal(Back(x, "state"), await ApproveAsync(x, admin, Guid.NewGuid()));
        var other = Guid.NewGuid();
        Assert.Equal($"/claims/{other}?reserve=state", await ApproveAsync(x, admin, claim: other));
        Assert.Equal(Back(x, "state"), await AskAsync(x, x.Claims));
        Assert.Equal(Back(x, "state"), await ClaimActions.ApproveReserveRequestAsync(w.App, admin, ClaimWorld.Form(("claim_id", x.Claim.ToString()), ("request_id", "nope"))));
    }

    [Fact]
    public async Task The_same_level_a_negative_or_malformed_level_no_reason_or_a_future_day_asks_nothing()
    {
        var x = await WorldAsync();
        Assert.Equal(Back(x, "unchanged"), await AskAsync(x, x.Claims, "1000.00"));
        Assert.Equal(Back(x, "invalid"), await AskAsync(x, x.Claims, "-1"));
        Assert.Equal(Back(x, "invalid"), await AskAsync(x, x.Claims, "12.345"));
        Assert.Equal(Back(x, "invalid"), await AskAsync(x, x.Claims, "ათასი"));
        Assert.Equal(Back(x, "invalid"), await AskAsync(x, x.Claims, reason: "  "));
        Assert.Equal(Back(x, "invalid"), await AskAsync(x, x.Claims, reason: new string('ა', 501)));
        Assert.Equal(Back(x, "invalid"), await AskAsync(x, x.Claims, bookedOn: ClaimWorld.D(x.Today.AddDays(1))));
        Assert.Equal(Back(x, "requested"), await AskAsync(x, x.Claims, "1000,5"));
        Assert.Equal(1000.50m, await w.ScalarAsync<decimal>("select new_level from core.claim_reserve_request where claim_id = @c", new { c = x.Claim }));
        Assert.Null(await ClaimActions.ChangeReserveAsync(w.App, x.Claims, ClaimWorld.Form(("claim_id", "x")), x.Today));
    }

    [Fact]
    public async Task The_approvals_movement_failing_once_leaves_nothing_and_the_retry_books_one()
    {
        var x = await WorldAsync();
        await AskAsync(x, x.Claims);
        var admin = await w.UserAsync("admin");
        await w.ArmFaultAsync("claim_reserve");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ApproveAsync(x, admin));
        }
        finally
        {
            await w.DisarmAsync();
        }
        Assert.Equal([Opening(x)], await MovementsAsync(x));
        Assert.Equal(Back(x, "approved"), await ApproveAsync(x, admin));
        Assert.Equal(2, (await MovementsAsync(x)).Count);
    }

    [Fact]
    public async Task Exact_decimals_beyond_two_to_the_53_are_asked_and_booked_exactly()
    {
        var x = await WorldAsync();
        Assert.Equal(Back(x, "requested"), await AskAsync(x, x.Claims, "90071992547408.01"));
        Assert.Equal([(1000m, 90071992547408.01m, 90071992546408.01m)], await w.RowsAsync<(decimal, decimal, decimal)>(
            "select base_level, new_level, change from core.claim_reserve_request where claim_id = @c", new { c = x.Claim }));
        Assert.Equal(Back(x, "approved"), await ApproveAsync(x, await w.UserAsync("admin")));
        Assert.Equal(90071992547408.01m, (await MovementsAsync(x)).Sum(m => m.Change));
    }

    [Fact]
    public async Task On_a_reinsured_claim_the_reinsurers_reserve_follows_the_approval_only()
    {
        var x = await WorldAsync();
        var treaty = Guid.NewGuid();
        await w.ExecAsync("""
            insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent)
            values (@treaty, @number, N'კვოტა 30 %', @reinsurer, N'quota_share', @from, @to, N'GEL', 30);
            insert core.claim_cession (claim_id, treaty_id, share_percent) values (@claim, @treaty, 30);
            """, new { treaty, number = "QS-" + ClaimWorld.Tag(), reinsurer = await w.PartyAsync(), from = new DateOnly(x.Year, 1, 1), to = new DateOnly(x.Year, 12, 31), claim = x.Claim });
        async Task<long> Share(DateOnly? asOf = null) => await w.App.ReadTxAsync(async tx => (await ReinsuranceStore.ClaimSharesAsync(tx, x.Claim, asOf)).Single().InReserve);
        Assert.Equal(30000, await Share());
        var admin = await w.UserAsync("admin");
        await AskAsync(x, admin);
        Assert.Equal(30000, await Share());
        Assert.Equal(Back(x, "own"), await ApproveAsync(x, admin));
        Assert.Equal(30000, await Share());
        var underwriter = await w.UserAsync("underwriter");
        await w.GroupAsync("claim.reserve.approve", underwriter.Id);
        Assert.Equal(Back(x, "approved"), await ApproveAsync(x, underwriter));
        Assert.Equal(36015, await Share());
        Assert.Equal(30000, await Share(new DateOnly(x.Year, 10, 31)));
        Assert.Equal(Back(x, "already"), await ApproveAsync(x, admin));
        Assert.Equal(36015, await Share());
    }

    // ---- the database's own guards (legacy scripts/claim-reserve-db-check.ts)

    [Fact]
    public async Task The_database_refuses_a_movement_that_does_not_carry_out_its_request_exactly()
    {
        var x = await WorldAsync();
        var admin = await w.UserAsync("admin");
        await AskAsync(x, x.Claims);
        var request = await LastRequestAsync(x);
        async Task<string?> Book(Guid by, decimal change, Guid? claim = null, DateOnly? on = null)
        {
            try
            {
                await w.App.AsUserAsync(by, tx => tx.ExecuteAsync(
                    "insert core.claim_reserve (claim_id, booked_on, change, reason, created_by, request_id) values (@claim, @on, @change, N'x', @by, @request)",
                    new { claim = claim ?? x.Claim, on = on ?? x.Today, change, by, request }));
                return "ok";
            }
            catch (Exception e) { return DbError.From(e)?.Code; }
        }
        Assert.Equal("RR003", await Book(x.Claims.Id, 200.50m));
        Assert.Equal("RR002", await Book(admin.Id, 200.49m));
        Assert.Equal("RR002", await Book(admin.Id, 200.50m, on: x.Today.AddDays(-1)));
        var other = (await w.RegisterAsync(x.Claims, x.Policy, new DateOnly(x.Year, 10, 5), new DateOnly(x.Year, 10, 6), ("damage", "10")))[0];
        Assert.Equal("RR001", await Book(admin.Id, 200.50m, other));
        Assert.Equal("ok", await Book(admin.Id, 200.50m));
        Assert.Equal("RR004", await Book(admin.Id, 200.50m));
        // A movement without a request (a payment, a decision) is not touched by the guard.
        Assert.Equal("ok", await w.App.AsUserAsync(admin.Id, async tx =>
        {
            await tx.ExecuteAsync("insert core.claim_reserve (claim_id, booked_on, change, reason, created_by) values (@c, @on, -200.50, N'x', @by)", new { c = x.Claim, on = x.Today, by = admin.Id });
            return "ok";
        }));
        Assert.Equal(DbError.UniqueViolation, await Book(admin.Id, 200.50m));
        // The application role cannot change or remove a request.
        await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync("update core.claim_reserve_request set new_level = 1 where id = @request", new { request }));
        await Assert.ThrowsAnyAsync<Exception>(() => w.ExecAsync("delete from core.claim_reserve_request where id = @request", new { request }));
    }

    // ---- decisions (decideClaim)

    [Fact]
    public async Task Decisions_review_approve_reject_close_reopen_move_the_status_and_the_reserve()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 5000m, 300m)]);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "800")))[0];
        var today = new DateOnly(y, 11, 3);
        Task Decide(params (string, string)[] f) => ClaimActions.DecideClaimAsync(w.App, claims, ClaimWorld.Form([("claim_id", claim.ToString()), .. f]), today);
        async Task<(string Status, decimal? Approved, decimal Deductible, bool TotalLoss, string? Reason, DateOnly? ClosedOn)> State() =>
            (await w.RowsAsync<(string, decimal?, decimal, bool, string?, DateOnly?)>(
                "select status, approved_amount, deductible_amount, is_total_loss, rejection_reason, closed_on from core.claim where id = @claim", new { claim })).Single();
        async Task<decimal> Level() => await w.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claim", new { claim });

        await Decide(("decision", "close"));
        Assert.Equal("registered", (await State()).Status);
        await Decide(("decision", "review"));
        Assert.Equal("in_review", (await State()).Status);
        await Decide(("decision", "approve"), ("approved_amount", "0"));
        Assert.Equal("in_review", (await State()).Status);
        // Above the limit, the approved amount is the limit; the deductible is the line's, at most the approved amount.
        await Decide(("decision", "approve"), ("approved_amount", "6000"), ("is_total_loss", "on"));
        Assert.Equal(("approved", 5000m, 300m, true, (string?)null, (DateOnly?)null), await State());
        Assert.Equal(5000m, await Level());
        Assert.Equal([(today, 4200m, ClaimTexts.Actions.Approve)], (await w.RowsAsync<(DateOnly, decimal, string)>(
            "select booked_on, change, reason from core.claim_reserve where claim_id = @claim order by created_at", new { claim })).Skip(1));
        await Decide(("decision", "close"));
        Assert.Equal(("closed", today), ((await State()).Status, (await State()).ClosedOn!.Value));
        Assert.Equal(0m, await Level());
        await Decide(("decision", "reopen"));
        Assert.Equal(("in_review", (DateOnly?)null), ((await State()).Status, (await State()).ClosedOn));
        await Decide(("decision", "reject"), ("rejection_reason", "   "));
        Assert.Equal("in_review", (await State()).Status);
        await Decide(("decision", "reject"), ("rejection_reason", " დაზღვევით გაუთვალისწინებელი "));
        Assert.Equal(("rejected", "დაზღვევით გაუთვალისწინებელი"), ((await State()).Status, (await State()).Reason));
        await Decide(("decision", "reopen"));
        await Decide(("decision", "approve"), ("approved_amount", "100,5"));
        Assert.Equal(("approved", 100.5m, 100.5m), ((await State()).Status, (await State()).Approved!.Value, (await State()).Deductible));
        Assert.Equal(100.5m, await Level());
        await Decide(("decision", "approve"), ("approved_amount", "200"));
        Assert.Equal(100.5m, (await State()).Approved);
    }
}
