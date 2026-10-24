using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// The permission storage on a real database (ported from scripts/integration/permission-storage.test.ts, which ran the
/// store on a model of the four tables): what is stored, read into the real evaluator; the admin's changes, one key at a
/// time, a repeat writing nothing (no audit row); the refusals writing nothing; the schema's own refusals. The legacy
/// model's statement logs and injected read faults have no counterpart on a real server and are not ported.
/// </summary>
public sealed class PermissionStorageTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private const string Key = "premium.receipt.create";

    private sealed record World(Guid Anna, Guid Beka, Guid Cara, Guid Admin, Guid Cashiers, Guid Sales, Guid Other, string Tag);

    private async Task<World> WorldAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        Guid[] users = [await Fixtures.UserAsync(db, "underwriter"), await Fixtures.UserAsync(db, "underwriter"), await Fixtures.UserAsync(db, "accountant"), await Fixtures.UserAsync(db)];
        await using var c = await db.OpenAdminAsync();
        async Task<Guid> Group(string name)
        {
            var id = Guid.NewGuid();
            await c.ExecuteAsync("insert core.user_group (id, name) values (@id, @name)", new { id, name = $"{name} {tag}" });
            return id;
        }
        return new World(users[0], users[1], users[2], users[3], await Group("სალაროები"), await Group("გაყიდვები"), await Group("სხვა"), tag);
    }

    private async Task SqlAsync(string sql, object param)
    {
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync(sql, param);
    }

    private Task Member(Guid group, Guid user) => SqlAsync("insert core.user_group_member (group_id, user_id) values (@group, @user)", new { group, user });
    private Task Grant(Guid group, string permission = Key) => SqlAsync("insert core.user_group_permission (group_id, permission) values (@group, @permission)", new { group, permission });
    private Task Decide(Guid user, string decision, string permission = Key) => SqlAsync("insert core.user_permission (user_id, permission, decision) values (@user, @permission, @decision)", new { user, permission, decision });

    private async Task<PermissionResult> ReadAsync(Guid user, string role, string permission = Key)
    {
        var inputs = await PermissionStore.ReadAsync(db.App, user);
        return Permissions.EvaluatePermission(new PermissionSubject(user.ToString(), role), permission, inputs.Overrides, inputs.Groups);
    }

    private static PermissionResult Result(bool allowed, PermissionSource source, params Guid[] groups) =>
        new(Key, allowed, source, null, groups.Length == 0 ? null : groups.Select(g => g.ToString()).Order(StringComparer.Ordinal).ToList());

    private static void Same(PermissionResult expected, PermissionResult actual)
    {
        Assert.Equal((expected.Permission, expected.Allowed, expected.Source, expected.Reason), (actual.Permission, actual.Allowed, actual.Source, actual.Reason));
        Assert.Equal(expected.Groups, actual.Groups);
    }

    [Fact]
    public async Task Nothing_stored_the_role_decides_the_underwriter_is_refused_everything_and_the_accountant_has_its_set()
    {
        var w = await WorldAsync();
        var inputs = await PermissionStore.ReadAsync(db.App, w.Anna);
        Assert.Empty(inputs.Overrides);
        Assert.Empty(inputs.Groups.Memberships);
        Assert.Empty(inputs.Groups.Grants);
        var all = Permissions.EvaluateAll(new PermissionSubject(w.Anna.ToString(), "underwriter"), inputs.Overrides, inputs.Groups);
        Assert.Equal(Permissions.All.Keys, all.Select(r => r.Permission));
        Assert.All(all, r => Assert.Equal((false, PermissionSource.NotInRole), (r.Allowed, r.Source)));
        Same(Result(true, PermissionSource.Role), await ReadAsync(w.Cara, "accountant"));
        // Insurance-Software PR #362 (permission-storage.test.ts L50, permission-actions.test.ts L152, head 540e81a): the reinsurer's
        // commission settlement is last in the catalogue; the accountant has it by role, the underwriter and the viewer do not.
        Assert.Equal("reinsurance.commission.settle", all[^1].Permission);
        const string Settle = "reinsurance.commission.settle";
        var accountant = await ReadAsync(w.Cara, "accountant", Settle);
        Assert.Equal((true, PermissionSource.Role), (accountant.Allowed, accountant.Source));
        foreach (var role in new[] { "underwriter", "viewer" })
        {
            var other = await ReadAsync(w.Anna, role, Settle);
            Assert.Equal((false, PermissionSource.NotInRole), (other.Allowed, other.Source));
        }
    }

    [Fact]
    public async Task A_group_gives_it_to_its_members_only_and_only_the_members_own_groups_are_read()
    {
        var w = await WorldAsync();
        await Member(w.Cashiers, w.Anna); await Member(w.Other, w.Anna); await Member(w.Other, w.Beka); await Member(w.Sales, w.Beka);
        await Grant(w.Cashiers); await Grant(w.Sales);
        Same(Result(true, PermissionSource.Group, w.Cashiers), await ReadAsync(w.Anna, "underwriter"));
        var inputs = await PermissionStore.ReadAsync(db.App, w.Anna);
        Assert.Equal(new[] { w.Cashiers.ToString(), w.Other.ToString() }.Order(StringComparer.Ordinal), inputs.Groups.Memberships.Select(m => m.GroupId));
        Assert.Equal([(w.Cashiers.ToString(), Key)], inputs.Groups.Grants.Select(g => (g.GroupId, g.Permission)));
        Same(Result(true, PermissionSource.Group, w.Sales), await ReadAsync(w.Beka, "underwriter"));
        Same(Result(false, PermissionSource.NotInRole), await ReadAsync(w.Cara, "underwriter"));
    }

    [Fact]
    public async Task Several_groups_add_up_each_listed_once_and_a_role_that_has_it_stays_role_with_the_groups_beside_it()
    {
        var w = await WorldAsync();
        await Member(w.Sales, w.Anna); await Member(w.Cashiers, w.Anna); await Grant(w.Sales); await Grant(w.Cashiers);
        await Member(w.Cashiers, w.Cara);
        Same(Result(true, PermissionSource.Group, w.Cashiers, w.Sales), await ReadAsync(w.Anna, "sales"));
        Same(Result(true, PermissionSource.Role, w.Cashiers), await ReadAsync(w.Cara, "accountant"));
    }

    [Fact]
    public async Task The_own_decision_decides_on_top_and_another_users_decision_is_not_read()
    {
        var w = await WorldAsync();
        await Member(w.Cashiers, w.Anna); await Grant(w.Cashiers);
        await Decide(w.Anna, "deny"); await Decide(w.Cara, "deny"); await Decide(w.Beka, "allow");
        Same(Result(false, PermissionSource.Revoked), await ReadAsync(w.Anna, "underwriter"));
        Same(Result(false, PermissionSource.Revoked), await ReadAsync(w.Cara, "accountant"));
        Same(Result(true, PermissionSource.Granted), await ReadAsync(w.Beka, "claims"));
        Same(Result(false, PermissionSource.NotInRole), await ReadAsync(w.Admin, "underwriter"));
        var inputs = await PermissionStore.ReadAsync(db.App, w.Anna);
        Assert.Equal([new PermissionOverride(w.Anna.ToString(), Key, "deny")], inputs.Overrides);
    }

    private Task<PermissionStore.ChangeResult> AsAdmin(World w, Func<Tx, Task<PermissionStore.ChangeResult>> run) => db.App.AsUserAsync(w.Admin, run);

    [Fact]
    public async Task Changes_through_the_real_operations_follow_back_to_the_group_out_of_it_the_grant_off_another_group_still_giving()
    {
        var w = await WorldAsync();
        await Member(w.Cashiers, w.Anna); await Member(w.Sales, w.Anna); await Grant(w.Cashiers); await Grant(w.Sales); await Decide(w.Anna, "deny");
        var steps = new List<PermissionResult> { await ReadAsync(w.Anna, "underwriter") };
        await AsAdmin(w, tx => PermissionStore.SetUserDecisionAsync(tx, w.Anna, Key, "inherit")); steps.Add(await ReadAsync(w.Anna, "underwriter"));
        await AsAdmin(w, tx => PermissionStore.SetGroupMemberAsync(tx, w.Sales, w.Anna, false)); steps.Add(await ReadAsync(w.Anna, "underwriter"));
        await AsAdmin(w, tx => PermissionStore.SetGroupGrantAsync(tx, w.Cashiers, Key, false)); steps.Add(await ReadAsync(w.Anna, "underwriter"));
        await AsAdmin(w, tx => PermissionStore.SetUserDecisionAsync(tx, w.Anna, Key, "allow")); steps.Add(await ReadAsync(w.Anna, "underwriter"));
        var expected = new[]
        {
            Result(false, PermissionSource.Revoked), Result(true, PermissionSource.Group, w.Cashiers, w.Sales), Result(true, PermissionSource.Group, w.Cashiers),
            Result(false, PermissionSource.NotInRole), Result(true, PermissionSource.Granted),
        };
        for (var i = 0; i < expected.Length; i++) Same(expected[i], steps[i]);
    }

    private async Task<(int Audit, IReadOnlyList<string> Actions)> AuditOfAsync(string table, Guid user, int skip)
    {
        await using var c = await db.OpenAdminAsync();
        var rows = (await c.QueryAsync<(string Action, Guid? By)>(
            "select action, user_id from core.audit_log where table_name = @table and (new_row like @user or old_row like @user) order by id", new { table, user = $"%{user}%" })).ToList();
        return (rows.Count, rows.Skip(skip).Select(r => $"{r.Action}:{r.By}").ToList());
    }

    [Fact]
    public async Task A_users_decision_allow_again_deny_inherit_inherit_again_writes_only_what_changes_with_the_admin_as_author()
    {
        var w = await WorldAsync();
        var answers = new List<(bool, bool)>();
        foreach (var choice in new[] { "allow", "allow", "deny", "inherit", "inherit" })
        {
            var r = await AsAdmin(w, tx => PermissionStore.SetUserDecisionAsync(tx, w.Anna, Key, choice));
            answers.Add((r.Ok, r.Changed));
        }
        Assert.Equal([(true, true), (true, false), (true, true), (true, true), (true, false)], answers);
        var (_, actions) = await AuditOfAsync("user_permission", w.Anna, 0);
        Assert.Equal([$"insert:{w.Admin}", $"update:{w.Admin}", $"delete:{w.Admin}"], actions);
        await using var c = await db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.user_permission where user_id = @anna", new { anna = w.Anna }));
        // The admin's own decision the same way, beside another user's.
        await Decide(w.Beka, "allow");
        Assert.True((await AsAdmin(w, tx => PermissionStore.SetUserDecisionAsync(tx, w.Admin, Key, "deny"))).Changed);
        // Both decisions, whatever their order: two rows written at once can share their created_at on a coarse clock (CI).
        Assert.Equal(new[] { (w.Beka, "allow"), (w.Admin, "deny") }.OrderBy(r => r.Item2, StringComparer.Ordinal), (await c.QueryAsync<(Guid, string)>(
            "select user_id, decision from core.user_permission where user_id in (@beka, @admin)", new { beka = w.Beka, admin = w.Admin })).OrderBy(r => r.Item2, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Refused_with_nothing_written_not_an_id_an_unknown_permission_a_decision_other_than_allow_deny_inherit_a_missing_user()
    {
        var w = await WorldAsync();
        var cases = new (string? User, string? Permission, string? Choice, string Refusal)[]
        {
            ("", Key, "allow", "invalid-id"), ("anna", Key, "allow", "invalid-id"),
            (w.Anna.ToString(), "premium.receipt.delete", "allow", "unknown-permission"), (w.Anna.ToString(), "__proto__", "allow", "unknown-permission"), (w.Anna.ToString(), null, "allow", "unknown-permission"),
            (w.Anna.ToString(), Key, "ALLOW", "invalid-decision"), (w.Anna.ToString(), Key, "yes", "invalid-decision"), (w.Anna.ToString(), Key, null, "invalid-decision"),
            (Guid.NewGuid().ToString(), Key, "allow", "missing-user"),
        };
        await using var c = await db.OpenAdminAsync();
        var before = await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log");
        foreach (var (user, permission, choice, refusal) in cases)
        {
            var r = await AsAdmin(w, tx => PermissionPanel.SetUserDecisionAsync(tx, user, permission, choice));
            Assert.False(r.Ok);
            Assert.Equal(refusal, r.Reason!.Value.ToKebabCode());
        }
        Assert.Equal(before, await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log"));
    }

    [Fact]
    public async Task Membership_in_again_and_out_touches_only_that_users_row_and_a_missing_group_or_user_is_refused()
    {
        var w = await WorldAsync();
        await Member(w.Cashiers, w.Beka);
        var answers = new List<string>();
        foreach (var (g, u, m) in new[] { (w.Cashiers.ToString(), w.Anna.ToString(), "true"), (w.Cashiers.ToString(), w.Anna.ToString(), "true"), (w.Cashiers.ToString(), w.Anna.ToString(), "false"),
                     (Guid.NewGuid().ToString(), w.Anna.ToString(), "true"), (w.Cashiers.ToString(), Guid.NewGuid().ToString(), "true"), ("cashiers", w.Anna.ToString(), "true") })
        {
            var r = await AsAdmin(w, tx => PermissionPanel.SetGroupMemberAsync(tx, g, u, m));
            answers.Add(r.Ok ? $"ok {r.Changed}" : r.Reason!.Value.ToKebabCode());
        }
        Assert.Equal(["ok True", "ok False", "ok True", "missing-group", "missing-user", "invalid-id"], answers);
        await using var c = await db.OpenAdminAsync();
        Assert.Equal([w.Beka], (await c.QueryAsync<Guid>("select user_id from core.user_group_member where group_id = @g", new { g = w.Cashiers })).ToList());
    }

    [Fact]
    public async Task A_groups_permission_ticked_again_and_unticked_and_an_unknown_permission_is_never_stored()
    {
        var w = await WorldAsync();
        await Grant(w.Sales);
        var answers = new List<string>();
        foreach (var (g, p, on) in new[] { (w.Cashiers, Key, "true"), (w.Cashiers, Key, "true"), (w.Cashiers, Key, "false"), (w.Cashiers, "premium.receipt.delete", "true"), (Guid.NewGuid(), Key, "true") })
        {
            var r = await AsAdmin(w, tx => PermissionPanel.SetGroupGrantAsync(tx, g.ToString(), p, on));
            answers.Add(r.Ok ? $"ok {r.Changed}" : r.Reason!.Value.ToKebabCode());
        }
        Assert.Equal(["ok True", "ok False", "ok True", "unknown-permission", "missing-group"], answers);
        await using var c = await db.OpenAdminAsync();
        Assert.Equal([(w.Sales, Key)], (await c.QueryAsync<(Guid, string)>("select group_id, permission from core.user_group_permission where group_id in (@a, @b)", new { a = w.Cashiers, b = w.Sales })).ToList());
    }

    [Fact]
    public async Task A_groups_name_is_trimmed_empty_or_long_or_taken_in_any_case_is_refused_and_the_same_name_writes_nothing()
    {
        var w = await WorldAsync();
        var created = await AsAdmin(w, tx => PermissionStore.CreateGroupAsync(tx, $"  ბუღალტერია {w.Tag}  "));
        Assert.True(created is { Ok: true, Changed: true, Id: not null });
        await using var c = await db.OpenAdminAsync();
        Assert.Equal($"ბუღალტერია {w.Tag}", await c.ExecuteScalarAsync<string>("select name from core.user_group where id = @id", new { id = created.Id }));
        var answers = new List<string>();
        foreach (var run in new Func<Tx, Task<PermissionStore.ChangeResult>>[]
                 {
                     tx => PermissionStore.CreateGroupAsync(tx, "   "), tx => PermissionStore.CreateGroupAsync(tx, new string('x', 101)),
                     tx => PermissionStore.CreateGroupAsync(tx, $"ბუღალტერია {w.Tag}"), tx => PermissionStore.RenameGroupAsync(tx, w.Sales, $"სხვა {w.Tag}"),
                     tx => PermissionStore.RenameGroupAsync(tx, w.Sales, $"გაყიდვები {w.Tag}"), tx => PermissionStore.RenameGroupAsync(tx, w.Sales, $"გაყიდვები 2 {w.Tag}"),
                     tx => PermissionStore.RenameGroupAsync(tx, Guid.NewGuid(), "ა"),
                 })
        {
            var r = await AsAdmin(w, run);
            answers.Add(r.Ok ? $"ok {r.Changed}" : r.Reason!.Value.ToKebabCode());
        }
        Assert.Equal(["invalid-name", "invalid-name", "duplicate-name", "duplicate-name", "ok False", "ok True", "missing-group"], answers);
        Assert.Equal($"გაყიდვები 2 {w.Tag}", await c.ExecuteScalarAsync<string>("select name from core.user_group where id = @id", new { id = w.Sales }));
        var latin = await AsAdmin(w, tx => PermissionStore.CreateGroupAsync(tx, $"Cashiers {w.Tag}"));
        Assert.True(latin.Ok);
        Assert.Equal("duplicate-name", (await AsAdmin(w, tx => PermissionStore.CreateGroupAsync(tx, $"CASHIERS {w.Tag.ToUpperInvariant()}"))).Reason!.Value.ToKebabCode());
        Assert.Equal("duplicate-name", (await AsAdmin(w, tx => PermissionStore.RenameGroupAsync(tx, w.Other, $"cashiers {w.Tag}"))).Reason!.Value.ToKebabCode());
    }

    [Theory]
    [InlineData("insert core.user_permission (user_id, permission, decision) values (@user, N'premium.receipt.create', N'maybe')")]
    [InlineData("insert core.user_permission (user_id, permission, decision) values (@user, N'Premium.Receipt', N'allow')")]
    [InlineData("insert core.user_group_permission (group_id, permission) values (@group, N'no..such')")]
    [InlineData("insert core.user_group_member (group_id, user_id) values (@group, @user); insert core.user_group_member (group_id, user_id) values (@group, @user)")]
    [InlineData("insert core.user_group (name) values (N' padded ')")]
    public async Task The_schema_itself_refuses_what_the_store_never_writes(string statement)
    {
        var w = await WorldAsync();
        await using var c = await db.OpenAppAsync();
        await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(statement, new { user = w.Anna, group = w.Cashiers }));
    }

    [Fact]
    public async Task Every_permission_table_is_audited()
    {
        await using var c = await db.OpenAdminAsync();
        var triggers = (await c.QueryAsync<string>("select name from sys.triggers where name in (N'user_group_audit', N'user_group_member_audit', N'user_group_permission_audit', N'user_permission_audit') order by name")).ToList();
        Assert.Equal(["user_group_audit", "user_group_member_audit", "user_group_permission_audit", "user_permission_audit"], triggers);
    }
}
