using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.Policies;

/// <summary>
/// Forced overlaps on real SQL Server sessions (the legacy scripts' overlap() of client-credit-regression.ts and the races of
/// race-engine.ts / app-race.ts): a holder transaction on its own connection takes a lock; actions are started one by one and
/// each is SEEN waiting in the holder's lock chain by a monitor connection (sys.dm_exec_requests.blocking_session_id) — never
/// assumed from a sleep. An action's own session is the waiter that appears in the chain after it is started and was not
/// there before (a waiter of another connection that was already waiting — a "foreign" one — is never taken for it). Only an
/// action's own proven session is ever killed. The holder and the monitor are the test database's administrative login,
/// as the legacy scripts held their locks on the admin connection.
/// </summary>
public sealed class Overlap(TestDatabase db) : IAsyncDisposable
{
    public sealed record Waiter(int Session, int BlockedBy, string WaitType);

    private SqlConnection? monitor;

    public sealed class Holder : IAsyncDisposable
    {
        public required SqlConnection Connection { get; init; }
        public required SqlTransaction Transaction { get; init; }
        public required int Session { get; init; }
        public Task<int> Run(string sql, object? param = null) => Connection.ExecuteAsync(sql, param, Transaction);
        public Task<IEnumerable<T>> Query<T>(string sql, object? param = null) => Connection.QueryAsync<T>(sql, param, Transaction);
        public async Task CommitAsync() { if (Transaction.Connection is not null) await Transaction.CommitAsync(); }
        public async ValueTask DisposeAsync()
        {
            try { if (Transaction.Connection is not null) await Transaction.RollbackAsync(); } catch (InvalidOperationException) { }
            await Connection.DisposeAsync();
        }
    }

    /// <summary>A transaction on a connection of its own; <paramref name="app"/>: as core_app (with the actor set), else as the administrative login.</summary>
    public async Task<Holder> HoldAsync(bool app = false, Guid? actor = null)
    {
        var c = app ? await db.OpenAppAsync() : await db.OpenAdminAsync();
        var session = await c.ExecuteScalarAsync<int>("select @@spid");
        var t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await c.ExecuteAsync("set xact_abort on", transaction: t);
        if (actor is { } id) await c.ExecuteAsync("exec sp_set_session_context N'core.user_id', @id", new { id = id.ToString() }, t);
        return new Holder { Connection = c, Transaction = t, Session = session };
    }

    private async Task<SqlConnection> MonitorAsync()
    {
        monitor ??= await db.OpenAdminAsync();
        return monitor;
    }

    /// <summary>Every session of this database waiting on a lock, and on whom.</summary>
    public async Task<IReadOnlyList<Waiter>> WaitersAsync() =>
        (await (await MonitorAsync()).QueryAsync<Waiter>("""
            select convert(int, r.session_id) as Session, convert(int, r.blocking_session_id) as BlockedBy, coalesce(r.wait_type, N'') as WaitType
            from sys.dm_exec_requests r where r.database_id = db_id() and r.blocking_session_id <> 0 and r.session_id <> @@spid
            """)).ToList();

    /// <summary>The sessions waiting, directly or behind one another, on <paramref name="holder"/>.</summary>
    public async Task<IReadOnlySet<int>> ChainAsync(int holder)
    {
        var rows = await WaitersAsync();
        var chain = new HashSet<int> { holder };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var r in rows)
                if (!chain.Contains(r.Session) && chain.Contains(r.BlockedBy)) { chain.Add(r.Session); grew = true; }
        }
        chain.Remove(holder);
        return chain;
    }

    /// <summary>Waits (≤ 20 s) until a session not in <paramref name="known"/> waits in the holder's chain; its id, or 0 when none was seen.</summary>
    public async Task<int> NewWaiterAsync(int holder, IReadOnlySet<int> known, Task started)
    {
        for (var n = 0; n < 200; n++)
        {
            var now = await ChainAsync(holder);
            var fresh = now.Where(s => !known.Contains(s)).ToList();
            if (fresh.Count == 1) return fresh[0];
            if (started.IsCompleted) return 0;
            await Task.Delay(100);
        }
        return 0;
    }

    /// <summary>
    /// Starts the actions one by one while the holder holds; each must be seen waiting on its own new session (the sessions,
    /// in start order; 0 = not seen). Then <paramref name="during"/> runs (the holder still holding), the holder commits, and
    /// every action is awaited (≤ 40 s each). <paramref name="commit"/> false rolls the holder back (a held row change, undone).
    /// </summary>
    public async Task<(IReadOnlyList<int> Sessions, IReadOnlyList<T?> Results, IReadOnlyList<Exception?> Errors)> RunAsync<T>(
        Holder holder, IReadOnlySet<int> foreign, IReadOnlyList<Func<Task<T>>> starts, Func<IReadOnlyList<int>, Task>? during = null, bool commit = true)
    {
        var tasks = new List<Task<T>>();
        var sessions = new List<int>();
        var known = new HashSet<int>(foreign);
        foreach (var start in starts)
        {
            var task = start();
            tasks.Add(task);
            var s = await NewWaiterAsync(holder.Session, known, task);
            sessions.Add(s);
            if (s != 0) known.Add(s);
        }
        if (during is not null) await during(sessions);
        if (commit) await holder.CommitAsync();
        else await holder.Transaction.RollbackAsync();
        var results = new List<T?>();
        var errors = new List<Exception?>();
        foreach (var t in tasks)
        {
            if (await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(40))) != t)
            {
                results.Add(default);
                errors.Add(new TimeoutException("did not settle within 40 s"));
                continue;
            }
            try { results.Add(await t); errors.Add(null); }
            catch (Exception e) { results.Add(default); errors.Add(e); }
        }
        return (sessions, results, errors);
    }

    /// <summary>Whether a session holds granted exclusive key locks on rows of a table (rows it has written in its open transaction).</summary>
    public async Task<bool> HoldsWrittenRowsAsync(int session, string table) =>
        await (await MonitorAsync()).ExecuteScalarAsync<int>("""
            select count(*) from sys.dm_tran_locks l join sys.partitions p on p.hobt_id = l.resource_associated_entity_id
            where l.request_session_id = @session and l.resource_type = N'KEY' and l.request_mode = N'X' and l.request_status = N'GRANT'
              and p.object_id = object_id(@table)
            """, new { session, table }) > 0;

    /// <summary>Ends a session (its open transaction is rolled back): the counterpart of pg_cancel_backend inside a transaction.</summary>
    public async Task KillAsync(int session)
    {
        if (session <= 50) throw new InvalidOperationException($"refusing to kill system session {session}");
        await (await MonitorAsync()).ExecuteAsync($"kill {session}");
    }

    /// <summary>Whether <paramref name="session"/> is waiting on <paramref name="by"/> (directly).</summary>
    public async Task<bool> BlockedByAsync(int session, int by) => (await WaitersAsync()).Any(w => w.Session == session && w.BlockedBy == by);

    /// <summary>Waits (≤ <paramref name="ms"/>) until <paramref name="session"/> waits on <paramref name="by"/> or the task ends.</summary>
    public async Task<bool> SeenBlockedAsync(int session, int by, Task task, int ms = 15000)
    {
        for (var waited = 0; waited <= ms; waited += 50)
        {
            if (await BlockedByAsync(session, by)) return true;
            if (task.IsCompleted) return false;
            await Task.Delay(50);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (monitor is not null) await monitor.DisposeAsync();
    }
}
