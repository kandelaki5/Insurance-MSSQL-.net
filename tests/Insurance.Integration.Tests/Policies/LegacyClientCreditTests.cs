using System.Text.Json;
using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Legacy scripts/client-credit-regression.ts (owner 07.10.2026, docs/05 ა14; Codex H57–H61, INS-CLIENT-CREDIT-ROLLBACK-
/// CONCURRENCY-EVIDENCE): a client's overpayment on one policy used to pay another policy of the same client, through the
/// application's own actions (approve, record payment, endorse, allocate, transfer, refund, reverse) on a real database as
/// core_app. Same cases (C1–C17), amounts and dates: CASCO policies of 365.00 over 365 days in 4 × 91.25 from 01.01.2026,
/// receipts on day 5, a −100 change on day 100. USD cases: the script used the first stored USD rate (SKIP without one);
/// here the test stores its own fictional 2.70 on the days it needs. Forced overlaps (C7–C11, C15) hold a lock on a
/// connection of their own and see each action wait on its own new session (<see cref="Overlap"/>).
/// </summary>
public sealed class LegacyClientCreditTests(TestDatabase db) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private readonly World w = new(db);
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly Change = Start.AddDays(99);
    private const decimal Usd = 2.7m;
    private CurrentUser admin = null!;

    public async Task InitializeAsync()
    {
        admin = await w.UserAsync();
        // The fictional USD rate of the USD cases, on the days they receive and change on (a rate is never fetched in a test).
        for (var d = 0; d <= 12; d++)
            if (await w.ScalarAsync<int>("select count(*) from core.exchange_rate where currency = N'USD' and rate_date = @d", new { d = Start.AddDays(d) }) == 0)
                await w.RateAsync("USD", Start.AddDays(d), Usd);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------- fixtures and readers

    private async Task<World.Policy> PolicyAsync(Guid holder, bool pending = false, decimal premium = 365m, IReadOnlyList<(int, decimal)>? schedule = null, string currency = "GEL")
    {
        var p = await w.PendingPolicyAsync(holder, Start, currency, currency == "GEL" ? 1m : Usd, premium, schedule);
        if (!pending) Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, admin, p.Id.ToString())).Outcome);
        return p;
    }

    private async Task<Guid> PayAsync(Guid policyId, string amount, DateOnly on)
    {
        var reference = "R-" + Guid.NewGuid().ToString("N")[..10];
        var answer = await w.PayAsync(admin, policyId, amount, on, reference: reference);
        Assert.True(answer.Redirect is not null, answer.Message ?? string.Join(",", answer.Errors.Values));
        return await w.ScalarAsync<Guid>("select id from core.payment where reference = @reference", new { reference });
    }

    /// <summary>A change of <paramref name="delta"/> on the day, approved (the outcome).</summary>
    private async Task<string> ReduceAsync(Guid policyId, string delta, DateOnly on)
    {
        var endorsed = await w.EndorseAsync(admin, policyId, on, delta, $"შემცირება {delta}");
        Assert.True(endorsed.Redirect is not null, endorsed.Message ?? string.Join(",", endorsed.Errors.Select(e => $"{e.Key}={e.Value}")));
        var change = await w.ScalarAsync<Guid?>("select id from core.policy where replaces_policy_id = @policyId and status = N'pending_approval'", new { policyId });
        return change is { } id ? (await PolicyActions.ApprovePolicyAsync(w.App, admin, id.ToString())).Outcome : "no change";
    }

    /// <summary>Policy A paid in full (365) by receipt R, reduced −100 and approved: R holds 100 beyond A's schedule.</summary>
    private async Task<(World.Policy A, Guid R)> OverpaidAsync(Guid holder, string amount = "365.00")
    {
        var a = await PolicyAsync(holder);
        var r = await PayAsync(a.Id, amount, Start.AddDays(4));
        Assert.Equal("done", await ReduceAsync(a.Id, "-100", Change));
        return (a, r);
    }

    private Task<string> Transfer(Guid policyId, Guid paymentId, CurrentUser? user = null) =>
        PaymentActions.TransferOverpaymentAsync(w.App, w.Rates, user ?? admin, policyId.ToString(), paymentId.ToString());

    private Task<string> Allocate(Guid policyId, Guid paymentId) =>
        PaymentActions.AllocateReceiptAsync(w.App, w.Rates, admin, policyId.ToString(), paymentId.ToString());

    private Task<string?> Reverse(Guid paymentId, Guid policyId, string reason = "შებრუნება") =>
        PaymentActions.ReversePaymentAsync(w.App, admin, paymentId.ToString(), policyId.ToString(), reason);

    private static string? Outcome(string? redirect) => World.Param(redirect, "advance");

    private sealed record Position(decimal Scheduled, decimal Settled, List<(decimal Amount, decimal Settled)> Rows)
    {
        public decimal Overpaid => Math.Max(Settled - Scheduled, 0);
        public decimal Owed => Math.Max(Scheduled - Settled, 0);
    }

    private async Task<Position> PositionAsync(Guid policyId)
    {
        var rows = await w.QueryAsync<(decimal Amount, decimal Settled)>("""
            select i.amount + coalesce((select sum(j.change) from core.installment_adjustment j where j.installment_id = i.id), 0),
                   coalesce((select sum(a.amount) from core.payment_allocation a where a.installment_id = i.id), 0)
            from core.installment i where i.policy_id = @policyId order by i.due_on
            """, new { policyId });
        return new Position(rows.Sum(r => r.Amount), rows.Sum(r => r.Settled), rows);
    }

    private Task<(decimal Gel, int Lines)> ReceiptAsync(Guid paymentId) => w.QueryAsync<(decimal, int)>(
        "select coalesce(sum(amount_gel), 0), count(*) from core.payment_allocation where payment_id = @paymentId", new { paymentId }).ContinueWith(t => t.Result.Single());

    /// <summary>What a receipt settles on one policy (sum of its lines there), in the policy's currency and in GEL.</summary>
    private Task<(decimal Amount, decimal Gel)> HoldingAsync(Guid policyId, Guid paymentId) => w.QueryAsync<(decimal, decimal)>("""
        select coalesce(sum(a.amount), 0), coalesce(sum(a.amount_gel), 0) from core.payment_allocation a join core.installment i on i.id = a.installment_id
        where i.policy_id = @policyId and a.payment_id = @paymentId
        """, new { policyId, paymentId }).ContinueWith(t => t.Result.Single());

    private Task<IReadOnlyList<Billing.ReleasableRow>> ListedAsync(Guid holder, Guid exceptPolicy) =>
        w.App.ReadTxAsync(tx => Billing.LoadReleasableAsync(tx, holder, World.Today, exceptPolicy));

    /// <summary>What a receipt still holds unallocated, in GEL.</summary>
    private Task<decimal> LeftAsync(Guid paymentId) => w.AvailableAsync(paymentId);

    private sealed record ReversalState(Guid? Reversal, decimal? HeaderGel, string Own, int MirrorLines, decimal MirrorAmount, decimal MirrorGel, bool AllZero, int Instalments);

    /// <summary>The receipt's and its reversal's lines: per instalment and in all, both currencies; the receipt's own lines exactly (ids, amounts, dates).</summary>
    private async Task<ReversalState> ReversalAsync(Guid paymentId)
    {
        var reversal = (await w.QueryAsync<(Guid Id, decimal Gel)>("select id, amount_gel from core.payment where reverses_payment_id = @paymentId", new { paymentId })).Cast<(Guid Id, decimal Gel)?>().SingleOrDefault();
        var own = await w.QueryAsync<(Guid, Guid, decimal, decimal, decimal, DateOnly)>(
            "select id, installment_id, amount, amount_gel, rate, booked_on from core.payment_allocation where payment_id = @paymentId order by id", new { paymentId });
        var per = await w.QueryAsync<(Guid Inst, decimal Amount, decimal Gel)>("""
            select installment_id, sum(amount), sum(amount_gel) from core.payment_allocation where payment_id = @paymentId or payment_id = @rev group by installment_id
            """, new { paymentId, rev = reversal?.Id ?? Guid.Empty });
        var (n, amount, gel) = reversal is { } r
            ? (await w.QueryAsync<(int, decimal, decimal)>("select count(*), coalesce(sum(amount), 0), coalesce(sum(amount_gel), 0) from core.payment_allocation where payment_id = @id", new { id = r.Id })).Single()
            : (0, 0m, 0m);
        return new ReversalState(reversal?.Id, reversal?.Gel, JsonSerializer.Serialize(own), n, amount, gel, per.All(p => p.Amount == 0 && p.Gel == 0), per.Count);
    }

    private Task<int> ReversalsAsync(Guid paymentId) => w.ScalarAsync<int>("select count(*) from core.payment where reverses_payment_id = @paymentId", new { paymentId });

    // ---------------------------------------------------------------- C1–C4

    [Fact]
    public async Task C1_C2_C3_C4_an_overpayment_listed_and_transferred_once_to_the_same_clients_policy_only()
    {
        var holder = await w.ClientAsync();
        var other = await w.ClientAsync();
        // C1
        var (a, receipt) = await OverpaidAsync(holder);
        var p1 = await PositionAsync(a.Id);
        Assert.Equal((265m, 365m, 100m), (p1.Scheduled, p1.Settled, p1.Overpaid));

        // C2
        var b = await PolicyAsync(holder, pending: true);
        var listed = await ListedAsync(holder, b.Id);
        Assert.Equal(100m, listed.Sum(r => r.ExcessGel));
        Assert.Equal([a.Number], listed.Select(r => r.PolicyNumber).Distinct());
        var before = await ReceiptAsync(receipt);
        Assert.Equal("transferred", Outcome(await Transfer(b.Id, receipt)));
        var negatives = await w.QueryAsync<decimal>("""
            select a.amount from core.payment_allocation a join core.installment i on i.id = a.installment_id
            where a.payment_id = @receipt and i.policy_id = @a and a.amount < 0 order by i.due_on
            """, new { receipt, a = a.Id });
        Assert.Equal([-8.75m, -91.25m], negatives);
        var (pa, pb, after) = (await PositionAsync(a.Id), await PositionAsync(b.Id), await ReceiptAsync(receipt));
        Assert.Equal((0m, 100m, 265m), (pa.Overpaid, pb.Settled, pb.Owed));
        Assert.Equal([91.25m, 8.75m, 0m, 0m], pb.Rows.Select(r => r.Settled));
        Assert.Equal((365m, before.Lines + 4), after);

        // C3
        Assert.Equal("nothing-left", Outcome(await Transfer(b.Id, receipt)));
        Assert.Equal("read-only", Outcome(await Transfer(b.Id, receipt, await w.UserAsync("viewer"))));
        Assert.Equal(after, await ReceiptAsync(receipt));

        // C4
        var (_, receipt2) = await OverpaidAsync(holder);
        var c = await PolicyAsync(other, pending: true);
        Assert.Equal("not-found", Outcome(await Transfer(c.Id, receipt2)));
        Assert.Equal(4, (await ReceiptAsync(receipt2)).Lines);
        Assert.Equal(365m, (await PositionAsync(c.Id)).Owed);
    }

    // ---------------------------------------------------------------- C5 (H57)

    [Fact]
    public async Task C5_a_usd_holding_released_in_two_parts_keeps_its_gel_exactly()
    {
        var holder = await w.ClientAsync();
        var u = await PolicyAsync(holder, premium: 0.02m, schedule: [(0, 0.02m)], currency: "USD");
        var receipt = await PayAsync(u.Id, "0.05", Start);
        Assert.Equal((0.02m, 0.05m), await HoldingAsync(u.Id, receipt));
        var targets = new[] { await PolicyAsync(holder, true, 1m, [(0, 1m)], "USD"), await PolicyAsync(holder, true, 1m, [(0, 1m)], "USD") };
        var listedGel = new List<decimal>();
        for (var k = 0; k < 2; k++)
        {
            Assert.Equal("done", await ReduceAsync(u.Id, "-0.01", Start.AddDays(k + 1)));
            listedGel.Add((await ListedAsync(holder, targets[k].Id)).Where(r => r.PaymentId == receipt).Sum(r => r.ExcessGel));
            Assert.Equal("transferred", Outcome(await Transfer(targets[k].Id, receipt)));
        }
        // 0.01 USD at 2.70 is 0.03 GEL from a 0.05 holding, then the last 0.01 takes the remainder 0.02 exactly.
        Assert.Equal([0.03m, 0.02m], listedGel);
        Assert.Equal((0m, 0m), await HoldingAsync(u.Id, receipt));
        Assert.Equal(0.05m, (await HoldingAsync(targets[0].Id, receipt)).Gel + (await HoldingAsync(targets[1].Id, receipt)).Gel);
        Assert.Equal(0.05m, (await ReceiptAsync(receipt)).Gel);
    }

    // ---------------------------------------------------------------- C6 (H58)

    [Fact]
    public async Task C6_a_transfer_and_an_advance_allocation_of_one_receipt_at_the_same_moment_both_finish_and_spend_each_amount_once()
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder, "400.00");
        var a2 = await PolicyAsync(holder);
        var b = await PolicyAsync(holder, pending: true);
        var both = await Task.WhenAll(Allocate(a2.Id, r), Transfer(b.Id, r));
        Assert.Equal(("allocated", "transferred"), (Outcome(both[0]), Outcome(both[1])));
        Assert.Equal((35m, 100m, 265m), ((await HoldingAsync(a2.Id, r)).Amount, (await HoldingAsync(b.Id, r)).Amount, (await HoldingAsync(a.Id, r)).Amount));
        Assert.Equal(0m, (await PositionAsync(a.Id)).Overpaid);
        Assert.Equal(400m, (await ReceiptAsync(r)).Gel);
    }

    // ---------------------------------------------------------------- C7–C11: forced overlaps

    private const string LockPolicy = "select id from core.policy with (updlock, rowlock, holdlock) where id = @id";

    [Fact]
    public async Task C7_policy_A_held_the_advance_allocation_and_the_transfer_both_finish_without_a_deadlock_and_a_foreign_waiter_is_not_counted()
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder);
        var b = await PolicyAsync(holder, pending: true);
        await using var overlap = new Overlap(db);
        await using var hold = await overlap.HoldAsync();
        await hold.Run(LockPolicy, new { id = a.Id });
        // A connection of the application's role, outside the actions, waiting on A with the same statement.
        await using var foreign = await db.OpenAppAsync();
        var foreignWait = foreign.ExecuteAsync(LockPolicy, new { id = a.Id });
        var foreignSession = await overlap.NewWaiterAsync(hold.Session, new HashSet<int>(), foreignWait);
        Assert.NotEqual(0, foreignSession);
        var o = await overlap.RunAsync(hold, new HashSet<int> { foreignSession }, [() => Allocate(a.Id, r), () => Transfer(b.Id, r)]);
        Assert.All(o.Sessions, s => Assert.NotEqual(0, s));
        Assert.Equal(2, o.Sessions.Distinct().Count());
        Assert.DoesNotContain(foreignSession, o.Sessions);
        Assert.All(o.Errors, Assert.Null);
        Assert.Equal(("nothing-left", "transferred"), (Outcome(o.Results[0]), Outcome(o.Results[1])));
        Assert.Equal(100m, (await HoldingAsync(b.Id, r)).Amount);
        Assert.Equal(365m, (await ReceiptAsync(r)).Gel);
        await foreignWait;
    }

    [Fact]
    public async Task C7_negative_control_the_old_lock_order_target_receipt_then_source_deadlocks_with_the_allocation()
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder);
        var b = await PolicyAsync(holder, pending: true);
        var resource = $"core.payment_allocation:{r.ToString().ToLowerInvariant()}";
        await using var overlap = new Overlap(db);
        // The allocation: A, then the receipt. The old transfer: B (target), the receipt, then A (source).
        await using var allocation = await overlap.HoldAsync(app: true, actor: admin.Id);
        await using var oldTransfer = await overlap.HoldAsync(app: true, actor: admin.Id);
        await allocation.Run(LockPolicy, new { id = a.Id });
        await oldTransfer.Run(LockPolicy, new { id = b.Id });
        await oldTransfer.Run("exec core.xact_lock @resource", new { resource });
        var x = allocation.Run("exec core.xact_lock @resource", new { resource });
        Assert.True(await overlap.SeenBlockedAsync(allocation.Session, oldTransfer.Session, x));
        var y = oldTransfer.Run(LockPolicy, new { id = a.Id });
        var errors = new List<string?>();
        foreach (var t in new[] { x, y })
        {
            var e = await Record.ExceptionAsync(() => t);
            errors.Add(e is null ? null : DbError.From(e) is { } d ? d.Code + " " + d.Message : e.Message);
        }
        // Exactly one of the two is chosen as the deadlock victim (an application lock reports it as 55P03 … (-3)).
        Assert.Single(errors, e => e is not null && (e.StartsWith("40P01", StringComparison.Ordinal) || (e.StartsWith("55P03", StringComparison.Ordinal) && e.Contains("(-3)", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task C8_policy_A_held_two_transfers_of_the_same_excess_one_transfers_100_and_the_other_finds_nothing_left()
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder);
        var b1 = await PolicyAsync(holder, pending: true);
        var b2 = await PolicyAsync(holder, pending: true);
        await using var overlap = new Overlap(db);
        await using var hold = await overlap.HoldAsync();
        await hold.Run(LockPolicy, new { id = a.Id });
        var o = await overlap.RunAsync(hold, new HashSet<int>(), [() => Transfer(b1.Id, r), () => Transfer(b2.Id, r)]);
        Assert.All(o.Sessions, s => Assert.NotEqual(0, s));
        Assert.Equal(2, o.Sessions.Distinct().Count());
        Assert.Equal(["nothing-left", "transferred"], o.Results.Select(Outcome).Order(StringComparer.Ordinal));
        var (s1, s2) = ((await HoldingAsync(b1.Id, r)).Amount, (await HoldingAsync(b2.Id, r)).Amount);
        Assert.Equal(100m, s1 + s2);
        Assert.True(s1 == 100m || s2 == 100m);
        Assert.Equal(265m, (await HoldingAsync(a.Id, r)).Amount);
        Assert.Equal(365m, (await ReceiptAsync(r)).Gel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C9_policy_A_held_a_transfer_and_a_refund_of_the_same_excess_each_amount_goes_one_way(bool transferFirst)
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder);
        var b = await PolicyAsync(holder, pending: true);
        var reference = "C9 refund " + Guid.NewGuid().ToString("N")[..6];
        Task<string?> RefundStart() => PaymentActions.RefundOverpaymentAsync(w.App, w.Rates, admin, a.Id.ToString(), "bank", reference);
        async Task<string?> TransferStart() => await Transfer(b.Id, r);
        await using var overlap = new Overlap(db);
        await using var hold = await overlap.HoldAsync();
        await hold.Run(LockPolicy, new { id = a.Id });
        var o = await overlap.RunAsync<string?>(hold, new HashSet<int>(), transferFirst ? [TransferStart, RefundStart] : [RefundStart, TransferStart]);
        Assert.All(o.Sessions, s => Assert.NotEqual(0, s));
        Assert.Equal(2, o.Sessions.Distinct().Count());
        Assert.All(o.Errors, Assert.Null);
        var moved = Outcome(transferFirst ? o.Results[0] : o.Results[1]);
        var refunded = (await w.QueryAsync<(decimal, int)>("select coalesce(sum(amount_gel), 0), count(*) from core.payment where kind = N'refund' and reference = @reference", new { reference })).Single();
        var bSettled = (await HoldingAsync(b.Id, r)).Amount;
        if (transferFirst) Assert.Equal(("transferred", 0, 100m), (moved, refunded.Item2, bSettled));
        else Assert.Equal(("nothing-left", -100m, 0m), (moved, refunded.Item1, bSettled));
        Assert.Equal(265m, await w.PaidAsync(a.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C10_the_receipts_lock_held_a_transfer_and_a_reversal_the_receipt_and_its_reversal_net_to_zero(bool transferFirst)
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder);
        var b = await PolicyAsync(holder, pending: true);
        Task<string?> ReversalStart() => Reverse(r, a.Id, "C10 შებრუნება");
        async Task<string?> TransferStart() => await Transfer(b.Id, r);
        await using var overlap = new Overlap(db);
        await using var hold = await overlap.HoldAsync();
        await hold.Run("exec core.xact_lock @resource, N'Exclusive'", new { resource = $"core.payment_allocation:{r.ToString().ToLowerInvariant()}" });
        var o = await overlap.RunAsync<string?>(hold, new HashSet<int>(), transferFirst ? [TransferStart, ReversalStart] : [ReversalStart, TransferStart]);
        Assert.All(o.Sessions, s => Assert.NotEqual(0, s));
        Assert.Equal(2, o.Sessions.Distinct().Count());
        Assert.All(o.Errors, Assert.Null);
        var moved = Outcome(transferFirst ? o.Results[0] : o.Results[1]);
        var state = await ReversalAsync(r);
        Assert.NotNull(state.Reversal);
        var net = (await w.QueryAsync<(decimal, decimal)>("select coalesce(sum(amount), 0), coalesce(sum(amount_gel), 0) from core.payment_allocation where payment_id in (@r, @rev)", new { r, rev = state.Reversal })).Single();
        Assert.Equal((0m, 0m), net);
        var bSettledByR = (await HoldingAsync(b.Id, r)).Amount;
        if (transferFirst) Assert.Equal(("transferred", 100m, 0m), (moved, bSettledByR, await w.PaidAsync(b.Id)));
        else Assert.Equal(("nothing-left", 0m), (moved, bSettledByR));
    }

    [Fact]
    public async Task C11_the_transfer_killed_after_its_negative_source_lines_leaves_nothing_behind_the_retry_transfers_once_and_a_foreign_waiter_is_left_alone()
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder);
        var b = await PolicyAsync(holder, pending: true);
        var d = await PolicyAsync(holder);
        async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
        {
            allocations = await w.QueryAsync<(Guid, Guid, decimal, decimal, DateOnly)>("select id, installment_id, amount, amount_gel, booked_on from core.payment_allocation where payment_id = @r order by id", new { r }),
            adjustments = await w.QueryAsync<(Guid, Guid, decimal, DateOnly)>("""
                select j.id, j.installment_id, j.change, j.booked_on from core.installment_adjustment j join core.installment i on i.id = j.installment_id
                where i.policy_id in (@a, @b) order by j.id
                """, new { a = a.Id, b = b.Id }),
            policies = await w.QueryAsync<(Guid, string, DateOnly)>("select id, status, effective_to from core.policy where id in (@a, @b) order by id", new { a = a.Id, b = b.Id }),
            receipt = await w.QueryAsync<(decimal, DateOnly, string)>("select amount_gel, received_on, kind from core.payment where id = @r", new { r }),
        });
        var before = await SnapshotAsync();
        await using var overlap = new Overlap(db);
        await using var hold = await overlap.HoldAsync();
        // The target's instalments held exclusively (legacy: FOR UPDATE): the transfer writes the source's negative lines,
        // then its first line on the target waits (the foreign key check reads the instalment). On SQL Server a select with
        // XLOCK does not stop a read-committed reader of an unchanged row, so the holder changes the rows to themselves
        // (amount = amount, audited like any change) and is rolled back at the end: nothing of it stays. The holder also
        // holds policy D, on which a foreign connection of the application role waits: the same lock chain.
        await hold.Run("update core.installment set amount = amount where policy_id = @b", new { b = b.Id });
        await hold.Run(LockPolicy, new { id = d.Id });
        await using var foreign = await db.OpenAppAsync();
        var foreignWait = foreign.ExecuteAsync(LockPolicy, new { id = d.Id });
        var foreignSession = await overlap.NewWaiterAsync(hold.Session, new HashSet<int>(), foreignWait);
        Assert.NotEqual(0, foreignSession);
        var sourceWritten = false;
        var killed = 0;
        var o = await overlap.RunAsync(hold, new HashSet<int> { foreignSession }, [() => Transfer(b.Id, r)], async sessions =>
        {
            var mine = sessions[0];
            // Only the action's own proven session: seen waiting, new in the chain, not the foreign one.
            Assert.NotEqual(0, mine);
            Assert.NotEqual(foreignSession, mine);
            sourceWritten = await overlap.HoldsWrittenRowsAsync(mine, "core.payment_allocation");
            await overlap.KillAsync(mine);
            killed = mine;
        }, commit: false);
        await foreignWait;
        Assert.True(sourceWritten, "the source's negative lines were not written when the target's line waited");
        Assert.Equal(o.Sessions[0], killed);
        Assert.NotNull(o.Errors[0]);
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal("transferred", Outcome(await Transfer(b.Id, r)));
        Assert.Equal((100m, 365m), ((await HoldingAsync(b.Id, r)).Amount, (await ReceiptAsync(r)).Gel));
        var settled = await SnapshotAsync();
        Assert.Equal("nothing-left", Outcome(await Transfer(b.Id, r)));
        Assert.Equal(settled, await SnapshotAsync());
    }

    // ---------------------------------------------------------------- C12–C14: USD receipts released in parts, reversed

    [Fact]
    public async Task C12_C14_two_receipts_on_one_usd_instalment_released_in_parts_to_a_usd_and_a_gel_policy_then_one_reversed()
    {
        var holder = await w.ClientAsync();
        var s = await PolicyAsync(holder, premium: 0.04m, schedule: [(0, 0.04m)], currency: "USD");
        var r1 = await PayAsync(s.Id, "0.05", Start);
        var r2 = await PayAsync(s.Id, "0.05", Start);
        Assert.Equal((0.02m, 0.05m), await HoldingAsync(s.Id, r1));
        Assert.Equal((0.02m, 0.05m), await HoldingAsync(s.Id, r2));
        var v = await PolicyAsync(holder, true, 1m, [(0, 1m)], "USD");
        var wGel = await PolicyAsync(holder, true, 365m, [(0, 365m)]);
        Assert.Equal("done", await ReduceAsync(s.Id, "-0.03", Start.AddDays(1)));
        static string By(IReadOnlyList<Billing.ReleasableRow> rows, Guid id) =>
            string.Join(" + ", rows.Where(x => x.PaymentId == id).Select(x => $"{x.Excess:0.00} USD / {x.ExcessGel:0.00} GEL"));
        var listed1 = await ListedAsync(holder, v.Id);
        Assert.Equal(("0.02 USD / 0.05 GEL", "0.01 USD / 0.03 GEL"), (By(listed1, r2), By(listed1, r1)));
        Assert.Equal("transferred", Outcome(await Transfer(v.Id, r2)));
        Assert.Equal("transferred", Outcome(await Transfer(wGel.Id, r1)));
        Assert.Equal((0.01m, 0.02m), await HoldingAsync(s.Id, r1));
        Assert.Equal("done", await ReduceAsync(s.Id, "-0.01", Start.AddDays(2)));
        Assert.Equal("0.01 USD / 0.02 GEL", By(await ListedAsync(holder, v.Id), r1));
        Assert.Equal("transferred", Outcome(await Transfer(v.Id, r1)));
        Assert.Equal((0m, 0m), await HoldingAsync(s.Id, r1));
        Assert.Equal((0m, 0m), await HoldingAsync(s.Id, r2));
        var gelOn = async (Guid p) => await w.ScalarAsync<decimal>("select coalesce(sum(a.amount_gel), 0) from core.payment_allocation a join core.installment i on i.id = a.installment_id where i.policy_id = @p", new { p });
        Assert.Equal((0.07m, 0.03m), (await gelOn(v.Id), await gelOn(wGel.Id)));
        Assert.Equal((0.05m, 0.05m), ((await ReceiptAsync(r1)).Gel, (await ReceiptAsync(r2)).Gel));

        // C14: R1 (+0.02/0.05 on S, −0.01/−0.03 and +0.03 on W, −0.01/−0.02 and +0.01/+0.02 on V) reversed.
        var r2Before = await ReversalAsync(r2);
        async Task<string> ScheduleAsync() => JsonSerializer.Serialize(await w.QueryAsync<(Guid, decimal, string)>("""
            select i.id, i.amount, coalesce((select string_agg(concat(j.id, ':', j.change, ':', j.booked_on), ',') within group (order by j.id) from core.installment_adjustment j where j.installment_id = i.id), '')
            from core.installment i where i.policy_id = @p order by i.due_on
            """, new { p = s.Id }));
        var schedule = await ScheduleAsync();
        var before = await ReversalAsync(r1);
        var lines = (await ReceiptAsync(r1)).Lines;
        Assert.Null(await Reverse(r1, s.Id, "C14 შებრუნება"));
        var after = await ReversalAsync(r1);
        Assert.Equal((-0.05m, lines, true), (after.HeaderGel, after.MirrorLines, after.AllZero));
        Assert.Equal(before.Own, after.Own);
        var r2After = await ReversalAsync(r2);
        Assert.Equal((r2Before.Own, (Guid?)null), (r2After.Own, r2After.Reversal));
        Assert.Equal(schedule, await ScheduleAsync());
        var rates = await w.QueryAsync<(decimal Rate, int N)>("select rate, count(*) from core.payment_allocation where payment_id = @id group by rate order by rate", new { id = after.Reversal });
        Assert.Equal([(1m, 1), (Usd, 4)], rates);
    }

    // ---------------------------------------------------------------- C13, C15–C17: reversing a receipt that gave money back

    [Fact]
    public async Task C13_a_partly_allocated_receipt_with_a_transfer_reversed_then_reversed_again()
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder, "400.00");
        var b = await PolicyAsync(holder, pending: true);
        Assert.Equal("transferred", Outcome(await Transfer(b.Id, r)));
        Assert.Equal(35m, await LeftAsync(r));
        var before = await ReversalAsync(r);
        var lines = (await ReceiptAsync(r)).Lines;
        Assert.Null(await Reverse(r, a.Id));
        var after = await ReversalAsync(r);
        Assert.Equal((-400m, lines, -365m, true), (after.HeaderGel, after.MirrorLines, after.MirrorGel, after.AllZero));
        Assert.Equal(before.Own, after.Own);
        Assert.Null(await Reverse(r, a.Id, "შებრუნება 2"));
        var again = await ReversalAsync(r);
        Assert.Equal((1, after.MirrorLines, before.Own), (await ReversalsAsync(r), again.MirrorLines, again.Own));
    }

    [Fact]
    public async Task C15_an_advance_allocation_begun_before_a_transfer_and_written_after_it_then_the_receipt_reversed()
    {
        var holder = await w.ClientAsync();
        var (a, r) = await OverpaidAsync(holder);
        var b = await PolicyAsync(holder, pending: true, premium: 60m, schedule: [(0, 60m)]);
        var c = await PolicyAsync(holder, pending: true);
        string? transfer = null;
        await using var overlap = new Overlap(db);
        await using var hold = await overlap.HoldAsync();
        await hold.Run(LockPolicy, new { id = c.Id });
        // While the allocation waits on C, the transfer to B (needs 60 of the 100) runs to its end: 40 is left unallocated.
        var o = await overlap.RunAsync(hold, new HashSet<int>(), [() => Allocate(c.Id, r)], async _ => transfer = Outcome(await Transfer(b.Id, r)));
        Assert.NotEqual(0, o.Sessions[0]);
        Assert.Equal(("transferred", "allocated"), (transfer, Outcome(o.Results[0])));
        Assert.Equal(40m, (await HoldingAsync(c.Id, r)).Amount);
        // SQL Server stamps created_at at each statement (database/README.md, difference 2), not at the transaction's start:
        // the allocation's lines come after the transfer's here, so PostgreSQL's mis-order by created_at does not arise; the
        // reversal over this same history is what is checked (the ordering search itself: Receivable.OrderMirrorLines tests).
        var before = await ReversalAsync(r);
        var lines = (await ReceiptAsync(r)).Lines;
        Assert.Null(await Reverse(r, a.Id));
        var after = await ReversalAsync(r);
        Assert.Equal((-365m, lines, true, before.Own), (after.HeaderGel, after.MirrorLines, after.AllZero, after.Own));
    }

    [Fact]
    public async Task C16_H60_plus_100_A_minus_80_A_plus_80_B_minus_60_B_plus_20_C_reversed_then_again()
    {
        var holder = await w.ClientAsync();
        var a = await PolicyAsync(holder, premium: 100m, schedule: [(0, 100m)]);
        var r = await PayAsync(a.Id, "100.00", Start.AddDays(4));
        Assert.Equal("done", await ReduceAsync(a.Id, "-80", Start.AddDays(5)));
        var b = await PolicyAsync(holder, premium: 80m, schedule: [(0, 80m)]);
        Assert.Equal("transferred", Outcome(await Transfer(b.Id, r)));
        Assert.Equal("done", await ReduceAsync(b.Id, "-60", Start.AddDays(6)));
        var c = await PolicyAsync(holder, pending: true, premium: 20m, schedule: [(0, 20m)]);
        Assert.Equal("transferred", Outcome(await Transfer(c.Id, r)));
        Assert.Equal(5, (await ReceiptAsync(r)).Lines);
        Assert.Equal(40m, await LeftAsync(r));
        var before = await ReversalAsync(r);
        Assert.Null(await Reverse(r, a.Id));
        var after = await ReversalAsync(r);
        Assert.Equal((-100m, 5, -60m, true, before.Own), (after.HeaderGel, after.MirrorLines, after.MirrorGel, after.AllZero, after.Own));
        Assert.Null(await Reverse(r, a.Id, "შებრუნება 2"));
        Assert.Equal((1, 5), (await ReversalsAsync(r), (await ReversalAsync(r)).MirrorLines));
    }

    [Fact]
    public async Task C17_H61_nineteen_lines_many_of_one_amount_reversed_then_again()
    {
        var holder = await w.ClientAsync();
        var a = await PolicyAsync(holder, premium: 100m, schedule: [(0, 100m)]);
        var r = await PayAsync(a.Id, "100.00", Start.AddDays(4));
        Assert.Equal("done", await ReduceAsync(a.Id, "-80", Start.AddDays(5)));
        var b = await PolicyAsync(holder, premium: 80m, schedule: [(0, 80m)]);
        Assert.Equal("transferred", Outcome(await Transfer(b.Id, r)));
        Assert.Equal("done", await ReduceAsync(b.Id, "-60", Start.AddDays(6)));
        var c = await PolicyAsync(holder, premium: 20m, schedule: [(0, 20m)]);
        Assert.Equal("transferred", Outcome(await Transfer(c.Id, r)));
        var targets = new List<World.Policy>();
        for (var k = 1; k <= 7; k++)
        {
            Assert.Equal("done", await ReduceAsync(c.Id, "-1", Start.AddDays(6 + k)));
            var t = await PolicyAsync(holder, pending: true, premium: 1m, schedule: [(0, 1m)]);
            targets.Add(t);
            Assert.Equal("transferred", Outcome(await Transfer(t.Id, r)));
        }
        Assert.Equal(19, (await ReceiptAsync(r)).Lines);
        Assert.Equal(40m, await LeftAsync(r));
        var ids = new[] { a.Id, b.Id, c.Id }.Concat(targets.Select(t => t.Id)).ToList();
        async Task<string> SchedulesAsync() => JsonSerializer.Serialize(await w.QueryAsync<(Guid, decimal, string)>("""
            select i.id, i.amount, coalesce((select string_agg(concat(j.id, ':', j.change, ':', j.booked_on), ',') within group (order by j.id) from core.installment_adjustment j where j.installment_id = i.id), '')
            from core.installment i where i.policy_id in @ids order by i.id
            """, new { ids }));
        var schedules = await SchedulesAsync();
        var before = await ReversalAsync(r);
        Assert.Null(await Reverse(r, a.Id));
        var after = await ReversalAsync(r);
        Assert.Equal((-100m, 19, -60m, 10, true, before.Own), (after.HeaderGel, after.MirrorLines, after.MirrorGel, after.Instalments, after.AllZero, after.Own));
        Assert.Equal(schedules, await SchedulesAsync());
        Assert.Null(await Reverse(r, a.Id, "შებრუნება 2"));
        Assert.Equal((1, 19), (await ReversalsAsync(r), (await ReversalAsync(r)).MirrorLines));
    }
}
