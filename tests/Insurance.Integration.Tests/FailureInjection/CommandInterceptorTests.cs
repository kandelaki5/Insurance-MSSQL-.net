using System.Data;
using Dapper;
using Insurance.Application.Data;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>
/// The test seam itself (DbOptions.Interceptor, src/Insurance.Application/Data/CommandInterceptor.cs) on the real SQL Server:
/// it sees every command of its own Db with the transaction it runs in, a thrown error comes out of the call unchanged and the
/// transaction keeps nothing, an action runs before the command and the command sees it, and another Db on the same database
/// is never touched.
/// </summary>
public sealed class CommandInterceptorTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private Db With(Faults faults) => new(new DbOptions { ConnectionString = db.AppConnection, Interceptor = faults });

    private async Task<int> GroupsNamedAsync(string name)
    {
        await using var c = await db.OpenAppAsync();
        return await c.ExecuteScalarAsync<int>("select count(*) from core.user_group where name = @name", new { name });
    }

    [Fact]
    public async Task Every_command_is_seen_with_its_transaction_reads_outside_snapshot_and_the_users_transaction()
    {
        var faults = new Faults();
        var d = With(faults);
        await d.ReadAsync(c => c.ExecuteScalarAsync<int>("select 1 /*plain*/"));
        await d.ReadTxAsync(tx => tx.ExecuteScalarAsync<int>("select 2 /*readtx*/"));
        await d.SnapshotAsync(tx => tx.ExecuteScalarAsync<int>("select 3 /*snapshot*/"));
        var user = await Fixtures.UserAsync(db, "viewer");
        await d.AsUserAsync(user, tx => tx.ExecuteScalarAsync<int>("select 4 /*asuser*/"));
        await d.AsUserAsync(user, tx => tx.Connection.ExecuteScalarAsync<int>("select 5 /*raw*/", transaction: tx.Transaction));
        var log = faults.Log;
        Assert.Contains(("select 1 /*plain*/", (IsolationLevel?)null), log.Select(c => (c.Sql, (IsolationLevel?)c.Isolation)));
        Assert.Contains(("select 2 /*readtx*/", (IsolationLevel?)null), log.Select(c => (c.Sql, (IsolationLevel?)c.Isolation)));
        Assert.Contains(("select 3 /*snapshot*/", (IsolationLevel?)IsolationLevel.Snapshot), log.Select(c => (c.Sql, (IsolationLevel?)c.Isolation)));
        Assert.Contains(("select 4 /*asuser*/", (IsolationLevel?)IsolationLevel.ReadCommitted), log.Select(c => (c.Sql, (IsolationLevel?)c.Isolation)));
        Assert.Contains(("select 5 /*raw*/", (IsolationLevel?)IsolationLevel.ReadCommitted), log.Select(c => (c.Sql, (IsolationLevel?)c.Isolation)));
        // The transaction's own set-up is a command like any other: the user's transaction starts with these two.
        var four = log.ToList().FindIndex(c => c.Sql == "select 4 /*asuser*/");
        Assert.Equal(["set xact_abort on", "exec sp_set_session_context N'core.user_id', @id"], log.Skip(four - 2).Take(2).Select(c => c.Sql));
    }

    [Fact]
    public async Task A_thrown_error_comes_out_unchanged_the_transaction_keeps_nothing_and_the_retry_succeeds()
    {
        var faults = new Faults();
        var d = With(faults);
        var user = await Fixtures.UserAsync(db, "admin");
        var name = "ჯგუფი " + Guid.NewGuid().ToString("N")[..8];
        var sentinel = new InjectedFault("SEAM_SENTINEL");
        faults.FailOnce(c => c.Sql.Contains("/*after*/", StringComparison.Ordinal), sentinel);
        Task<int> Work() => d.AsUserAsync(user, async tx =>
        {
            await tx.ExecuteAsync("insert core.user_group (id, name) values (newid(), @name)", new { name });
            return await tx.ExecuteScalarAsync<int>("select count(*) /*after*/ from core.user_group where name = @name", new { name });
        });
        var thrown = await Assert.ThrowsAsync<InjectedFault>(Work);
        Assert.Same(sentinel, thrown);
        Assert.Equal(0, await GroupsNamedAsync(name));
        Assert.True(faults.AllFired);
        Assert.Equal(1, await Work());
        Assert.Equal(1, await GroupsNamedAsync(name));
    }

    [Fact]
    public async Task An_action_runs_before_the_command_and_the_command_sees_what_it_committed()
    {
        var faults = new Faults();
        var d = With(faults);
        var name = "ჯგუფი " + Guid.NewGuid().ToString("N")[..8];
        faults.BeforeOnce(c => c.Sql.Contains("/*count*/", StringComparison.Ordinal), () =>
        {
            using var other = new Microsoft.Data.SqlClient.SqlConnection(db.AppConnection);
            other.Open();
            other.Execute("insert core.user_group (id, name) values (newid(), @name)", new { name });
        });
        var user = await Fixtures.UserAsync(db, "admin");
        Assert.Equal(1, await d.AsUserAsync(user, tx => tx.ExecuteScalarAsync<int>("select count(*) /*count*/ from core.user_group where name = @name", new { name })));
        Assert.True(faults.AllFired);
    }

    [Fact]
    public async Task Another_Db_on_the_same_database_is_never_intercepted_and_a_rule_fires_once()
    {
        var faults = new Faults();
        faults.FailOnce(c => c.Sql.Contains("/*x*/", StringComparison.Ordinal), new InjectedFault("ONCE"));
        var plain = new Db(new DbOptions { ConnectionString = db.AppConnection });
        Assert.Equal(7, await plain.ReadAsync(c => c.ExecuteScalarAsync<int>("select 7 /*x*/")));
        Assert.Empty(faults.Log);
        var d = With(faults);
        await Assert.ThrowsAsync<InjectedFault>(() => d.ReadAsync(c => c.ExecuteScalarAsync<int>("select 7 /*x*/")));
        Assert.Equal(7, await d.ReadAsync(c => c.ExecuteScalarAsync<int>("select 7 /*x*/")));
        Assert.Equal(2, faults.Log.Count(c => c.Sql == "select 7 /*x*/"));
    }
}
