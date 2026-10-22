using System.Data;
using Dapper;
using Insurance.Application.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// Two real application sessions in a forced interleaving (legacy scripts/db-acceptance.ts, docs/04 წესი 19): the
/// second session must wait for the first and then see its committed row. Each race also has a negative control
/// that shows what happens without the lock.
/// </summary>
public sealed class ConcurrencyTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(800);

    private sealed class Session : IAsyncDisposable
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

    private async Task<Session> BeginAsync(Guid actor)
    {
        var c = await db.OpenAppAsync();
        var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await c.ExecuteAsync("set xact_abort on; exec sp_set_session_context N'core.user_id', @id", new { id = actor.ToString() }, t);
        return new Session { Connection = c, Transaction = t };
    }

    private static async Task AssertWaitingAsync(Task task) =>
        Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(Blocked)));

    private const string CloseMonth = """
        exec core.xact_lock N'core.accounting_period';
        insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @actor);
        """;

    [Fact]
    public async Task Month_close_waits_for_a_movement_in_flight_and_then_closes()
    {
        var actor = await Fixtures.UserAsync(db);
        var month = new DateOnly(2032, 5, 1);
        await using var movement = await BeginAsync(actor);
        await movement.Run("insert core.payment (received_on, amount_gel, method) values ('2032-05-10', 50, N'cash')");

        await using var close = await BeginAsync(actor);
        var closing = close.Run(CloseMonth, new { month, actor });
        await AssertWaitingAsync(closing);

        await movement.Transaction.CommitAsync();
        await closing;
        await close.Transaction.CommitAsync();

        await using var c = await db.OpenAppAsync();
        Assert.Equal("closed", await c.ExecuteScalarAsync<string>("select status from core.accounting_period where period_start = @month", new { month }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.payment where received_on = '2032-05-10'"));
    }

    [Fact]
    public async Task Movement_waits_for_a_month_close_in_flight_and_is_then_refused()
    {
        var actor = await Fixtures.UserAsync(db);
        var month = new DateOnly(2032, 6, 1);
        await using var close = await BeginAsync(actor);
        await close.Run(CloseMonth, new { month, actor });

        await using var movement = await BeginAsync(actor);
        var moving = movement.Run("insert core.payment (received_on, amount_gel, method) values ('2032-06-10', 50, N'cash')");
        await AssertWaitingAsync(moving);

        await close.Transaction.CommitAsync();
        var e = await Assert.ThrowsAsync<SqlException>(() => moving);
        Assert.Equal(DbError.CheckViolation, DbError.FromSql(e)!.Code);
        await using var c = await db.OpenAppAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.payment where received_on = '2032-06-10'"));
    }

    [Fact]
    public async Task Negative_control_without_the_close_lock_a_movement_lands_in_a_closed_month()
    {
        var actor = await Fixtures.UserAsync(db);
        var month = new DateOnly(2032, 7, 1);
        await using var movement = await BeginAsync(actor);
        await movement.Run("insert core.payment (received_on, amount_gel, method) values ('2032-07-10', 50, N'cash')");

        // A close that skips core.xact_lock is not held up by the movement in flight...
        await using var close = await BeginAsync(actor);
        var closing = close.Run("insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @actor)", new { month, actor });
        Assert.Same(closing, await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(10))));
        await closing;
        await close.Transaction.CommitAsync();
        await movement.Transaction.CommitAsync();

        // ...so the month is closed with a movement that its close never saw: the race the lock prevents.
        await using var c = await db.OpenAppAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.payment where received_on = '2032-07-10'"));
        Assert.Equal("closed", await c.ExecuteScalarAsync<string>("select status from core.accounting_period where period_start = @month", new { month }));
    }

    [Fact]
    public async Task Two_allocations_of_one_receipt_never_exceed_it()
    {
        var actor = await Fixtures.UserAsync(db);
        var (policy, receipt) = await db.App.AsUserAsync(actor, async tx =>
        {
            var p = await Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(2032, 8, 1));
            return (p, await Fixtures.ReceiptAsync(tx, new DateOnly(2032, 8, 2), 100m, p.PartyId));
        });
        const string allocate = "insert core.payment_allocation (payment_id, installment_id, amount, amount_gel, rate, booked_on) values (@receipt, @installment, 70, 70, 1, '2032-08-02')";
        var args = new { receipt, installment = policy.InstallmentId };

        await using var first = await BeginAsync(actor);
        await first.Run(allocate, args);
        await using var second = await BeginAsync(actor);
        var secondRun = second.Run(allocate, args);
        await AssertWaitingAsync(secondRun);
        await first.Transaction.CommitAsync();

        var e = await Assert.ThrowsAsync<SqlException>(() => secondRun);
        Assert.Equal("PA002", DbError.FromSql(e)!.Code);
        await using var c = await db.OpenAppAsync();
        Assert.Equal(70m, await c.ExecuteScalarAsync<decimal>("select sum(amount_gel) from core.payment_allocation where payment_id = @receipt", new { receipt }));
    }

    [Fact]
    public async Task Two_placements_on_one_premium_row_never_exceed_it()
    {
        var actor = await Fixtures.UserAsync(db);
        var (row, treatyA, treatyB) = await db.App.AsUserAsync(actor, async tx =>
        {
            var p = await Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(2032, 9, 1));
            var reinsurer = await Fixtures.PersonAsync(tx);
            var row = await tx.ExecuteScalarAsync<Guid>("select id from core.premium_transaction where policy_line_id = @line", new { line = p.LineId });
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            await tx.ExecuteAsync("""
                insert core.ri_treaty (id, number, name, reinsurer_party_id, kind, period_from, period_to, currency, ceded_percent)
                values (@a, @na, N'კვოტა A', @reinsurer, N'quota_share', '2032-01-01', '2032-12-31', N'GEL', 60),
                       (@b, @nb, N'კვოტა B', @reinsurer, N'quota_share', '2032-01-01', '2032-12-31', N'GEL', 60)
                """, new { a, b, na = "RI-" + a.ToString("N")[..8], nb = "RI-" + b.ToString("N")[..8], reinsurer });
            return (row, a, b);
        });
        const string place = """
            insert core.premium_cession (premium_transaction_id, treaty_id, share_percent, amount, amount_gel, booked_on, cover_from, cover_to)
            values (@row, @treaty, 60, 720, 720, '2032-09-01', '2032-09-01', '2033-09-01')
            """;

        await using var first = await BeginAsync(actor);
        await first.Run(place, new { row, treaty = treatyA });
        await using var second = await BeginAsync(actor);
        var secondRun = second.Run(place, new { row, treaty = treatyB });
        await AssertWaitingAsync(secondRun);
        await first.Transaction.CommitAsync();

        var e = await Assert.ThrowsAsync<SqlException>(() => secondRun);
        Assert.True(DbError.FromSql(e)?.Code == "RI001", $"{e.Number}: {e.Message}");
        await using var c = await db.OpenAppAsync();
        Assert.Equal(60m, await c.ExecuteScalarAsync<decimal>("select sum(share_percent) from core.premium_cession where premium_transaction_id = @row", new { row }));
    }
}
