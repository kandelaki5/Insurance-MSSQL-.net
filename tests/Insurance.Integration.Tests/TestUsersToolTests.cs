using Dapper;
using Insurance.Domain.Common;
using Insurance.Tools;

namespace Insurance.Integration.Tests;

/// <summary>
/// The test-users and users-inventory commands (ported from scripts/claude-test-users-plan.test.ts and
/// scripts/reg-users-inventory.ts): the plan's rules by hand, then the real command on a fresh test database.
/// </summary>
public sealed class TestUsersToolTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private static string Key(string username) => $"K_{username}";
    private static bool Verify(string password, string hash) => hash == $"h({password})";

    [Fact]
    public void An_existing_user_whose_password_differs_here_is_reported_not_overwritten()
    {
        var n = 0;
        var plan = TestUsers.PlanTestUsers(["a"], Key, new Dictionary<string, string> { ["K_a"] = "mine" }, new Dictionary<string, string> { ["a"] = "h(theirs)" }, Verify, () => $"new{++n}");
        Assert.Empty(plan.Create);
        Assert.Empty(plan.Generated);
        Assert.Single(plan.Problems);
    }

    [Fact]
    public void An_existing_user_without_a_local_password_is_reported_and_no_password_is_generated()
    {
        var plan = TestUsers.PlanTestUsers(["a"], Key, new Dictionary<string, string>(), new Dictionary<string, string> { ["a"] = "h(theirs)" }, Verify, () => "x");
        Assert.Empty(plan.Generated);
        Assert.Single(plan.Problems);
    }

    [Fact]
    public void Matching_users_are_left_alone_missing_ones_are_created_with_a_password_only_if_none_is_stored()
    {
        var plan = TestUsers.PlanTestUsers(["a", "b", "c"], Key, new Dictionary<string, string> { ["K_a"] = "pa", ["K_b"] = "pb" },
            new Dictionary<string, string> { ["a"] = "h(pa)" }, Verify, () => "new");
        Assert.Empty(plan.Problems);
        Assert.Equal(["b", "c"], plan.Create);
        Assert.Equal(["K_c"], plan.Generated.Keys);
    }

    [Fact]
    public void A_new_password_appended_to_a_file_without_a_trailing_newline_does_not_merge_into_the_last_value()
    {
        const string current = "CLAUDE_ADMIN_PASSWORD=old";
        var after = current + TestUsers.EnvAppendText(current, new Dictionary<string, string> { ["CLAUDE_VIEWER_PASSWORD"] = "new" }, "# h\n");
        Assert.Equal([new("CLAUDE_ADMIN_PASSWORD", "old"), new KeyValuePair<string, string>("CLAUDE_VIEWER_PASSWORD", "new")], TestUsers.ParseEnv(after));
        Assert.Equal("B=2\n", TestUsers.EnvAppendText("A=1\n", new Dictionary<string, string> { ["B"] = "2" }, "# h\n"));
        Assert.Equal("B=2\n", TestUsers.EnvAppendText("", new Dictionary<string, string> { ["B"] = "2" }, "# h\n"));
        Assert.Equal("# h\nB=2\n", TestUsers.EnvAppendText(null, new Dictionary<string, string> { ["B"] = "2" }, "# h\n"));
    }

    [Fact]
    public async Task The_command_creates_the_users_once_never_changes_an_existing_password_and_stops_on_a_mismatch()
    {
        var env = Path.Combine(Path.GetTempPath(), $"env-uat-{Guid.NewGuid():N}");
        try
        {
            var log = new List<string>();
            await TestUsers.RunAsync(db.AdminConnection, env, log.Add);
            var stored = TestUsers.ParseEnv(await File.ReadAllTextAsync(env));
            Assert.Equal(5, stored.Count(p => p.Key.EndsWith("_PASSWORD", StringComparison.Ordinal)));
            await using var c = await db.OpenAdminAsync();
            var users = (await c.QueryAsync<(string Username, string Role, string Hash)>("select username, role_code, password_hash from core.app_user where username like N'claude.%' order by username")).ToList();
            Assert.Equal(["claude.accountant", "claude.admin", "claude.head", "claude.operator", "claude.viewer"], users.Select(u => u.Username));
            Assert.All(users, u => Assert.True(Password.VerifyPassword(stored[TestUsers.Key(u.Username)], u.Hash)));
            Assert.DoesNotContain(log, line => stored.Values.Any(line.Contains));

            // Again: nothing new, the passwords stay; a role changed meanwhile is put back.
            await c.ExecuteAsync("update core.app_user set role_code = N'viewer', is_active = 0 where username = N'claude.head'");
            await TestUsers.RunAsync(db.AdminConnection, env, _ => { });
            Assert.Equal(stored, TestUsers.ParseEnv(await File.ReadAllTextAsync(env)));
            Assert.Equal(users.Select(u => u.Hash), (await c.QueryAsync<string>("select password_hash from core.app_user where username like N'claude.%' order by username")));
            Assert.Equal(("operations_head", true), await c.QuerySingleAsync<(string, bool)>("select role_code, is_active from core.app_user where username = N'claude.head'"));

            // A different local password for an existing user: nothing is written.
            await File.WriteAllTextAsync(env, (await File.ReadAllTextAsync(env)).Replace($"CLAUDE_ADMIN_PASSWORD={stored["CLAUDE_ADMIN_PASSWORD"]}", "CLAUDE_ADMIN_PASSWORD=different", StringComparison.Ordinal));
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => TestUsers.RunAsync(db.AdminConnection, env, _ => { }));
            Assert.StartsWith("nothing written", e.Message);
            Assert.Equal(users[1].Hash, await c.ExecuteScalarAsync<string>("select password_hash from core.app_user where username = N'claude.admin'"));
        }
        finally
        {
            File.Delete(env);
        }
        Assert.False(TestUsers.IsTestDatabase("insurance"));
        Assert.True(TestUsers.IsTestDatabase("insurance_test"));
    }

    [Fact]
    public async Task The_inventory_lists_the_prefixed_users_with_their_sessions_and_what_points_at_them_and_writes_nothing()
    {
        await using var c = await db.OpenAdminAsync();
        var used = Guid.NewGuid();
        var unused = Guid.NewGuid();
        await c.ExecuteAsync("""
            insert core.app_user (id, username, full_name, password_hash, role_code) values (@used, N'reg-inv-used', N'რეგრესია', N'x', N'admin'), (@unused, N'reg-inv-unused', N'რეგრესია', N'x', N'viewer');
            insert core.session (token_hash, user_id, expires_at) values (N'live-inv', @used, dateadd(hour, 1, sysdatetimeoffset())), (N'old-inv', @used, dateadd(hour, -1, sysdatetimeoffset()));
            exec sp_set_session_context N'core.user_id', @used;
            insert core.party (kind, first_name, last_name, tax_id) values (N'person', N'ა', N'ბ', N'98765432101');
            exec sp_set_session_context N'core.user_id', null;
            insert core.complaint (number, received_on, channel, customer_name, category, description, created_by) values (N'PR-INV-1', '2026-01-01', N'written', N'x', 1, N'y', @used);
            """, new { used, unused });
        var audit = await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log");
        var rows = await UsersInventory.ReadAsync(db.AdminConnection, "reg-inv-");
        Assert.Equal(["reg-inv-used", "reg-inv-unused"], rows.Select(r => r.Username).Order(StringComparer.Ordinal).Reverse());
        var u = rows.Single(r => r.Id == used);
        Assert.Equal((1, 1, "admin", true), (u.LiveSessions, u.ExpiredSessions, u.Role, u.Active));
        Assert.Contains("complaint.created_by 1", u.Links);
        Assert.Contains(u.Links, l => l.StartsWith("audit_log.user_id ", StringComparison.Ordinal));
        Assert.Empty(rows.Single(r => r.Id == unused).Links);
        Assert.Equal(audit, await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log"));
        var printed = new List<string>();
        UsersInventory.Print(rows, printed.Add);
        Assert.Contains(printed, l => l.StartsWith("SUMMARY users 2, active 2, with live sessions 1, referenced 1, unreferenced 1", StringComparison.Ordinal));
    }
}
