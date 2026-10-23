using System.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Application.Data;

/// <summary>
/// Opens connections and runs units of work. A write runs in one transaction attributed to a user, so the
/// audit triggers and the created_by defaults know the actor (legacy asUser: set_config('core.user_id', …, true)).
/// The database runs READ COMMITTED with row versioning (READ_COMMITTED_SNAPSHOT ON), which is PostgreSQL's
/// read committed: a statement sees the data committed when it started and never waits for readers.
/// </summary>
public sealed class Db(DbOptions options)
{
    static Db() => DapperSetup.Register();

    public string ConnectionString => options.ConnectionString;

    /// <summary>The clock of this Db's options: the system clock in production, a test's own clock in a test.</summary>
    public TimeProvider Clock => options.Clock;

    /// <summary>The current instant by this Db's clock.</summary>
    public DateTimeOffset Now() => options.Clock.GetUtcNow();

    /// <summary>Today's date in Tbilisi by this Db's clock (legacy todayInTbilisi()).</summary>
    public DateOnly Today() => TbilisiClock.Day(Now());

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(options.ConnectionString);
        if (options.Interceptor is { } interceptor) CommandInterception.Attach(connection, interceptor);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>Reads outside a transaction (one statement or several independent ones).</summary>
    public async Task<T> ReadAsync<T>(Func<SqlConnection, Task<T>> work, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await work(connection);
    }

    /// <summary>Reads through a <see cref="Tx"/> without a transaction, so read helpers serve pages and transactions alike.</summary>
    public async Task<T> ReadTxAsync<T>(Func<Tx, Task<T>> work, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await work(new Tx(connection, null, options.Clock));
    }

    /// <summary>Runs <paramref name="work"/> in one transaction attributed to <paramref name="userId"/>; commits when it returns, rolls back when it throws.</summary>
    public Task<T> AsUserAsync<T>(Guid userId, Func<Tx, Task<T>> work, CancellationToken ct = default) =>
        InTransactionAsync(userId, work, ct);

    /// <summary>Reads in one snapshot-isolation transaction: several statements see one committed state, nothing waits.</summary>
    public async Task<T> SnapshotAsync<T>(Func<Tx, Task<T>> work, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Snapshot, ct);
        var result = await work(new Tx(connection, transaction, options.Clock));
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>A transaction without an actor (system work such as login bookkeeping or rate loading).</summary>
    public Task<T> InTransactionAsync<T>(Func<Tx, Task<T>> work, CancellationToken ct = default) =>
        InTransactionAsync(null, work, ct);

    private async Task<T> InTransactionAsync<T>(Guid? userId, Func<Tx, Task<T>> work, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var tx = new Tx(connection, transaction, options.Clock);
        try
        {
            // XACT_ABORT: any error rolls the whole transaction back, as in PostgreSQL where an error aborts it.
            await tx.ExecuteAsync("set xact_abort on");
            if (userId is { } id)
                await tx.ExecuteAsync("exec sp_set_session_context N'core.user_id', @id", new { id = id.ToString() });
            var result = await work(tx);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { } catch (SqlException) { }
            }
            throw;
        }
        finally
        {
            if (userId is not null && connection.State == ConnectionState.Open)
            {
                try
                {
                    await using var clear = new SqlCommand("exec sp_set_session_context N'core.user_id', null", connection);
                    await clear.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (SqlException) { }
            }
        }
    }
}
