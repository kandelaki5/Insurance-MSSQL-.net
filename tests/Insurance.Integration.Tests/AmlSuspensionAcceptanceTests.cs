using System.Data;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Compliance;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// The AML suspension scenarios of the legacy acceptance run (scripts/db-acceptance.ts H11, H13, the owner's decision of
/// 04.10.2026 on an oral instruction, the claim target and the attach of an old unmapped suspension), on two real
/// application sessions in a forced interleaving. The operations call the Aml service as the payment and claim actions
/// do: their policy or claim row first, then the client's lock shared and the suspension check inside the transaction.
/// </summary>
public sealed class AmlSuspensionAcceptanceTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(800);
    private const int Hold = 1500;

    private sealed class Session : IAsyncDisposable
    {
        public required SqlConnection Connection { get; init; }
        public required SqlTransaction Transaction { get; init; }
        public Tx Tx => new(Connection, Transaction);
        public async ValueTask DisposeAsync()
        {
            try { if (Transaction.Connection is not null) await Transaction.RollbackAsync(); } catch (InvalidOperationException) { }
            await Connection.DisposeAsync();
        }
    }

    private async Task<Session> BeginAsync(Guid actor)
    {
        var c = await db.OpenAppAsync();
        var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await c.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = actor.ToString() }, t);
        return new Session { Connection = c, Transaction = t };
    }

    private static async Task AssertWaitingAsync(Task task) => Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(Blocked)));

    private async Task<(Guid Actor, Fixtures.PolicyRows Policy)> PolicyAsync()
    {
        var actor = await Fixtures.UserAsync(db);
        return (actor, await db.App.AsUserAsync(actor, tx => Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(2026, 8, 1))));
    }

    private sealed record Outcome(string Result, DateTimeOffset At);

    /// <summary>A receipt on the policy as the payment action takes it: the policy row, then the client shared and the check; then it is written.</summary>
    private async Task<Outcome> ReceiptAsync(Guid actor, Fixtures.PolicyRows p, bool checkOutside = false, Func<Task>? afterCheck = null)
    {
        if (checkOutside && await db.App.ReadTxAsync(tx => Aml.BlockingSuspensionAsync(tx, p.PartyId)) is not null)
            return new Outcome("refused: suspended", DateTimeOffset.UtcNow);
        await using var s = await BeginAsync(actor);
        await s.Tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = p.PolicyId });
        if (!checkOutside && await Aml.SuspensionInTransactionAsync(s.Tx, p.PartyId, new OperationTargets([p.PolicyId.ToString()])) is not null)
            return new Outcome("refused: suspended", DateTimeOffset.UtcNow);
        if (afterCheck is not null) await afterCheck();
        await s.Tx.ExecuteAsync("insert core.payment (received_on, amount_gel, method, payer_party_id, created_by) values ('2026-10-02', 100, N'bank', @party, @actor)", new { party = p.PartyId, actor });
        await s.Transaction.CommitAsync();
        return new Outcome("committed", DateTimeOffset.UtcNow);
    }

    /// <summary>createSuspension of all of the client's operations; <paramref name="withoutLock"/> is the order before the review (no client lock).</summary>
    private async Task<DateTimeOffset> SuspendAsync(Guid actor, Guid party, bool withoutLock = false, string kind = "written", DateTimeOffset? instructedAt = null)
    {
        await using var s = await BeginAsync(actor);
        if (!withoutLock) await Aml.LockForSuspensionChangeAsync(s.Tx, party);
        var at = instructedAt ?? DateTimeOffset.UtcNow;
        await s.Tx.ExecuteAsync("""
            insert core.fms_suspension (id, party_id, kind, scope, instructed_at, written_confirmed_at, ends_at, created_by)
            values (newid(), @party, @kind, N'all', @at, @confirmed, @ends, @actor)
            """, new { party, kind, at, confirmed = kind == "written" ? at : (DateTimeOffset?)null, ends = Suspension.SuspensionEnd(at), actor });
        await s.Transaction.CommitAsync();
        return DateTimeOffset.UtcNow;
    }

    [Fact]
    public async Task H11_a_suspension_waits_for_a_receipt_in_flight_and_the_next_receipt_is_refused()
    {
        var (actor, p) = await PolicyAsync();
        var checkedIn = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var receipt = Task.Run(() => ReceiptAsync(actor, p, afterCheck: async () => { checkedIn.SetResult(); await release.Task; }));
        await checkedIn.Task;
        var suspension = Task.Run(() => SuspendAsync(actor, p.PartyId));
        await AssertWaitingAsync(suspension);
        release.SetResult();
        var r = await receipt;
        var suspendedAt = await suspension;
        Assert.Equal("committed", r.Result);
        Assert.True(r.At <= suspendedAt, "the receipt in flight committed before the suspension");
        Assert.Equal("refused: suspended", (await ReceiptAsync(actor, p)).Result);
    }

    [Fact]
    public async Task H11_negative_control_with_the_check_outside_the_transaction_a_receipt_commits_after_the_suspension()
    {
        var (actor, p) = await PolicyAsync();
        var checkedIn = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var receipt = Task.Run(() => ReceiptAsync(actor, p, checkOutside: true, afterCheck: async () => { checkedIn.SetResult(); await release.Task; }));
        await checkedIn.Task;
        // Without the client's lock the suspension does not wait.
        var suspendedAt = await SuspendAsync(actor, p.PartyId, withoutLock: true);
        release.SetResult();
        var r = await receipt;
        Assert.Equal("committed", r.Result);
        Assert.True(r.At > suspendedAt, "the race shows: the receipt committed AFTER the suspension");
    }

    [Fact]
    public async Task An_oral_instruction_without_the_letter_stays_in_force_after_24_hours_until_the_officer_lifts_it()
    {
        var (actor, p) = await PolicyAsync();
        await SuspendAsync(actor, p.PartyId, kind: "oral", instructedAt: DateTimeOffset.UtcNow.AddHours(-25));
        var row = (await db.App.ReadTxAsync(tx => Aml.SuspensionsOfAsync(tx, partyId: p.PartyId))).Single();
        Assert.Equal("unconfirmed", row.State);
        Assert.Equal("refused: suspended", (await ReceiptAsync(actor, p)).Result);
        // The letter arrives late: it is recorded and the suspension stays in force.
        await db.App.AsUserAsync(actor, async tx =>
        {
            await Aml.LockForSuspensionChangeAsync(tx, p.PartyId);
            return await tx.ExecuteAsync("update core.fms_suspension set written_confirmed_at = sysdatetimeoffset() where id = @id and written_confirmed_at is null and lifted_at is null", new { id = row.Id });
        });
        Assert.Equal("active", (await db.App.ReadTxAsync(tx => Aml.SuspensionsOfAsync(tx, partyId: p.PartyId))).Single().State);
        Assert.Equal("refused: suspended", (await ReceiptAsync(actor, p)).Result);

        // Another client: the officer resumes the operations with a reason, then the receipt is accepted.
        var (actor2, q) = await PolicyAsync();
        await SuspendAsync(actor2, q.PartyId, kind: "oral", instructedAt: DateTimeOffset.UtcNow.AddHours(-25));
        await db.App.AsUserAsync(actor2, tx => tx.ExecuteAsync(
            "update core.fms_suspension set lifted_at = sysdatetimeoffset(), lift_reason = N'წერილობითი მითითება ვადაში არ მოსულა (მუხ. 36, პ. 3)' where party_id = @party", new { party = q.PartyId }));
        Assert.Equal("committed", (await ReceiptAsync(actor2, q)).Result);
    }

    /// <summary>
    /// H13: a suspension aimed at one policy while a receipt on that policy holds its row and has not yet locked the client.
    /// With the shared order (target first, then the client) neither side waits on the other in a cycle: the suspension
    /// waits for the receipt, the receipt commits first, then the suspension, and the next receipt is refused (legacy
    /// scripts/db-acceptance.ts H13 expectation).
    /// </summary>
    [Fact]
    public async Task H13_a_suspension_of_one_policy_against_a_receipt_on_it_does_not_deadlock_and_no_receipt_slips_past_it()
    {
        var (actor, p) = await PolicyAsync();
        var number = await db.App.ReadTxAsync(tx => tx.ExecuteScalarAsync<string>("select number from core.policy where id = @id", new { id = p.PolicyId }));
        var policyLocked = new TaskCompletionSource();
        var receipt = Task.Run(async () =>
        {
            await using var s = await BeginAsync(actor);
            await s.Tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = p.PolicyId });
            policyLocked.SetResult();
            await Task.Delay(Hold);
            if (await Aml.SuspensionInTransactionAsync(s.Tx, p.PartyId, new OperationTargets([p.PolicyId.ToString()])) is not null)
                return new Outcome("refused: suspended", DateTimeOffset.UtcNow);
            await s.Tx.ExecuteAsync("insert core.payment (received_on, amount_gel, method, payer_party_id) values ('2026-10-02', 100, N'bank', @party)", new { party = p.PartyId });
            await s.Transaction.CommitAsync();
            return new Outcome("committed", DateTimeOffset.UtcNow);
        });
        await policyLocked.Task;
        await Task.Delay(200);
        var suspension = Task.Run(async () =>
        {
            await using var s = await BeginAsync(actor);
            var target = await Aml.SuspensionTargetAsync(s.Tx, p.PartyId, number!);
            await Aml.LockForSuspensionChangeAsync(s.Tx, p.PartyId, target!.Value.PolicyId, target.Value.ClaimId);
            await s.Tx.ExecuteAsync("""
                insert core.fms_suspension (id, party_id, kind, scope, transaction_ref, target_policy_id, instructed_at, written_confirmed_at, ends_at)
                values (newid(), @party, N'written', N'transaction', @number, @policy, sysdatetimeoffset(), sysdatetimeoffset(), dateadd(day, 3, sysdatetimeoffset()))
                """, new { party = p.PartyId, number = target.Value.Number, policy = target.Value.PolicyId });
            await s.Transaction.CommitAsync();
            return DateTimeOffset.UtcNow;
        });
        var outcome = await receipt;
        var suspendedAt = await suspension;
        // No deadlock (either side would have thrown 1205): the suspension's update lock of the target waits for the
        // receipt's, so the receipt commits first and the suspension after it.
        Assert.Equal("committed", outcome.Result);
        Assert.True(outcome.At <= suspendedAt);
        Assert.Equal("refused: suspended", (await ReceiptAsync(actor, p)).Result);
    }

    /// <summary>A claim payment in flight (claim, then policy, then the client) while the claim is suspended, or an old unmapped suspension is attached to it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_suspension_of_a_claim_or_an_attach_to_it_against_a_claim_payment_in_flight(bool attach)
    {
        var (actor, p) = await PolicyAsync();
        var claim = await db.App.AsUserAsync(actor, tx => ComplianceFixtures.ClaimAsync(tx, p));
        var legacy = attach ? await ComplianceFixtures.LegacyUnmappedSuspensionAsync(db, p.PartyId, "PAY-LEGACY-1") : Guid.Empty;
        var targets = new OperationTargets([p.PolicyId.ToString()], [claim.ClaimId.ToString()], [claim.Number]);
        async Task<Outcome> PayAsync(Func<Task>? afterLocks = null)
        {
            await using var s = await BeginAsync(actor);
            await s.Tx.ExecuteAsync("select id from core.claim with (updlock, rowlock, holdlock) where id = @id", new { id = claim.ClaimId });
            await s.Tx.ExecuteAsync("select id from core.policy with (updlock, rowlock, holdlock) where id = @id", new { id = p.PolicyId });
            if (afterLocks is not null) await afterLocks();
            if (await Aml.SuspensionInTransactionAsync(s.Tx, p.PartyId, targets) is not null) return new Outcome("refused: suspended", DateTimeOffset.UtcNow);
            await s.Tx.ExecuteAsync("insert core.payment (received_on, amount_gel, method, payer_party_id) values ('2026-10-02', 100, N'bank', @party)", new { party = p.PartyId });
            await s.Transaction.CommitAsync();
            return new Outcome("committed", DateTimeOffset.UtcNow);
        }
        // Before any suspension the payment goes through.
        Assert.Equal("committed", (await PayAsync()).Result);
        var locked = new TaskCompletionSource();
        var payment = Task.Run(() => PayAsync(async () => { locked.SetResult(); await Task.Delay(Hold); }));
        await locked.Task;
        await Task.Delay(200);
        var change = Task.Run(async () =>
        {
            await using var s = await BeginAsync(actor);
            var target = await Aml.SuspensionTargetAsync(s.Tx, p.PartyId, claim.Number.ToLowerInvariant());
            await Aml.LockForSuspensionChangeAsync(s.Tx, p.PartyId, target!.Value.PolicyId, target.Value.ClaimId);
            if (attach)
                await s.Tx.ExecuteAsync("update core.fms_suspension set target_claim_id = @claimId, transaction_ref = @number where id = @legacy and legacy_unmapped = 1",
                    new { claimId = target.Value.ClaimId, number = target.Value.Number, legacy });
            else
                await s.Tx.ExecuteAsync("""
                    insert core.fms_suspension (id, party_id, kind, scope, transaction_ref, target_claim_id, instructed_at, written_confirmed_at, ends_at)
                    values (newid(), @party, N'written', N'transaction', @number, @claimId, sysdatetimeoffset(), sysdatetimeoffset(), dateadd(day, 3, sysdatetimeoffset()))
                    """, new { party = p.PartyId, number = target.Value.Number, claimId = target.Value.ClaimId });
            await s.Transaction.CommitAsync();
            return DateTimeOffset.UtcNow;
        });
        var paid = await payment;
        var changedAt = await change;
        // No deadlock: the payment commits first, the suspension (or the attach) waits for it (legacy db-acceptance).
        Assert.Equal("committed", paid.Result);
        Assert.True(paid.At <= changedAt);
        Assert.Equal("refused: suspended", (await PayAsync()).Result);
        if (attach)
            Assert.False(await db.App.ReadTxAsync(tx => tx.ExecuteScalarAsync<bool>("select legacy_unmapped from core.fms_suspension where id = @legacy", new { legacy })));
        // A receipt on another policy of the client is not blocked by a suspension of this claim.
        var other = await db.App.AsUserAsync(actor, async tx =>
        {
            var contract = Guid.NewGuid();
            var policy = Guid.NewGuid();
            await tx.ExecuteAsync("""
                insert core.contract (id, number, signed_on, policyholder_id, created_by) values (@contract, @cno, '2026-08-01', @party, @actor);
                insert core.policy (id, contract_id, number, issued_on, effective_from, effective_to, status, premium_currency, premium_rate, segment)
                values (@policy, @contract, @pno, '2026-08-01', '2026-08-01', '2027-08-01', N'pending_approval', N'GEL', 1, N'retail');
                """, new { contract, cno = "C-O" + contract.ToString("N")[..8], party = p.PartyId, actor, policy, pno = "P-O" + policy.ToString("N")[..8] });
            return policy;
        });
        Assert.Null(await db.App.ReadTxAsync(tx => Aml.BlockingSuspensionAsync(tx, p.PartyId, new OperationTargets([other.ToString()]))));
    }
}
