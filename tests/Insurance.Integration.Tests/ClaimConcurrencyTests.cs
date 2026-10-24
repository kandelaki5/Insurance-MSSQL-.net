using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// Two real sessions in a forced interleaving for what the claim actions lock (docs/04 წესი 19; legacy scripts/loss-event-check.ts
/// F and L, scripts/claim-payee-race.ts, the race rows of docs/07 §4.3ა გ): a session holds a lock the action needs, the action is
/// seen waiting, the holder commits, and the action then sees what was committed. Pattern of ConcurrencyTests; each race that the
/// lock prevents also has its negative control.
/// </summary>
public sealed class ClaimConcurrencyTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly ClaimWorld w = new(db);
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(800);
    private static int year = 2600;

    /// <summary>A transaction of the application role held open by the test.</summary>
    private sealed class Held : IAsyncDisposable
    {
        public required SqlConnection Connection { get; init; }
        public required SqlTransaction Transaction { get; init; }
        public Tx Tx => new(Connection, Transaction);
        public Task<int> Run(string sql, object? param = null) => Connection.ExecuteAsync(sql, param, Transaction);
        public Task CommitAsync() => Transaction.CommitAsync();
        public async ValueTask DisposeAsync()
        {
            try { if (Transaction.Connection is not null) await Transaction.RollbackAsync(); } catch (InvalidOperationException) { }
            await Connection.DisposeAsync();
        }
    }

    private async Task<Held> HoldAsync(Guid actor)
    {
        var c = await db.OpenAppAsync();
        var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await c.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = actor.ToString() }, t);
        return new Held { Connection = c, Transaction = t };
    }

    private static async Task AssertWaitingAsync(params Task[] tasks)
    {
        foreach (var task in tasks) Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(Blocked)));
    }

    private sealed record Event(CurrentUser Claims, CurrentUser Accountant, ClaimWorld.Policy Policy, IReadOnlyList<Guid> Ids, DateOnly Today, int Year);

    private async Task<Event> EventAsync(decimal deductible, (DateOnly, decimal)[]? installments = null, decimal premium = 0m, int claims = 2, bool oneEvent = true)
    {
        var y = Interlocked.Increment(ref year);
        var u = await w.UserAsync("claims");
        var p = await w.PolicyAsync(u.Id, new DateOnly(y, 1, 1), [new("damage", 20000m, deductible, premium), new("equipment", 20000m, deductible)],
            installments?.Select(i => (new DateOnly(y, i.Item1.Month, i.Item1.Day), i.Item2)).ToArray());
        IReadOnlyList<Guid> ids = oneEvent
            ? await w.RegisterAsync(u, p, new DateOnly(y, 3, 1), new DateOnly(y, 3, 2), ("damage", "1000"), ("equipment", "1000"))
            : [(await w.RegisterAsync(u, p, new DateOnly(y, 3, 1), new DateOnly(y, 3, 2), ("damage", "1000")))[0], (await w.RegisterAsync(u, p, new DateOnly(y, 3, 5), new DateOnly(y, 3, 6), ("damage", "1000")))[0]];
        var today = new DateOnly(y, 4, 1);
        foreach (var id in ids.Take(claims)) await w.ApproveAsync(u, id, "1000", today);
        return new Event(u, await w.UserAsync("accountant"), p, ids, today, y);
    }

    [Fact]
    public async Task Two_payments_on_one_class_in_one_event_wait_for_the_event_and_withhold_its_deductible_once()
    {
        var e = await EventAsync(150m);
        var eventId = await w.ScalarAsync<Guid>("select loss_event_id from core.claim where id = @id", new { id = e.Ids[0] });
        await using var holder = await HoldAsync(e.Claims.Id);
        await holder.Run("select id from core.loss_event with (updlock, rowlock, holdlock) where id = @eventId", new { eventId });
        var payments = e.Ids.Select(id => w.PayAsync(e.Accountant, id, "150", e.Today, e.Today)).ToArray();
        await AssertWaitingAsync(payments);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id in @ids", new { ids = e.Ids }));
        await holder.CommitAsync();
        foreach (var p in await Task.WhenAll(payments)) Assert.NotNull(p.RedirectTo);
        Assert.Equal(150m, await w.ScalarAsync<decimal>("select sum(deductible_withheld) from core.claim_payment where claim_id in @ids", new { ids = e.Ids }));
    }

    /// <summary>
    /// Legacy loss-event-check.ts L1 / L2 with their own numbers: damage and equipment of one event (deductible 200 / 200), two
    /// payments of 100 + 100 (L1) or 150 + 150 (L2) sent at the same moment while another connection holds the event — both wait;
    /// once it lets go, 200 is withheld once: net 0 (L1), net 100 (L2).
    /// </summary>
    [Theory]
    [InlineData("100", 0)]
    [InlineData("150", 100)]
    public async Task L1_L2_two_small_payments_at_the_same_moment_wait_for_the_held_event_then_withhold_200_once(string each, int net)
    {
        var e = await EventAsync(200m);
        var eventId = await w.ScalarAsync<Guid>("select loss_event_id from core.claim where id = @id", new { id = e.Ids[0] });
        await using var holder = await HoldAsync(e.Claims.Id);
        await holder.Run("select id from core.loss_event with (updlock, rowlock, holdlock) where id = @eventId", new { eventId });
        var payments = e.Ids.Select(id => w.PayAsync(e.Accountant, id, each, e.Today, e.Today)).ToArray();
        await AssertWaitingAsync(payments);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id in @ids", new { ids = e.Ids }));
        await holder.CommitAsync();
        foreach (var p in await Task.WhenAll(payments)) Assert.NotNull(p.RedirectTo);
        Assert.Equal([(2, 200m, (decimal)net)], await w.RowsAsync<(int, decimal, decimal)>(
            "select count(*), sum(deductible_withheld), sum(net_paid) from core.claim_payment where claim_id in @ids", new { ids = e.Ids }));
    }

    [Fact]
    public async Task Negative_control_without_the_event_lock_two_payments_withhold_the_one_deductible_twice()
    {
        var e = await EventAsync(150m);
        // The payment's reads and writes without the event lock: both see nothing withheld, both withhold 150.
        const string Withheld = "select coalesce(sum(deductible_withheld), 0) from core.claim_payment where claim_id in @ids";
        await using var a = await HoldAsync(e.Accountant.Id);
        await using var b = await HoldAsync(e.Accountant.Id);
        var seenA = await a.Connection.ExecuteScalarAsync<decimal>(Withheld, new { ids = e.Ids }, a.Transaction);
        var seenB = await b.Connection.ExecuteScalarAsync<decimal>(Withheld, new { ids = e.Ids }, b.Transaction);
        const string Pay = """
            insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id)
            values (@claim, @on, 150, @withheld, 0, @net, @net, 1, N'policyholder', @party)
            """;
        await a.Run(Pay, new { claim = e.Ids[0], on = e.Today, withheld = 150m - seenA, net = seenA, party = e.Policy.PartyId });
        await b.Run(Pay, new { claim = e.Ids[1], on = e.Today, withheld = 150m - seenB, net = seenB, party = e.Policy.PartyId });
        await a.CommitAsync();
        await b.CommitAsync();
        Assert.Equal(300m, await w.ScalarAsync<decimal>(Withheld, new { ids = e.Ids }));
    }

    [Fact]
    public async Task Two_set_offs_on_one_policy_wait_for_the_policy_and_take_its_unpaid_premium_once()
    {
        var e = await EventAsync(0m, [(new DateOnly(2000, 1, 1), 90m)], 90m, oneEvent: false);
        await using var holder = await HoldAsync(e.Claims.Id);
        await holder.Run("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = e.Policy.Id });
        var payments = e.Ids.Select(id => w.PayAsync(e.Accountant, id, "250", e.Today, e.Today, ("offset_premium", "on"))).ToArray();
        await AssertWaitingAsync(payments);
        await holder.CommitAsync();
        foreach (var p in await Task.WhenAll(payments)) Assert.NotNull(p.RedirectTo);
        Assert.Equal([0m, 90m], (await w.RowsAsync<decimal>("select premium_offset from core.claim_payment where claim_id in @ids", new { ids = e.Ids })).Order());
        Assert.Equal(90m, await w.ScalarAsync<decimal>("select sum(amount) from core.payment_allocation where installment_id = @i", new { i = e.Policy.Installments[0] }));
    }

    [Fact]
    public async Task A_payment_waits_for_a_month_close_in_flight_and_is_then_refused_with_the_closed_month_message()
    {
        var e = await EventAsync(0m, claims: 1);
        await using var close = await HoldAsync(e.Claims.Id);
        await close.Run("""
            exec core.xact_lock N'core.accounting_period';
            insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @actor);
            """, new { month = new DateOnly(e.Year, 4, 1), actor = e.Claims.Id });
        var paying = w.PayAsync(e.Accountant, e.Ids[0], "100", e.Today, e.Today);
        await AssertWaitingAsync(paying);
        await close.CommitAsync();
        Assert.Equal(ClaimTexts.Errors.PeriodClosed, (await paying).Errors["paid_on"]);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @id", new { id = e.Ids[0] }));
    }

    [Fact]
    public async Task A_month_close_waits_for_a_payment_in_flight_and_closes_after_it()
    {
        var e = await EventAsync(0m, claims: 1);
        // The payment is held at its reserve movement, after its payment row took the period's lock shared.
        await using var blocker = await HoldAsync(e.Claims.Id);
        await blocker.Run("select top (0) id from core.claim_reserve with (tablockx, holdlock)");
        var paying = w.PayAsync(e.Accountant, e.Ids[0], "100", e.Today, e.Today);
        await AssertWaitingAsync(paying);
        await using var close = await HoldAsync(e.Claims.Id);
        var closing = close.Run("""
            exec core.xact_lock N'core.accounting_period';
            insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @actor);
            """, new { month = new DateOnly(e.Year, 4, 1), actor = e.Claims.Id });
        await AssertWaitingAsync(closing);
        await blocker.CommitAsync();
        Assert.NotNull((await paying).RedirectTo);
        await closing;
        await close.CommitAsync();
        Assert.Equal("closed", await w.ScalarAsync<string>("select status from core.accounting_period where period_start = @m", new { m = new DateOnly(e.Year, 4, 1) }));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @id", new { id = e.Ids[0] }));
    }

    [Fact]
    public async Task An_FMS_suspension_being_registered_on_the_third_payee_holds_the_payout_which_is_then_refused()
    {
        var e = await EventAsync(0m, claims: 1);
        var tax = Fixtures.RandomTaxId();
        var service = await w.PartyAsync(tax, "სერვისი");
        await using var writer = await HoldAsync(e.Claims.Id);
        await Aml.LockForSuspensionChangeAsync(writer.Tx, service);
        await writer.Run("""
            insert core.fms_suspension (party_id, kind, scope, instructed_at, ends_at) values (@service, N'written', N'all', dateadd(day, -1, sysdatetimeoffset()), dateadd(day, 5, sysdatetimeoffset()))
            """, new { service });
        var paying = w.PayAsync(e.Accountant, e.Ids[0], "100", e.Today, e.Today, ("payee_kind", "workshop"), ("payee_id_number", tax));
        await AssertWaitingAsync(paying);
        await writer.CommitAsync();
        Assert.Equal(ClaimTexts.Errors.PayeeBlocked, (await paying).Message);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @id", new { id = e.Ids[0] }));
    }

    [Fact]
    public async Task Two_reversals_of_one_payment_at_once_reverse_it_once_and_the_other_is_already()
    {
        var e = await EventAsync(0m, claims: 1);
        await w.PayAsync(e.Accountant, e.Ids[0], "100", e.Today, e.Today);
        var payment = await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @id", new { id = e.Ids[0] });
        var form = ClaimWorld.Form(("claim_id", e.Ids[0].ToString()), ("claim_payment_id", payment.ToString()), ("reason", "correction"), ("reversed_on", ClaimWorld.D(e.Today)));
        await using var holder = await HoldAsync(e.Claims.Id);
        await holder.Run("select id from core.claim with (updlock, rowlock, holdlock) where id = @id", new { id = e.Ids[0] });
        var both = new[] { ClaimActions.ReverseClaimPaymentAsync(w.App, e.Accountant, form, e.Today), ClaimActions.ReverseClaimPaymentAsync(w.App, e.Accountant, form, e.Today) };
        await AssertWaitingAsync(both);
        await holder.CommitAsync();
        Assert.Equal(["already", "ok"], (await Task.WhenAll(both)).Select(r => r!.Split('=')[1]).Order());
        Assert.Equal(2, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @id", new { id = e.Ids[0] }));
    }

    [Fact]
    public async Task Two_approvals_of_one_payment_request_at_once_pay_it_once()
    {
        var y = Interlocked.Increment(ref year);
        var u = await w.UserAsync("claims");
        var p = await w.PolicyAsync(u.Id, new DateOnly(y, 1, 1), [new("damage", 50000m, 0m)]);
        var claim = (await w.RegisterAsync(u, p, new DateOnly(y, 3, 1), new DateOnly(y, 3, 2), ("damage", "20000")))[0];
        var today = new DateOnly(y, 4, 1);
        await w.ApproveAsync(u, claim, "20000", today);
        Assert.EndsWith("payment=requested", (await w.PayAsync(await w.UserAsync("accountant"), claim, "15000", today, today)).RedirectTo);
        var request = await w.ScalarAsync<Guid>("select id from core.claim_payment_request where claim_id = @claim", new { claim });
        var admin = await w.UserAsync("admin");
        var form = ClaimWorld.Form(("claim_id", claim.ToString()), ("request_id", request.ToString()));
        await using var holder = await HoldAsync(u.Id);
        await holder.Run("select id from core.claim with (updlock, rowlock, holdlock) where id = @claim", new { claim });
        var both = new[] { ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, admin, form), ClaimActions.ApproveClaimPaymentRequestAsync(w.App, w.Rates, admin, form) };
        await AssertWaitingAsync(both);
        await holder.CommitAsync();
        Assert.Equal(["already", "approved"], (await Task.WhenAll(both)).Select(r => r!.Split('=')[1]).Order());
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @claim", new { claim }));
    }

    // ---- regress on the claim against the reversal of its payment (one lock per claim: core.lock_subrogation_claim)

    private async Task<(Event E, Guid Payment, Guid CaseId, SubrogationTransact Transact)> PaidCaseAsync()
    {
        var e = await EventAsync(0m, claims: 1);
        await w.PayAsync(e.Accountant, e.Ids[0], "1000", e.Today, e.Today);
        var payment = await w.ScalarAsync<Guid>("select id from core.claim_payment where claim_id = @id", new { id = e.Ids[0] });
        var transact = SubrogationCases.InDatabase(w.App, e.Claims.Id);
        var opened = await SubrogationCases.OpenCaseAsync(transact, e.Ids[0], e.Today, null, Guid.NewGuid());
        Assert.True(opened.Ok, opened.Error);
        return (e, payment, opened.Id!.Value, transact);
    }

    [Fact]
    public async Task A_claim_payment_reversal_waits_for_a_regress_confirmation_in_flight_and_is_then_refused_as_regress()
    {
        var (e, payment, caseId, _) = await PaidCaseAsync();
        await using var held = await HoldAsync(e.Claims.Id);
        var confirmed = await SubrogationCases.AddMovementAsync(operation => operation(new SqlSubrogationStore(held.Tx)), caseId, DocumentedMovement.Confirmed,
            100000, e.Today, DocumentKind.DebtorAcceptance, null, e.Today, null, Guid.NewGuid());
        Assert.True(confirmed.Ok, confirmed.Error);
        var reversing = ClaimActions.ReverseClaimPaymentAsync(w.App, e.Accountant, ClaimWorld.Form(("claim_id", e.Ids[0].ToString()), ("claim_payment_id", payment.ToString()),
            ("reason", "correction"), ("reversed_on", ClaimWorld.D(e.Today))), e.Today);
        await AssertWaitingAsync(reversing);
        await held.CommitAsync();
        Assert.EndsWith("reversal=regress", await reversing);
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.claim_payment where claim_id = @id", new { id = e.Ids[0] }));
    }

    [Fact]
    public async Task A_regress_confirmation_waits_for_a_payment_reversal_in_flight_and_then_finds_no_room()
    {
        var (e, payment, caseId, transact) = await PaidCaseAsync();
        await using var held = await HoldAsync(e.Accountant.Id);
        // The reversal's mirror row, written in a held transaction: its guard takes the claim's regress lock.
        await held.Run("""
            insert core.claim_payment (claim_id, paid_on, amount, deductible_withheld, premium_offset, net_paid, net_paid_gel, rate, payee_kind, payee_party_id,
                                       kind, reverses_claim_payment_id, reversal_reason)
            select claim_id, @on, -amount, -deductible_withheld, -premium_offset, -net_paid, -net_paid_gel, rate, payee_kind, payee_party_id, N'reversal', id, N'correction'
            from core.claim_payment where id = @payment
            """, new { on = e.Today, payment });
        var confirming = SubrogationCases.AddMovementAsync(transact, caseId, DocumentedMovement.Confirmed, 100000, e.Today, DocumentKind.DebtorAcceptance, null, e.Today, null, Guid.NewGuid());
        await AssertWaitingAsync(confirming);
        await held.CommitAsync();
        Assert.Equal("cap", (await confirming).Error);
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.subrogation_movement where case_id = @caseId", new { caseId }));
    }
}
