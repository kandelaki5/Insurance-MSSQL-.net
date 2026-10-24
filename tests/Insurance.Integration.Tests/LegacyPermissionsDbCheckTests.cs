using Dapper;
using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Domain.Common;
using Insurance.Integration.Tests.Policies;

namespace Insurance.Integration.Tests;

/// <summary>
/// Legacy scripts/permissions-db-check.ts (owner 16.10.2026, migrations 0030–0032): the application's own permission store
/// through core_app and asUser on a real database — the tables' rights, checks, unique keys and audit; the role defaults; a
/// group's grant, a personal removal and inherit; the store's own refusals; the audit of each change by the admin — and the
/// report's bulk reads (LoadSchedulesAsync against LoadScheduleAsync for every policy, the bulk allocations against the
/// per-policy ones) and exact numeric addition. Its own fictional users and group in its own database.
/// </summary>
public sealed class LegacyPermissionsDbCheckTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private static readonly string[] Tables = ["user_group", "user_group_member", "user_group_permission", "user_permission"];
    private readonly World w = new(db);

    [Fact]
    public async Task The_four_permission_tables_core_app_may_read_and_change_them()
    {
        await using var c = await db.OpenAppAsync();
        foreach (var t in Tables)
            Assert.Equal((1, 1, 1, 1), await c.QuerySingleAsync<(int, int, int, int)>("""
                select has_perms_by_name(@o, 'OBJECT', 'SELECT'), has_perms_by_name(@o, 'OBJECT', 'INSERT'), has_perms_by_name(@o, 'OBJECT', 'UPDATE'), has_perms_by_name(@o, 'OBJECT', 'DELETE')
                """, new { o = $"core.{t}" }));
    }

    [Fact]
    public async Task The_store_on_the_real_database_role_defaults_a_group_a_removal_inherit_refusals_and_the_audit_by_the_admin()
    {
        var adminId = await Fixtures.UserAsync(db);
        var admin = new CurrentUser(adminId, "zzdb-admin", "ბაზის შემოწმება", "admin", "admin");
        var viewer = await w.UserAsync("viewer");
        var accountant = await w.UserAsync("accountant");
        Task<bool> May(CurrentUser u, string key) => PermissionStore.HasPermissionAsync(w.App, u, key);
        Task<PermissionStore.ChangeResult> Change(Func<Tx, Task<PermissionStore.ChangeResult>> fn) => w.App.AsUserAsync(admin.Id, fn);
        var auditFrom = await w.ScalarAsync<long?>("select max(id) from core.audit_log") ?? 0;

        var defaults = new List<bool> { await May(viewer, "policy.approve"), await May(viewer, "accounting.period.close"), await May(accountant, "accounting.period.close"),
            await May(accountant, "premium.receipt.create"), await May(accountant, "policy.approve") };
        Assert.Equal([false, false, true, true, false], defaults);

        var created = await Change(tx => PermissionStore.CreateGroupAsync(tx, "zzdb შემოწმება " + Guid.NewGuid().ToString("N")[..6]));
        Assert.True(created.Ok && created.Id is not null);
        var group = created.Id!.Value;
        var granted = await Change(tx => PermissionStore.SetGroupGrantAsync(tx, group, "policy.approve", true));
        var joined = await Change(tx => PermissionStore.SetGroupMemberAsync(tx, group, viewer.Id, true));
        Assert.True(granted.Ok && joined.Ok);
        Assert.True(await May(viewer, "policy.approve"));
        Assert.Equal(PermissionSource.Group, (await PermissionStore.LoadUserPermissionsAsync(w.App, viewer)).Single(p => p.Permission == "policy.approve").Source);

        Assert.True((await Change(tx => PermissionStore.SetUserDecisionAsync(tx, viewer.Id, "policy.approve", "deny"))).Ok);
        Assert.False(await May(viewer, "policy.approve"));
        Assert.True((await Change(tx => PermissionStore.SetUserDecisionAsync(tx, viewer.Id, "policy.approve", "inherit"))).Ok);
        Assert.True(await May(viewer, "policy.approve"));

        Assert.True((await Change(tx => PermissionStore.SetUserDecisionAsync(tx, accountant.Id, "accounting.period.close", "deny"))).Ok);
        Assert.False(await May(accountant, "accounting.period.close"));
        Assert.True((await Change(tx => PermissionStore.SetUserDecisionAsync(tx, accountant.Id, "accounting.period.close", "inherit"))).Ok);
        Assert.True(await May(accountant, "accounting.period.close"));

        var repeat = await Change(tx => PermissionStore.SetGroupGrantAsync(tx, group, "policy.approve", true));
        Assert.Equal((true, false), (repeat.Ok, repeat.Changed));
        var unknown = await Change(tx => PermissionStore.SetUserDecisionAsync(tx, viewer.Id, "no.such", "allow"));
        Assert.Equal((false, (PermissionStore.Refusal?)PermissionStore.Refusal.UnknownPermission), (unknown.Ok, unknown.Reason));

        // The database's own guards, past the store, as core_app.
        async Task<string?> Raw(string permission, string decision)
        {
            var e = await Record.ExceptionAsync(() => w.App.AsUserAsync(admin.Id, tx =>
                tx.ExecuteAsync("insert into core.user_permission (user_id, permission, decision) values (@user, @permission, @decision)", new { user = viewer.Id, permission, decision })));
            return e is null ? "no error" : DbError.From(e)?.Code ?? e.Message;
        }
        Assert.Equal("23514", await Raw("Not A Key", "allow"));
        Assert.Equal("23514", await Raw("policy.approve", "maybe"));
        Assert.Equal("no error", await Raw("claim.payment.record", "allow"));
        Assert.Equal("23505", await Raw("claim.payment.record", "deny"));
        Assert.True((await Change(tx => PermissionStore.SetUserDecisionAsync(tx, viewer.Id, "claim.payment.record", "inherit"))).Ok);

        // The audit: each change, all by the admin.
        var audit = await w.QueryAsync<(string Table, string Action, Guid? User)>(
            "select table_name, action, user_id from core.audit_log where id > @auditFrom and table_name in @tables order by id", new { auditFrom, tables = Tables });
        Assert.All(audit, a => Assert.Equal(admin.Id, a.User));
        Assert.Equal(new Dictionary<string, int> { ["user_group:insert"] = 1, ["user_group_permission:insert"] = 1, ["user_group_member:insert"] = 1, ["user_permission:insert"] = 3, ["user_permission:delete"] = 3 },
            audit.GroupBy(a => $"{a.Table}:{a.Action}").ToDictionary(g => g.Key, g => g.Count()));

        // The legacy cleanup: the group emptied through the store; nothing of it left.
        Assert.True((await Change(tx => PermissionStore.SetGroupGrantAsync(tx, group, "policy.approve", false))).Ok);
        Assert.True((await Change(tx => PermissionStore.SetGroupMemberAsync(tx, group, viewer.Id, false))).Ok);
        Assert.Equal((0, 0), (await w.ScalarAsync<int>("select count(*) from core.user_group_permission where group_id = @group", new { group }),
            await w.ScalarAsync<int>("select count(*) from core.user_group_member where group_id = @group", new { group })));
    }

    [Fact]
    public async Task Exact_numeric_addition_and_the_bulk_schedules_and_allocations_equal_the_per_policy_reads()
    {
        Assert.Equal("0.01", await w.ScalarAsync<string>("select convert(nvarchar(40), convert(decimal(18, 2), 90071992547408.00) + convert(decimal(18, 2), -90071992547407.99))"));
        Assert.Equal("0.01", Billing.TetriText(Billing.Tetri(90071992547408.00m) + Billing.Tetri(-90071992547407.99m)));

        // Policies with what the bulk reader must agree on: a plain schedule, a payment, a cancellation, a reduction.
        var user = await w.UserAsync();
        var start = new DateOnly(2026, 1, 1);
        var plain = await w.ActivePolicyAsync(user, await w.ClientAsync(), start);
        var paid = await w.ActivePolicyAsync(user, await w.ClientAsync(), start);
        await w.PayAsync(user, paid.Id, "200.00", start.AddDays(4));
        var cancelled = await w.ActivePolicyAsync(user, await w.ClientAsync(), start);
        await w.PayAsync(user, cancelled.Id, "365.00", start.AddDays(4));
        await w.CancelAsync(user, cancelled.Id, start.AddDays(181));
        var reduced = await w.ActivePolicyAsync(user, await w.ClientAsync(), start);
        await w.EndorseAsync(user, reduced.Id, start.AddDays(99), "-100");
        var change = await w.ScalarAsync<Guid>("select id from core.policy where replaces_policy_id = @id", new { id = reduced.Id });
        Assert.Equal("done", (await PolicyActions.ApprovePolicyAsync(w.App, user, change.ToString())).Outcome);
        Assert.NotNull(plain);

        var ids = await w.QueryAsync<Guid>("select id from core.policy order by id");
        foreach (var asOf in new[] { new DateOnly(2026, 10, 31), World.Today, start.AddDays(50) })
        {
            var bulk = await w.App.ReadTxAsync(tx => Billing.LoadSchedulesAsync(tx, ids, asOf));
            foreach (var id in ids)
            {
                var one = await w.App.ReadTxAsync(tx => Billing.LoadScheduleAsync(tx, id, asOf));
                Assert.Equal(one, bulk.TryGetValue(id, out var b) ? b : []);
            }
        }
        var lastDay = World.Today;
        const string allocations = """
            select i.policy_id as PolicyId, a.amount as Amount, a.booked_on as BookedOn, p.method as Method
            from core.payment_allocation a join core.payment p on p.id = a.payment_id join core.installment i on i.id = a.installment_id
            where a.booked_on <= @lastDay
            """;
        var all = await w.QueryAsync<(Guid PolicyId, decimal Amount, DateOnly BookedOn, string Method)>(allocations + " and i.policy_id in @ids", new { ids, lastDay });
        foreach (var id in ids)
        {
            var one = await w.QueryAsync<(Guid PolicyId, decimal Amount, DateOnly BookedOn, string Method)>(allocations + " and i.policy_id = @id", new { id, lastDay });
            Assert.Equal(one.Select(r => $"{r.BookedOn}|{r.Amount}|{r.Method}").Order(), all.Where(r => r.PolicyId == id).Select(r => $"{r.BookedOn}|{r.Amount}|{r.Method}").Order());
        }
    }
}
