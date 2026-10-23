using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Two real sessions in a forced order (docs/04 წესი 19; legacy advance-allocation-regression R, scripts/db-acceptance.ts)
/// for what the policy and payment actions lock: two receipts on one policy, an approval against a month close, a
/// reversal against an allocation of one receipt, a suspension against a receipt. A gate transaction holds the lock the
/// actions take; the actions are started one after the other and queue on it; the gate is released and each answers in
/// the order it queued. Each has a negative control that shows the race the lock prevents.
/// </summary>
public sealed class PaymentConcurrencyTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private readonly World w = new(db);
    private static readonly TimeSpan Queue = TimeSpan.FromMilliseconds(700);
    private static readonly DateOnly Start = new(2026, 2, 1);

    private sealed class Gate : IAsyncDisposable
    {
        public required SqlConnection Connection { get; init; }
        public required SqlTransaction Transaction { get; init; }
        public Task<int> Run(string sql, object? param = null) => Connection.ExecuteAsync(sql, param, Transaction);
        public async ValueTask DisposeAsync()
        {
            try { if (Transaction.Connection is not null) await Transaction.RollbackAsync(); } catch (InvalidOperationException) { }
            await Connection.DisposeAsync();
        }
    }

    private async Task<Gate> GateAsync(Guid actor)
    {
        var c = await w.Database.OpenAppAsync();
        var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await c.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = actor.ToString() }, t);
        return new Gate { Connection = c, Transaction = t };
    }

    private static async Task WaitingAsync(Task task) => Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(Queue)));

    [Fact]
    public async Task Two_receipts_on_one_policy_settle_it_once()
    {
        var user = await w.UserAsync();
        var p = await w.ActivePolicyAsync(user, await w.ClientAsync(), Start);
        await using var gate = await GateAsync(user.Id);
        await gate.Run("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = p.Id });
        var first = w.PayAsync(user, p.Id, "365.00", Start, reference: "A");
        await WaitingAsync(first);
        var second = w.PayAsync(user, p.Id, "100.00", Start, reference: "B");
        await WaitingAsync(second);
        await gate.Transaction.CommitAsync();
        Assert.Equal($"/policies/{p.Id}?paid=0", (await first).Redirect);
        Assert.Equal(BillingTexts.Payment.Errors.NothingOwed, (await second).Message);
        Assert.Equal(365m, await w.PaidAsync(p.Id));
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where payer_party_id = @h", new { h = p.Holder }));
    }

    [Fact]
    public async Task Negative_control_without_the_policy_lock_two_receipts_settle_one_instalment_twice()
    {
        var user = await w.UserAsync();
        var p = await w.ActivePolicyAsync(user, await w.ClientAsync(), Start);
        var inst = await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @id order by due_on", new { id = p.Id });
        // What RecordPayment does without its lock: each session reads what is settled, then pays the unpaid instalment.
        await using var a = await GateAsync(user.Id);
        await using var b = await GateAsync(user.Id);
        const string settled = "select coalesce(sum(a.amount), 0) from core.payment_allocation a where a.installment_id = @inst";
        Assert.Equal(0m, await a.Connection.ExecuteScalarAsync<decimal>(settled, new { inst }, a.Transaction));
        Assert.Equal(0m, await b.Connection.ExecuteScalarAsync<decimal>(settled, new { inst }, b.Transaction));
        foreach (var s in new[] { a, b })
        {
            var pay = Guid.NewGuid();
            await s.Run("insert core.payment (id, received_on, amount_gel, method, payer_party_id) values (@pay, @on, 91.25, N'bank', @h)", new { pay, on = Start, h = p.Holder });
            await s.Run("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@pay, @inst, 91.25, 91.25, 1, @today)", new { pay, inst, today = World.Today });
        }
        await a.Transaction.CommitAsync();
        await b.Transaction.CommitAsync();
        Assert.Equal(182.50m, await w.ScalarAsync<decimal>(settled, new { inst }));
    }

    [Fact]
    public async Task An_approval_waits_for_a_month_close_in_flight_and_is_then_refused_as_the_month_is_closed()
    {
        // Owner, 13.10.2026 (legacy PR #212): nothing is approved into a closed period, and no longer booked on the first
        // open day. The approval reads the closed months under the period lock, so it waits for the close in flight and then
        // sees it: the owner's answer, nothing written — before PR #212's rule the insert itself was refused (23514) and a
        // retry booked the first open day.
        var user = await w.UserAsync();
        var month = new DateOnly(2024, 6, 1);
        var p = await w.PendingPolicyAsync(await w.ClientAsync(), month.AddDays(3));
        await using var close = await GateAsync(user.Id);
        await close.Run("exec core.xact_lock N'core.accounting_period'; insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @user)",
            new { month, user = user.Id });
        var approving = PolicyActions.ApprovePolicyAsync(w.App, user, p.Id.ToString());
        await WaitingAsync(approving);
        await close.Transaction.CommitAsync();
        var answer = await approving;
        Assert.Equal(("policy-period-closed", p.Number), (answer.Outcome, answer.Number));
        Assert.Equal("შეუძლებელია პოლისის დამტკიცება, თვე დახურულია", PolicyActions.AnswerText(answer.Outcome));
        Assert.Empty(await w.PremiumRowsAsync(p.Id));
        Assert.Equal("pending_approval", await w.ScalarAsync<string>("select status from core.policy where id = @id", new { id = p.Id }));
        Assert.Equal("policy-period-closed", (await PolicyActions.ApprovePolicyAsync(w.App, user, p.Id.ToString())).Outcome);
        Assert.Empty(await w.PremiumRowsAsync(p.Id));
    }

    [Theory]
    [InlineData("allocate")]
    [InlineData("reverse")]
    public async Task An_allocation_and_a_reversal_of_one_receipt_at_once(string first)
    {
        var user = await w.UserAsync();
        var holder = await w.ClientAsync();
        var p = await w.ActivePolicyAsync(user, holder, Start);
        var r = await w.ReceiptAsync(holder, Start.AddDays(4), 91.25m);
        await using var gate = await GateAsync(user.Id);
        await gate.Run("exec core.xact_lock @resource, N'Exclusive'", new { resource = $"core.payment_allocation:{r}" });
        Task<string?> allocation = null!, reversal = null!;
        void Allocate() => allocation = PaymentActions.AllocateReceiptAsync(w.App, w.Rates, user, p.Id.ToString(), r.ToString())!;
        void Reverse() => reversal = PaymentActions.ReversePaymentAsync(w.App, user, r.ToString(), p.Id.ToString(), "race");
        if (first == "allocate") Allocate(); else Reverse();
        await WaitingAsync(first == "allocate" ? allocation : reversal);
        if (first == "allocate") Reverse(); else Allocate();
        await WaitingAsync(first == "allocate" ? reversal : allocation);
        await gate.Transaction.CommitAsync();
        var allocated = await allocation;
        await reversal;
        var lines = await w.ScalarAsync<int>("select count(*) from core.payment_allocation where payment_id = @r", new { r });
        var mirrored = await w.ScalarAsync<decimal>("select coalesce(sum(a.amount), 0) from core.payment_allocation a join core.payment v on v.id = a.payment_id where v.reverses_payment_id = @r", new { r });
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @r", new { r }));
        if (first == "allocate")
        {
            // Allocation first: allocated 91.25; the reversal mirrors it.
            Assert.Equal(("allocated", 1, -91.25m), (World.Param(allocated, "advance"), lines, mirrored));
        }
        else
        {
            // Reversal first: the allocation finds nothing left; no lines at all.
            Assert.Equal(("nothing-left", 0, 0m), (World.Param(allocated, "advance"), lines, mirrored));
        }
        Assert.Equal(365m, await w.OwedAsync(p.Id));
    }

    [Fact]
    public async Task A_receipt_waits_for_a_suspension_being_registered_and_then_sees_it()
    {
        var user = await w.UserAsync();
        var holder = await w.ClientAsync();
        var p = await w.ActivePolicyAsync(user, holder, Start);
        await using var suspension = await GateAsync(user.Id);
        await suspension.Run("exec core.xact_lock @resource, N'Exclusive'", new { resource = $"core.party:{holder}" });
        await suspension.Run("""
            insert core.fms_suspension (party_id, kind, scope, instructed_at, ends_at) values (@holder, N'written', N'all', sysdatetimeoffset(), dateadd(hour, 72, sysdatetimeoffset()))
            """, new { holder });
        var paying = w.PayAsync(user, p.Id, "50.00", Start);
        await WaitingAsync(paying);
        await suspension.Transaction.CommitAsync();
        Assert.Equal(AmlTexts.Suspension.Blocked, (await paying).Message);
        Assert.Equal(0m, await w.PaidAsync(p.Id));
    }

    [Fact]
    public async Task A_suspension_waits_for_a_receipt_in_flight()
    {
        var user = await w.UserAsync();
        var holder = await w.ClientAsync();
        // A receipt in flight holds the client's lock shared (Aml.SuspensionInTransactionAsync) until it commits.
        await using var receipt = await GateAsync(user.Id);
        await receipt.Run("exec core.xact_lock @resource, N'Shared'", new { resource = $"core.party:{holder}" });
        // Another receipt (shared, too) does not wait for the first.
        await using (var other = await GateAsync(user.Id))
        {
            var shared = other.Run("exec core.xact_lock @resource, N'Shared'", new { resource = $"core.party:{holder}" });
            Assert.Same(shared, await Task.WhenAny(shared, Task.Delay(TimeSpan.FromSeconds(10))));
            await other.Transaction.CommitAsync();
        }
        var registering = w.SuspendAsync(holder, user.Id);
        await WaitingAsync(registering);
        await receipt.Transaction.CommitAsync();
        await registering;
        Assert.Equal(1, await w.ScalarAsync<int>("select count(*) from core.fms_suspension where party_id = @holder", new { holder }));
    }

    [Fact]
    public async Task Negative_control_a_receipt_that_skips_the_client_lock_commits_after_a_suspension_it_did_not_see()
    {
        var user = await w.UserAsync();
        var holder = await w.ClientAsync();
        var p = await w.ActivePolicyAsync(user, holder, Start);
        var inst = await w.ScalarAsync<Guid>("select top (1) id from core.installment where policy_id = @id order by due_on", new { id = p.Id });
        // The receipt checks (no suspension yet) without the lock ...
        await using var receipt = await GateAsync(user.Id);
        Assert.Null(await Aml.BlockingSuspensionAsync(new Tx(receipt.Connection, receipt.Transaction), holder));
        // ... a suspension is registered and committed meanwhile ...
        await w.SuspendAsync(holder, user.Id);
        // ... and the receipt still commits: the race the shared lock and the in-transaction check prevent.
        var pay = Guid.NewGuid();
        await receipt.Run("insert core.payment (id, received_on, amount_gel, method, payer_party_id) values (@pay, @on, 50, N'bank', @holder)", new { pay, on = Start, holder });
        await receipt.Run("insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@pay, @inst, 50, 50, 1, @today)", new { pay, inst, today = World.Today });
        await receipt.Transaction.CommitAsync();
        Assert.Equal(50m, await w.PaidAsync(p.Id));
    }
}
