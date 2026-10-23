using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Domain.Common;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests.FailureInjection;

/// <summary>
/// Reads of the permission storage and of the admin's panel failing once, and the admin's changes failing in the middle, on the
/// real SQL Server through the command interceptor (legacy scripts/integration/permission-storage.test.ts "a failed read stops,
/// never 'nothing stored'" and "a database error that is not a taken name is not hidden", permission-panel.test.ts "a failed
/// read stops the page", permission-actions.test.ts "a database error in the middle"). Anna is an accountant in Cashiers, which
/// is given premium.receipt.create, and has removed it by her own choice.
/// </summary>
public sealed class PermissionFaultTests : IClassFixture<TestDatabase>
{
    private const string Key = "premium.receipt.create";
    private readonly TestDatabase db;
    private readonly Faults faults = new();
    private readonly Db app;

    public PermissionFaultTests(TestDatabase db)
    {
        this.db = db;
        app = new Db(new DbOptions { ConnectionString = db.AppConnection, Interceptor = faults });
    }

    private sealed record World(Guid Anna, Guid Admin, Guid Cashiers, string CashiersName, string Tag);

    private async Task<World> WorldAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var anna = await Fixtures.UserAsync(db, "accountant");
        var admin = await Fixtures.UserAsync(db, "admin");
        var cashiers = Guid.NewGuid();
        var name = $"სალაროები {tag}";
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync("""
            update core.app_user set full_name = @full where id = @anna;
            insert core.user_group (id, name) values (@cashiers, @name);
            insert core.user_group_member (group_id, user_id) values (@cashiers, @anna);
            insert core.user_group_permission (group_id, permission) values (@cashiers, @key);
            insert core.user_permission (user_id, permission, decision) values (@anna, @key, N'deny');
            """, new { anna, full = $"ანა {tag}", cashiers, name, key = Key });
        return new World(anna, admin, cashiers, name, tag);
    }

    private static CurrentUser As(Guid id, string role) => new(id, $"test.{id:N}"[..30], "სატესტო", role, role);

    private static string Shown(IReadOnlyList<PermissionResult> all) => string.Join(";", all.Select(r => $"{r.Permission}={r.Allowed}/{r.Source}/{string.Join(",", r.Groups ?? [])}"));

    // ================================================================ permission-storage.test.ts

    [Theory]
    [InlineData("own decisions")]
    [InlineData("memberships")]
    [InlineData("group grants")]
    public async Task Storage_the_read_fails_the_same_error_comes_out_no_answer_the_retry_on_the_same_rows_answers_right(string name)
    {
        var w = await WorldAsync();
        var plain = new Db(new DbOptions { ConnectionString = db.AppConnection });
        var right = Shown(await PermissionStore.LoadUserPermissionsAsync(plain, As(w.Anna, "accountant")));
        var asUnderwriter = Shown(await PermissionStore.LoadUserPermissionsAsync(plain, As(w.Anna, "underwriter")));
        var sentinel = new InjectedFault("PERMISSION_READ_SENTINEL");
        faults.FailOnce(Faults.Any(Faults.PermissionReads.Single(r => r.Name == name).Fragment), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => PermissionStore.LoadUserPermissionsAsync(app, As(w.Anna, "accountant"))));
        Assert.True(faults.AllFired);
        // Never "nothing stored": the retry answers from the same rows — the own removal, the group beside it, the role for the rest.
        Assert.Equal(right, Shown(await PermissionStore.LoadUserPermissionsAsync(app, As(w.Anna, "accountant"))));
        Assert.StartsWith($"{Key}=False/Revoked/;claim.recovery.receive=True/Role/", right, StringComparison.Ordinal);
        Assert.Equal(asUnderwriter, Shown(await PermissionStore.LoadUserPermissionsAsync(app, As(w.Anna, "underwriter"))));
        Assert.DoesNotContain(faults.Log, c => c.Isolation == System.Data.IsolationLevel.ReadCommitted);
    }

    [Fact]
    public async Task Storage_a_database_error_that_is_not_a_taken_name_is_not_hidden()
    {
        var w = await WorldAsync();
        var boom = new InjectedFault("INSERT_SENTINEL");
        faults.FailOnce(Faults.Any("insert into core.user_permission"), boom);
        Assert.Same(boom, await Assert.ThrowsAsync<InjectedFault>(() => PermissionPanel.ChangeAsync(app, w.Admin, tx => PermissionStore.SetUserDecisionAsync(tx, w.Admin, Key, "allow"))));
        faults.FailOnce(Faults.Any("insert into core.user_group (id, name)"), boom);
        Assert.Same(boom, await Assert.ThrowsAsync<InjectedFault>(() => PermissionPanel.ChangeAsync(app, w.Admin, tx => PermissionStore.CreateGroupAsync(tx, "ახალი " + w.Tag))));
        await using var c = await db.OpenAppAsync();
        Assert.Equal((0, 0), await c.QuerySingleAsync<(int, int)>("select (select count(*) from core.user_permission where user_id = @a), (select count(*) from core.user_group where name = @n)",
            new { a = w.Admin, n = "ახალი " + w.Tag }));
    }

    /// <summary>
    /// Legacy createGroup / renameGroup: the taken name is looked up first, and the unique key's refusal is the fallback for two
    /// admins naming two groups alike at the same moment — answered as "duplicate-name", nothing written. Here the other admin's
    /// group lands between the look-up and the insert (or the rename).
    /// </summary>
    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    public async Task Storage_a_name_taken_by_another_admin_between_the_look_up_and_the_write_is_refused_as_a_taken_name_with_nothing_written(string change)
    {
        var w = await WorldAsync();
        var name = $"ბუღალტერია {w.Tag}";
        void Other()
        {
            using var c = new SqlConnection(db.AppConnection);
            c.Open();
            c.Execute("insert core.user_group (id, name) values (newid(), @name)", new { name });
        }
        faults.BeforeOnce(Faults.Any(change == "create" ? "insert into core.user_group (id, name)" : "update core.user_group set name"), Other);
        var answer = await PermissionPanel.ChangeAsync(app, w.Admin, tx => change == "create"
            ? PermissionStore.CreateGroupAsync(tx, name)
            : PermissionStore.RenameGroupAsync(tx, w.Cashiers, name));
        Assert.True(faults.AllFired);
        Assert.Equal(new PermissionPanel.Answer(false, PermissionTexts.Changes.Refusals["duplicate-name"], false), answer);
        await using var r = await db.OpenAppAsync();
        Assert.Equal(1, await r.ExecuteScalarAsync<int>("select count(*) from core.user_group where name = @name", new { name }));
        Assert.Equal(w.CashiersName, await r.ExecuteScalarAsync<string>("select name from core.user_group where id = @id", new { id = w.Cashiers }));
    }

    // ================================================================ permission-actions.test.ts

    [Fact]
    public async Task Actions_a_database_error_in_the_middle_comes_out_as_it_is_nothing_of_the_change_stays_the_retry_saves_it_once()
    {
        var w = await WorldAsync();
        var beka = await Fixtures.UserAsync(db, "underwriter");
        var sentinel = new InjectedFault("PERMISSION_WRITE_SENTINEL");
        faults.FailOnce(Faults.Any("insert into core.user_group_member"), sentinel);
        Task<PermissionPanel.Answer> Put() => PermissionPanel.ChangeAsync(app, w.Admin, tx => PermissionPanel.SetGroupMemberAsync(tx, w.Cashiers.ToString(), beka.ToString(), "true"));
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(Put));
        await using var c = await db.OpenAppAsync();
        async Task<int> Members() => await c.ExecuteScalarAsync<int>("select count(*) from core.user_group_member where group_id = @g and user_id = @beka", new { g = w.Cashiers, beka });
        async Task<int> Audited() => await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log where table_name = N'user_group_member' and user_id = @a", new { a = w.Admin });
        Assert.Equal((0, 0), (await Members(), await Audited()));
        Assert.Equal(new PermissionPanel.Answer(true, PermissionTexts.Changes.Saved, true), await Put());
        Assert.Equal((1, 1), (await Members(), await Audited()));
        Assert.Equal(new PermissionPanel.Answer(true, PermissionTexts.Changes.Unchanged, false), await Put());
        Assert.Equal(1, await Members());
    }

    // ================================================================ permission-panel.test.ts

    /// <summary>Each read of the panel (legacy PANEL_SQL and SQL), with the address that makes the page read it.</summary>
    public static TheoryData<string, string, string?, string?, string?, string?> PanelReads() => new()
    {
        { "roles", "select code, name_ka from core.role order by code", null, null, null, null },
        { "every user", "from core.app_user order by username", null, null, null, null },
        { "users found", "from core.app_user\nwhere charindex(lower(@query), lower(username))", "ა", null, null, null },
        { "every group", "select top (@limit) id as Id, name as Name from core.user_group order by name", null, null, null, null },
        { "groups found", "from core.user_group where charindex(lower(@groupQuery), lower(name))", null, "ს", null, null },
        { "the chosen user", "from core.app_user where id = @userId", null, null, "anna", null },
        { "own decisions", Faults.Overrides, null, null, "anna", null },
        { "memberships", Faults.Memberships, null, null, "anna", null },
        { "group grants", Faults.Grants, null, null, "anna", null },
        { "membership names", "from core.user_group where id in", null, null, "anna", null },
        { "the chosen group", "from core.user_group where id = @groupId", null, null, null, "cashiers" },
        { "its permissions", "from core.user_group_permission where group_id = @id", null, null, null, "cashiers" },
        { "its member ids", "select user_id from core.user_group_member where group_id = @id", null, null, null, "cashiers" },
        { "its members", "from core.app_user where id in", null, null, null, "cashiers" },
    };

    [Theory]
    [MemberData(nameof(PanelReads))]
    public async Task Panel_a_failed_read_the_same_error_comes_out_no_page_the_next_render_is_whole(string name, string fragment, string? q, string? gq, string? user, string? group)
    {
        var w = await WorldAsync();
        string? Id(string? which) => which switch { "anna" => w.Anna.ToString(), "cashiers" => w.Cashiers.ToString(), _ => null };
        var sentinel = new InjectedFault($"PANEL_READ_SENTINEL {name}");
        faults.FailOnce(c => c.Sql.Replace("\r\n", "\n", StringComparison.Ordinal).Contains(fragment, StringComparison.Ordinal), sentinel);
        Assert.Same(sentinel, await Assert.ThrowsAsync<InjectedFault>(() => PermissionPanel.ReadAsync(app, q, gq, Id(user), Id(group))));
        Assert.True(faults.AllFired, name);
        var panel = await PermissionPanel.ReadAsync(app, q, gq, w.Anna.ToString(), w.Cashiers.ToString());
        var shown = panel.User!.Permissions.Single(p => p.Key == Key);
        Assert.Equal((false, PermissionSource.Revoked, "deny", w.CashiersName), (shown.Allowed, shown.Source, shown.Choice, string.Join(",", shown.GroupNames)));
        Assert.Equal([w.Anna], panel.Group!.Members.Select(m => m.Id));
        Assert.Equal([Key], panel.Group.Granted);
    }
}
