using Dapper;
using Insurance.Application.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// What legacy scripts/run-cleanup-check.ts and scripts/parallel-users-probe.ts (Codex H34–H37) proved for the regressions
/// that ran on the shared insurance_test — a run cleans up only what it created, never a parallel run's, and leaves nothing
/// that can sign in — holds here by construction: every test class makes its own ins_it_* database and application login and
/// drops both afterwards. These checks prove that construction on the real server: the cleanup refuses a database that is
/// not a test database, and dropping one run's database and login leaves a parallel run's database, rows and login as they were.
/// </summary>
public sealed class TestDatabaseIsolationTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    [Theory]
    [InlineData("insurance_test")]
    [InlineData("master")]
    [InlineData("ins_test_x")]
    public async Task The_cleanup_refuses_a_database_that_is_not_a_test_database(string name)
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseAdmin.DropTestDatabaseAsync(db.MasterConnection, name, TestDatabase.Prefix, []));
        Assert.Contains("not a test database", e.Message);
    }

    [Fact]
    public async Task Dropping_one_runs_database_and_login_leaves_a_parallel_runs_database_rows_and_login_as_they_were()
    {
        // This class's database stands for the parallel run: a fictional user with a live session.
        var probe = await Fixtures.UserAsync(db, "viewer");
        await using (var c = await db.OpenAppAsync())
            await c.ExecuteAsync("insert core.session (token_hash, user_id, expires_at) values (@hash, @probe, dateadd(hour, 3, sysdatetimeoffset()))",
                new { hash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()), probe });
        var other = new TestDatabase();
        await other.InitializeAsync();
        await using (var c = await other.OpenAppAsync()) Assert.Equal(other.Name, await c.ExecuteScalarAsync<string>("select db_name()"));
        await other.DisposeAsync();

        await using var master = new SqlConnection(db.MasterConnection);
        await master.OpenAsync();
        Assert.Null(await master.ExecuteScalarAsync<int?>("select database_id from sys.databases where name = @name", new { name = other.Name }));
        Assert.Null(await master.ExecuteScalarAsync<int?>("select principal_id from sys.server_principals where name = @name", new { name = other.AppLogin }));
        Assert.NotNull(await master.ExecuteScalarAsync<int?>("select database_id from sys.databases where name = @name", new { name = db.Name }));
        await using var mine = await db.OpenAppAsync();
        Assert.Equal((true, 1), await mine.QuerySingleAsync<(bool, int)>("""
            select u.is_active, (select count(*) from core.session s where s.user_id = u.id and s.expires_at > sysdatetimeoffset()) from core.app_user u where u.id = @probe
            """, new { probe }));
    }
}
