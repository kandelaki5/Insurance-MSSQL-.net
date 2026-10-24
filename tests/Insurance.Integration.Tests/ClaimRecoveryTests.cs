using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests;

/// <summary>
/// Money recovered after a claim (salvage, regress) by addRecovery on the real SQL Server (legacy scripts/integration/recovery-permission.test.ts,
/// claim-recovery-viewer-refusal.test.ts, claim-recovery-monthly-report.test.ts): its own permission, the stored amount checked,
/// income in the month received, the history kept.
/// </summary>
public sealed class ClaimRecoveryTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static int year = 2300;

    private async Task<(Guid Claim, DateOnly Today, int Year)> PaidClaimAsync()
    {
        var y = Interlocked.Increment(ref year);
        var claims = await w.UserAsync("claims");
        var policy = await w.PolicyAsync(claims.Id, new DateOnly(y, 10, 1), [new ClaimWorld.Line("damage", 20000m, 0m)]);
        var claim = (await w.RegisterAsync(claims, policy, new DateOnly(y, 10, 1), new DateOnly(y, 10, 2), ("damage", "1000")))[0];
        await w.ApproveAsync(claims, claim, "1000", new DateOnly(y, 10, 2));
        var admin = await w.UserAsync("admin");
        await w.PayAsync(admin, claim, "900", new DateOnly(y, 10, 6), new DateOnly(y, 10, 6));
        // An earlier regress of 7.50 received 20.10.
        Assert.Null(await AddAsync(admin, claim, "subrogation", new DateOnly(y, 10, 20), "7.50"));
        return (claim, new DateOnly(y, 11, 3), y);
    }

    private Task<string?> AddAsync(CurrentUser who, Guid claim, string kind, DateOnly on, string amount, string? note = null) =>
        ClaimActions.AddRecoveryAsync(w.App, who, ClaimWorld.Form(("claim_id", claim.ToString()), ("kind", kind), ("received_on", ClaimWorld.D(on)), ("amount_gel", amount), ("note", note ?? "")));

    private Task<List<(string Kind, DateOnly On, decimal Amount, string? Note, Guid? By)>> RowsAsync(Guid claim) =>
        w.RowsAsync<(string, DateOnly, decimal, string?, Guid?)>("select kind, received_on, amount_gel, note, created_by from core.claim_recovery where claim_id = @claim order by created_at", new { claim });

    [Theory]
    [InlineData("accountant", true)]
    [InlineData("admin", true)]
    [InlineData("underwriter", false)]
    [InlineData("sales", false)]
    [InlineData("claims", false)]
    [InlineData("operations_head", false)]
    [InlineData("viewer", false)]
    public async Task The_roles_starting_set_records_25_or_goes_back_to_the_claim_with_the_refusal(string role, bool may)
    {
        var (claim, today, _) = await PaidClaimAsync();
        var who = await w.UserAsync(role);
        var outcome = await AddAsync(who, claim, "subrogation", today, "25.00", " რეგრესი ");
        var rows = await RowsAsync(claim);
        if (may)
        {
            Assert.Null(outcome);
            Assert.Equal(("subrogation", today, 25m, "რეგრესი", (Guid?)who.Id), rows[^1]);
        }
        else
        {
            Assert.Equal($"/claims/{claim}?recovery=not-allowed", outcome);
            Assert.Single(rows);
        }
    }

    [Fact]
    public async Task A_personal_grant_or_a_group_gives_it_a_personal_removal_takes_it_and_a_group_cannot_undo_that()
    {
        var (claim, today, _) = await PaidClaimAsync();
        var viewer = await w.UserAsync("viewer");
        await w.GrantAsync(viewer.Id, "claim.recovery.receive", "allow");
        Assert.Null(await AddAsync(viewer, claim, "salvage", today, "1"));
        var underwriter = await w.UserAsync("underwriter");
        var beka = await w.UserAsync("underwriter");
        await w.GroupAsync("claim.recovery.receive", underwriter.Id);
        Assert.Equal($"/claims/{claim}?recovery=not-allowed", await AddAsync(beka, claim, "salvage", today, "1"));
        Assert.Null(await AddAsync(underwriter, claim, "salvage", today, "1"));
        var accountant = await w.UserAsync("accountant");
        await w.GrantAsync(accountant.Id, "claim.recovery.receive", "deny");
        await w.GroupAsync("claim.recovery.receive", accountant.Id);
        Assert.Equal($"/claims/{claim}?recovery=not-allowed", await AddAsync(accountant, claim, "salvage", today, "1"));
        // Another permission gives nothing.
        await w.GrantAsync(beka.Id, "claim.payment.record", "allow");
        Assert.Equal($"/claims/{claim}?recovery=not-allowed", await AddAsync(beka, claim, "salvage", today, "1"));
        Assert.Equal(3, (await RowsAsync(claim)).Count);
    }

    [Fact]
    public async Task The_stored_amount_is_checked_0_004_and_0_are_refused_0_005_is_stored_as_0_01()
    {
        var (claim, today, _) = await PaidClaimAsync();
        var admin = await w.UserAsync("admin");
        foreach (var bad in new[] { "0.00", "0.004", "-5", "x", "" }) Assert.Null(await AddAsync(admin, claim, "salvage", today, bad));
        Assert.Null(await AddAsync(admin, claim, "nothing", today, "5"));
        Assert.Null(await ClaimActions.AddRecoveryAsync(w.App, admin, ClaimWorld.Form(("claim_id", claim.ToString()), ("kind", "salvage"), ("received_on", "03.11.2026"), ("amount_gel", "5"))));
        Assert.Single(await RowsAsync(claim));
        Assert.Null(await AddAsync(admin, claim, "salvage", today, "0.005"));
        Assert.Null(await AddAsync(admin, claim, "salvage", today, "0.01"));
        Assert.Equal([0.01m, 0.01m], (await RowsAsync(claim)).Skip(1).Select(r => r.Amount));
        Assert.Null(await ClaimActions.AddRecoveryAsync(w.App, await w.UserAsync("viewer"), ClaimWorld.Form(("claim_id", "x"))));
    }

    [Fact]
    public async Task A_recovery_counts_in_the_month_it_was_received_once_and_the_history_stays()
    {
        var (claim, today, y) = await PaidClaimAsync();
        var before = await RowsAsync(claim);
        var accountant = await w.UserAsync("accountant");
        Assert.Null(await AddAsync(accountant, claim, "salvage", new DateOnly(y, 10, 31), "50.00", "ნარჩენი"));
        Assert.Null(await AddAsync(accountant, claim, "subrogation", today, "25.00"));
        async Task<decimal> Month(int month) => await w.ScalarAsync<decimal>(
            "select coalesce(sum(amount_gel), 0) from core.claim_recovery where claim_id = @claim and year(received_on) = @y and month(received_on) = @month", new { claim, y, month });
        Assert.Equal((0m, 57.50m, 25m), (await Month(9), await Month(10), await Month(11)));
        Assert.Equal(before, (await RowsAsync(claim)).Take(1).ToList());
        Assert.Equal((1000m, 900m), (await w.ScalarAsync<decimal>("select sum(change) from core.claim_reserve where claim_id = @claim and change > 0", new { claim }),
            await w.ScalarAsync<decimal>("select sum(amount) from core.claim_payment where claim_id = @claim", new { claim })));
    }

    [Fact]
    public async Task Once_a_claim_has_a_regress_case_its_regress_money_goes_through_the_case_salvage_does_not()
    {
        var (claim, today, y) = await PaidClaimAsync();
        var admin = await w.UserAsync("admin");
        var opened = await SubrogationCases.OpenCaseAsync(SubrogationCases.InDatabase(w.App, admin.Id), claim, today, null, Guid.NewGuid());
        Assert.True(opened.Ok, opened.Error);
        var e = await Assert.ThrowsAnyAsync<Exception>(() => AddAsync(admin, claim, "subrogation", today, "5"));
        Assert.Equal("SG032", DbError.From(e)?.Code);
        Assert.Null(await AddAsync(admin, claim, "salvage", today, "5"));
        // A closed month is not caught by the action (as before): the database refuses it.
        await w.CloseMonthAsync(admin.Id, new DateOnly(y, 12, 1));
        Assert.Equal(DbError.CheckViolation, DbError.From(await Assert.ThrowsAnyAsync<Exception>(() => AddAsync(admin, claim, "salvage", new DateOnly(y, 12, 3), "5")))?.Code);
    }
}
