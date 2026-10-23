using System.Data;
using Dapper;
using Insurance.Application.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// Helpers of the ports of the legacy real-database check scripts (scripts/*-db-check.ts, scripts/regress-db/*, verify-00xx.ts,
/// number-*-check.ts, db-acceptance.ts): a transaction of the application role held open by the test, so that two real sessions
/// can be put in a forced interleaving, and the refusal code of a statement as the legacy scripts recorded it.
/// </summary>
public static class LegacyDbCheck
{
    /// <summary>How long a statement must stay blocked to count as waiting (the legacy scripts held for 1.5 s and allowed 200 ms).</summary>
    public static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(800);

    /// <summary>A transaction of the application role, held open by the test, attributed to <c>actor</c>.</summary>
    public sealed class Held : IAsyncDisposable
    {
        public required SqlConnection Connection { get; init; }
        public required SqlTransaction Transaction { get; init; }
        public Tx Tx => new(Connection, Transaction);
        public Task<int> Run(string sql, object? param = null) => Connection.ExecuteAsync(sql, param, Transaction);
        public Task<T?> Scalar<T>(string sql, object? param = null) => Connection.ExecuteScalarAsync<T>(sql, param, Transaction);
        public Task CommitAsync() => Transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            try { if (Transaction.Connection is not null) await Transaction.RollbackAsync(); } catch (InvalidOperationException) { } catch (SqlException) { }
            await Connection.DisposeAsync();
        }
    }

    public static async Task<Held> HoldAsync(TestDatabase db, Guid? actor, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        var c = await db.OpenAppAsync();
        var t = (SqlTransaction)await c.BeginTransactionAsync(isolation);
        await c.ExecuteAsync("set xact_abort on", transaction: t);
        if (actor is { } id) await c.ExecuteAsync("exec sp_set_session_context N'core.user_id', @id", new { id = id.ToString() }, t);
        return new Held { Connection = c, Transaction = t };
    }

    /// <summary>Each task is still running after <see cref="Blocked"/>: it waits for a lock someone else holds.</summary>
    public static async Task AssertWaitingAsync(params Task[] tasks)
    {
        foreach (var task in tasks) Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(Blocked)));
    }

    /// <summary>The task finishes without waiting for anyone (within 10 s).</summary>
    public static async Task AssertNotWaitingAsync(Task task) =>
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));

    /// <summary>"ok", or the legacy code of the refusal: a guard's code, 23505 / 23514 / 23503, or "42501" when the role lacks the right.</summary>
    public static async Task<string> CodeOf(Func<Task> run)
    {
        try
        {
            await run();
            return "ok";
        }
        catch (Exception e)
        {
            for (var x = e; x is not null; x = x.InnerException)
                if (x is SqlException sql && sql.Errors.Cast<SqlError>().Any(r => r.Number is 229 or 230 or 262 or 3704))
                    return "42501";
            return DbError.From(e) is { } d ? d.Constraint is { } name ? $"{d.Code} {name}" : d.Code : e.Message;
        }
    }

    /// <summary>The code alone, without the constraint's name.</summary>
    public static async Task<string> BareCodeOf(Func<Task> run) => (await CodeOf(run)).Split(' ')[0];
}
