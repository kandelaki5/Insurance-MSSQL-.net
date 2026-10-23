using Dapper;
using Insurance.Application.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// The schema, rights and audit probes of the legacy real-database checks, on the real SQL Server: scripts/verify-0025.ts (closed
/// history cannot change, even for the database owner; settings are audited; core_app's limits), scripts/verify-0026-0028.ts parts 3
/// and 4 (loss events: the rules are in place, a claim moved to another policy's event or onto a coverage its event already has is
/// refused, core_app's rights), the regress catalog audit scripts/regress-db/catalog.ts K1–K9 in SQL Server's own catalog, and the
/// zero-row negative attempts N1–N6 of scripts/regress-db/negative.ts. Probes that write run in a transaction that is rolled back.
/// </summary>
public sealed class LegacyDbCheckSchemaTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    /// <summary>Runs <paramref name="probe"/> as the administrator in a transaction that is always rolled back; "ok" or the refusal's code.</summary>
    private async Task<string> AdminProbeAsync(Func<SqlConnection, SqlTransaction, Task> probe)
    {
        await using var c = await db.OpenAdminAsync();
        await using var t = (SqlTransaction)await c.BeginTransactionAsync();
        var code = await LegacyDbCheck.BareCodeOf(() => probe(c, t));
        await LegacyDbCheck.RollbackQuietlyAsync(t);
        return code;
    }

    private async Task<DateOnly> ClosedMonthAsync(Guid actor, DateOnly month)
    {
        await db.App.AsUserAsync(actor, tx => tx.ExecuteAsync(
            "insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @actor)", new { month, actor }));
        return month;
    }

    private static async Task<HashSet<string>> TriggersAsync(SqlConnection c, params string[] tables) =>
        (await c.QueryAsync<string>("""
            select convert(nvarchar(400), object_name(t.parent_id) + N'.' + t.name collate database_default) + N' '
                   + case when t.is_instead_of_trigger = 1 then N'INSTEAD OF ' else N'AFTER ' end
                   + (select string_agg(convert(nvarchar(60), e.type_desc collate database_default), N' OR ') within group (order by e.type)
                      from sys.trigger_events e where e.object_id = t.object_id)
                   + case when t.is_disabled = 1 then N' DISABLED' else N'' end
            from sys.triggers t where t.parent_class = 1 and object_schema_name(t.parent_id) = N'core' and object_name(t.parent_id) in @tables
            """, new { tables })).ToHashSet(StringComparer.Ordinal);

    // ---------------------------------------------------------------- verify-0025.ts

    [Fact]
    public async Task V0025_the_three_triggers_of_settings_and_periods_are_there_and_enabled()
    {
        await using var c = await db.OpenAdminAsync();
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "accounting_period.accounting_period_audit AFTER INSERT OR UPDATE OR DELETE",
            "accounting_period.accounting_period_immutable AFTER UPDATE OR DELETE",
            "setting.setting_audit AFTER INSERT OR UPDATE OR DELETE",
        }, await TriggersAsync(c, "setting", "accounting_period"));
    }

    [Fact]
    public async Task V0025_a_closed_month_cannot_be_reopened_removed_or_have_its_time_author_or_date_changed_even_by_the_database_owner()
    {
        var actor = await Fixtures.UserAsync(db);
        var someone = await Fixtures.UserAsync(db);
        var m = await ClosedMonthAsync(actor, new DateOnly(2711, 3, 1));
        var closedBefore = await db.App.ReadAsync(c => c.ExecuteScalarAsync<int>("select count(*) from core.accounting_period where status = N'closed'"));
        Task<string> Admin(string sql) => AdminProbeAsync((c, t) => c.ExecuteAsync(sql, new { m, someone }, t));
        Assert.Equal("23514", await Admin("update core.accounting_period set status = N'open', closed_at = null, closed_by = null where period_start = @m"));
        Assert.Equal("23514", await Admin("delete from core.accounting_period where period_start = @m"));
        Assert.Equal("23514", await Admin("update core.accounting_period set closed_at = dateadd(day, -1, closed_at) where period_start = @m"));
        Assert.Equal("23514", await Admin("update core.accounting_period set closed_by = @someone where period_start = @m"));
        Assert.Equal("23514", await Admin("update core.accounting_period set period_start = dateadd(year, -1, period_start) where period_start = @m"));
        Assert.Equal("ok", await Admin("update core.accounting_period set status = status where period_start = @m"));
        Assert.Equal("42501", await LegacyDbCheck.CodeOf(() => db.App.AsUserAsync(actor, tx => tx.ExecuteAsync("delete from core.accounting_period where period_start = @m", new { m }))));
        Assert.Equal(closedBefore, await db.App.ReadAsync(c => c.ExecuteScalarAsync<int>("select count(*) from core.accounting_period where status = N'closed'")));
    }

    [Fact]
    public async Task V0025_a_setting_change_is_audited_with_its_author_old_and_new_value_and_an_unchanged_save_writes_nothing()
    {
        var actor = await Fixtures.UserAsync(db);
        await using var c = await db.OpenAppAsync();
        await using var t = (SqlTransaction)await c.BeginTransactionAsync();
        await c.ExecuteAsync("exec sp_set_session_context N'core.user_id', @id", new { id = actor.ToString() }, t);
        var before = await c.ExecuteScalarAsync<string>("select value from core.setting where [key] = N'company_name'", transaction: t);
        var maxId = await c.ExecuteScalarAsync<long>("select coalesce(max(id), 0) from core.audit_log", transaction: t);
        await c.ExecuteAsync("update core.setting set value = value + N' (probe)' where [key] = N'company_name'", transaction: t);
        await c.ExecuteAsync("update core.setting set value = value where [key] = N'company_name'", transaction: t);
        var rows = (await c.QueryAsync<(string Action, Guid? UserId, string Old, string New)>("""
            select action, user_id, json_value(old_row, '$.value'), json_value(new_row, '$.value') from core.audit_log
            where table_name = N'setting' and row_id = N'company_name' and id > @maxId order by id
            """, new { maxId }, t)).ToList();
        await t.RollbackAsync();
        Assert.Equal([("update", (Guid?)actor, before!, before + " (probe)")], rows);
        Assert.Equal(before, await c.ExecuteScalarAsync<string>("select value from core.setting where [key] = N'company_name'"));
    }

    private async Task<string> RightsAsync(string user, params string[] tables)
    {
        await using var c = await db.OpenAdminAsync();
        var rows = await c.QueryAsync<string>($"""
            execute as user = N'{user}';
            select t.name + N':' + convert(nchar(1), has_perms_by_name(N'core.' + quotename(t.name), N'OBJECT', N'INSERT'))
                   + convert(nchar(1), has_perms_by_name(N'core.' + quotename(t.name), N'OBJECT', N'UPDATE'))
                   + convert(nchar(1), has_perms_by_name(N'core.' + quotename(t.name), N'OBJECT', N'DELETE'))
            from sys.tables t where t.schema_id = schema_id(N'core') and t.name in @tables order by t.name;
            revert;
            """, new { tables });
        return string.Join(" ", rows);
    }

    [Fact]
    public async Task V0025_the_application_may_add_and_close_months_not_remove_them_only_add_audit_rows_and_change_settings()
    {
        Assert.Equal("accounting_period:110 audit_log:100 setting:111", await RightsAsync("core_app_rights_probe", "setting", "accounting_period", "audit_log"));
    }

    // ---------------------------------------------------------------- verify-0026-0028.ts, parts 3 and 4

    [Fact]
    public async Task V0028_the_unique_coverage_rule_and_the_policy_guard_of_loss_events_are_in_place()
    {
        await using var c = await db.OpenAdminAsync();
        Assert.Equal("loss_event_id,policy_line_id", await c.ExecuteScalarAsync<string>("""
            select string_agg(col.name, N',') within group (order by ic.key_ordinal)
            from sys.key_constraints k join sys.index_columns ic on ic.object_id = k.parent_object_id and ic.index_id = k.unique_index_id
            join sys.columns col on col.object_id = ic.object_id and col.column_id = ic.column_id
            where k.name = N'claim_event_coverage' and k.parent_object_id = object_id(N'core.claim') and k.type = N'UQ'
            """));
        Assert.Contains("claim.claim_event_policy AFTER INSERT OR UPDATE", await TriggersAsync(c, "claim"));
        // A claim cannot be without its event.
        Assert.False(await c.ExecuteScalarAsync<bool>("select is_nullable from sys.columns where object_id = object_id(N'core.claim') and name = N'loss_event_id'"));
    }

    [Fact]
    public async Task V0028_a_claim_moved_to_another_policys_event_is_LE001_and_onto_a_coverage_its_event_has_is_23505()
    {
        var w = new ClaimWorld(db);
        var u = await w.UserAsync("claims");
        var p = await w.PolicyAsync(u.Id, new DateOnly(2712, 1, 1), [new("damage"), new("equipment")]);
        var other = await w.PolicyAsync(u.Id, new DateOnly(2712, 1, 1), [new("damage")]);
        var a = (await w.RegisterAsync(u, p, new DateOnly(2712, 3, 1), new DateOnly(2712, 3, 2), ("damage", "100")))[0];
        var b = (await w.RegisterAsync(u, p, new DateOnly(2712, 3, 5), new DateOnly(2712, 3, 6), ("equipment", "100")))[0];
        var foreign = (await w.RegisterAsync(u, other, new DateOnly(2712, 3, 1), new DateOnly(2712, 3, 2), ("damage", "100")))[0];
        var foreignEvent = await w.ScalarAsync<Guid>("select loss_event_id from core.claim where id = @foreign", new { foreign });
        var (eventA, lineA) = (await w.RowsAsync<(Guid, Guid)>("select loss_event_id, policy_line_id from core.claim where id = @a", new { a })).Single();
        Assert.Equal("LE001", await LegacyDbCheck.BareCodeOf(() => w.ExecAsync("update core.claim set loss_event_id = @foreignEvent where id = @a", new { foreignEvent, a })));
        Assert.Equal("23505 claim_event_coverage", await LegacyDbCheck.CodeOf(() =>
            w.ExecAsync("update core.claim set loss_event_id = @eventA, policy_line_id = @lineA where id = @b", new { eventA, lineA, b })));
        // Nothing moved; every claim is on the policy of its own event.
        Assert.Equal(0, await w.ScalarAsync<int>("select count(*) from core.claim c join core.loss_event e on e.id = c.loss_event_id where e.policy_id <> c.policy_id"));
    }

    [Fact]
    public async Task V0028_core_app_may_read_and_add_loss_events_and_still_may_not_change_any_ledger()
    {
        await using var c = await db.OpenAppAsync();
        Assert.True(await c.ExecuteScalarAsync<bool>("select convert(bit, has_perms_by_name(N'core.loss_event', N'OBJECT', N'SELECT') & has_perms_by_name(N'core.loss_event', N'OBJECT', N'INSERT'))"));
        Assert.Empty(await c.QueryAsync<string>("""
            select t.name from sys.tables t where t.schema_id = schema_id(N'core')
              and t.name in (N'audit_log', N'premium_transaction', N'payment', N'payment_allocation', N'installment', N'installment_adjustment', N'premium_cession')
              and (has_perms_by_name(N'core.' + quotename(t.name), N'OBJECT', N'UPDATE') = 1 or has_perms_by_name(N'core.' + quotename(t.name), N'OBJECT', N'DELETE') = 1)
            """));
    }

    // ---------------------------------------------------------------- regress catalog audit K1–K9 (catalog.ts), in SQL Server's catalog

    private static readonly string[] All = ["SELECT", "INSERT", "UPDATE", "DELETE", "ALTER", "REFERENCES", "CONTROL", "TAKE OWNERSHIP", "VIEW CHANGE TRACKING"];

    /// <summary>What core_app and core_reader may do on each regress table (privilege plan §2); anything else must be refused.</summary>
    private static readonly Dictionary<string, (string App, string Reader)> Plan = new(StringComparer.Ordinal)
    {
        ["subrogation_case"] = ("SIU", "S"), ["subrogation_party"] = ("SIU", "S"), ["subrogation_movement"] = ("SI", "S"),
        ["claim_recovery"] = ("SI", "S"), ["claim_payment"] = ("SI", "S"), ["claim"] = ("SIUD", "S"), ["policy"] = ("SIUD", "S"),
        ["policy_line"] = ("SIUD", "S"), ["party"] = ("SIUD", "S"), ["loss_event"] = ("SIUD", "S"), ["audit_log"] = ("SI", "S"),
        ["number_sequence"] = ("SIUD", "S"), ["accounting_period"] = ("SIU", "S"), ["schema_migration"] = ("S", "S"),
        ["app_user"] = ("SIUD", ""), ["session"] = ("SIUD", ""),
    };

    private static string Letters(IEnumerable<string> permissions) =>
        string.Concat(permissions.Select(p => p switch { "SELECT" => "S", "INSERT" => "I", "UPDATE" => "U", "DELETE" => "D", _ => $"[{p}]" }));

    private async Task<Dictionary<string, string>> EffectiveAsync(SqlConnection c, string user) =>
        (await c.QueryAsync<(string Table, string Permission)>($"""
            execute as user = N'{user}';
            select t.name, p.permission from sys.tables t
            cross apply (values {string.Join(", ", All.Select((p, i) => $"(N'{p}', {i})"))}) p(permission, n)
            where t.schema_id = schema_id(N'core') and t.name in @tables
              and has_perms_by_name(N'core.' + quotename(t.name), N'OBJECT', p.permission) = 1
            order by t.name, p.n;
            revert;
            """, new { tables = Plan.Keys.ToList() }))
            .GroupBy(r => r.Table).ToDictionary(g => g.Key, g => Letters(g.Select(r => r.Permission)), StringComparer.Ordinal);

    [Fact]
    public async Task K1_K2_the_roles_are_plain_database_roles_members_of_nothing_and_the_application_login_holds_only_core_app()
    {
        await using var c = await db.OpenAdminAsync();
        // K1: no server role and no fixed database role for the application's login or user.
        await using (var app = await db.OpenAppAsync())
        {
            Assert.Equal(0, await app.ExecuteScalarAsync<int>("select is_srvrolemember(N'sysadmin') + is_srvrolemember(N'serveradmin') + is_srvrolemember(N'securityadmin') + is_srvrolemember(N'dbcreator')"));
            Assert.Equal("core_app", string.Join(",", await app.QueryAsync<string>("""
                select r.name from sys.database_role_members m join sys.database_principals r on r.principal_id = m.role_principal_id
                where m.member_principal_id = user_id() order by r.name
                """)));
            Assert.Equal(0, await app.ExecuteScalarAsync<int>("select is_member(N'db_owner') + is_member(N'db_ddladmin') + is_member(N'db_securityadmin') + is_member(N'db_datawriter') + is_member(N'db_datareader')"));
        }
        // K2: core_app and core_reader are members of no role at all, however deep.
        Assert.Empty(await c.QueryAsync<string>("""
            select m.name + N' -> ' + r.name from sys.database_role_members x
            join sys.database_principals m on m.principal_id = x.member_principal_id join sys.database_principals r on r.principal_id = x.role_principal_id
            where m.name in (N'core_app', N'core_reader')
            """));
        Assert.Equal(["R", "R"], await c.QueryAsync<string>("select rtrim(type) from sys.database_principals where name in (N'core_app', N'core_reader')"));
    }

    /// <summary>
    /// Legacy core_reader read every table of core but users and sessions, those added later too, by the schema's default
    /// privileges (old db/README.md line 30). SQL Server has none: the migration runner grants it after every run (Migrator);
    /// this keeps that true for tables added later, such as 0011's (the session's pass table of 0009 is internal and left out).
    /// </summary>
    [Fact]
    public async Task Core_reader_reads_every_core_table_and_view_but_users_sessions_and_the_internal_pass()
    {
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync("""
            if database_principal_id(N'core_reader_probe') is null create user core_reader_probe without login;
            if is_rolemember(N'core_reader', N'core_reader_probe') = 0 alter role core_reader add member core_reader_probe;
            """);
        Assert.Empty(await c.QueryAsync<string>("""
            declare @objects table (name sysname);
            -- Read the catalogue as the owner: a user sees in sys.objects only what it has a right on.
            insert @objects select o.name from sys.objects o
            where o.schema_id = schema_id(N'core') and o.type in (N'U', N'V') and o.name not in (N'app_user', N'session', N'case_money_pass');
            execute as user = N'core_reader_probe';
            select name from @objects where coalesce(has_perms_by_name(N'core.' + quotename(name), N'OBJECT', N'SELECT'), 0) = 0 order by name;
            revert;
            """));
    }

    [Fact]
    public async Task K3_the_schema_belongs_to_the_owner_and_neither_role_may_alter_it_or_create_objects()
    {
        await using var c = await db.OpenAdminAsync();
        Assert.Equal("dbo", await c.ExecuteScalarAsync<string>("select user_name(principal_id) from sys.schemas where name = N'core'"));
        await c.ExecuteAsync("""
            if database_principal_id(N'core_reader_probe') is null create user core_reader_probe without login;
            if is_rolemember(N'core_reader', N'core_reader_probe') = 0 alter role core_reader add member core_reader_probe;
            """);
        foreach (var user in new[] { "core_app_rights_probe", "core_reader_probe" })
            Assert.Equal("0000", await c.ExecuteScalarAsync<string>($"""
                execute as user = N'{user}';
                select concat(has_perms_by_name(N'core', N'SCHEMA', N'ALTER'), has_perms_by_name(N'core', N'SCHEMA', N'CONTROL'),
                              has_perms_by_name(null, N'DATABASE', N'CREATE TABLE'), has_perms_by_name(null, N'DATABASE', N'CREATE PROCEDURE'));
                revert;
                """));
    }

    [Fact]
    public async Task K4_K5_every_privilege_of_core_app_and_core_reader_on_each_regress_table_is_exactly_the_plan_and_public_holds_nothing()
    {
        await using var c = await db.OpenAdminAsync();
        await c.ExecuteAsync("""
            if database_principal_id(N'core_reader_probe') is null create user core_reader_probe without login;
            if is_rolemember(N'core_reader', N'core_reader_probe') = 0 alter role core_reader add member core_reader_probe;
            """);
        var app = await EffectiveAsync(c, "core_app_rights_probe");
        var reader = await EffectiveAsync(c, "core_reader_probe");
        foreach (var (table, (wantApp, wantReader)) in Plan)
        {
            Assert.Equal($"{table}:{wantApp}", $"{table}:{app.GetValueOrDefault(table, "")}");
            Assert.Equal($"{table}:{wantReader}", $"{table}:{reader.GetValueOrDefault(table, "")}");
        }
        // K5: every table belongs to the schema's owner (no object owner of its own), and PUBLIC holds nothing on core.
        Assert.Empty(await c.QueryAsync<string>("select name from sys.tables where schema_id = schema_id(N'core') and principal_id is not null"));
        Assert.Empty(await c.QueryAsync<string>("""
            select p.permission_name from sys.database_permissions p
            where p.grantee_principal_id = database_principal_id(N'public')
              and ((p.class = 1 and object_schema_name(p.major_id) = N'core') or (p.class = 3 and p.major_id = schema_id(N'core')))
            """));
    }

    [Fact]
    public async Task K7_the_regress_procedures_and_functions_run_as_their_caller_and_core_app_may_execute_them()
    {
        string[] routines = ["next_number", "lock_subrogation_claim", "subrogation_totals", "subrogation_room", "assert_period_open", "record_case_money", "xact_lock"];
        await using var c = await db.OpenAdminAsync();
        var found = (await c.QueryAsync<(string Name, int? ExecuteAs, string Type)>("""
            select o.name, m.execute_as_principal_id, rtrim(o.type) from sys.objects o join sys.sql_modules m on m.object_id = o.object_id
            where o.schema_id = schema_id(N'core') and o.name in @routines
            """, new { routines })).ToList();
        Assert.Equal(routines.Order(StringComparer.Ordinal), found.Select(f => f.Name).Order(StringComparer.Ordinal));
        // No security definer: nothing runs as its owner.
        Assert.All(found, f => Assert.Null(f.ExecuteAs));
        var granted = (await c.QueryAsync<string>("""
            execute as user = N'core_app_rights_probe';
            select o.name from sys.objects o where o.schema_id = schema_id(N'core') and o.name in @routines
              and has_perms_by_name(N'core.' + quotename(o.name), N'OBJECT', case when o.type = N'P' then N'EXECUTE' else N'SELECT' end) = 1;
            revert;
            """, new { routines })).ToList();
        Assert.Equal(routines.Order(StringComparer.Ordinal), granted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task K9_the_regress_tables_have_exactly_the_planned_triggers_all_enabled_per_row_guards_and_audits()
    {
        await using var c = await db.OpenAdminAsync();
        // The legacy BEFORE triggers are INSTEAD OF / refusing AFTER triggers here (database/README.md); the deferred SG034 trigger of
        // claim_recovery is core.record_case_money (difference 1). No trigger is disabled, none fires on anything but row changes.
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "subrogation_case.subrogation_case_insert INSTEAD OF INSERT",
            "subrogation_case.subrogation_case_update AFTER UPDATE",
            "subrogation_case.subrogation_case_audit AFTER INSERT OR UPDATE OR DELETE",
            "subrogation_party.subrogation_party_guard AFTER UPDATE",
            "subrogation_party.subrogation_party_audit AFTER INSERT OR UPDATE OR DELETE",
            "subrogation_movement.subrogation_movement_guard INSTEAD OF INSERT",
            "subrogation_movement.subrogation_movement_audit AFTER INSERT OR UPDATE OR DELETE",
            "claim_recovery.claim_recovery_guard INSTEAD OF INSERT",
        }, await TriggersAsync(c, "subrogation_case", "subrogation_party", "subrogation_movement", "claim_recovery"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from sys.triggers where parent_class = 0"));
    }

    // ---------------------------------------------------------------- zero-row negative attempts N1–N6 (negative.ts)

    [Fact]
    public async Task N1_to_N6_core_app_is_refused_on_cases_money_audit_and_journal_and_may_add_audit_rows_with_nothing_moved()
    {
        await using var admin = await db.OpenAdminAsync();
        const string Fingerprint = """
            select concat((select count(*) from core.subrogation_case), N'|', (select count(*) from core.claim_recovery), N'|', (select count(*) from core.subrogation_movement),
                          N'|', (select count(*) from core.audit_log), N'|', (select coalesce(max(id), 0) from core.audit_log), N'|', (select count(*) from core.schema_migration),
                          N'|', ident_current(N'core.audit_log'), N'|', (select next_value from core.number_sequence where code = N'subrogation'))
            """;
        var before = await admin.ExecuteScalarAsync<string>(Fingerprint);
        var attempts = new (string Id, string Sql, string Expected)[]
        {
            ("N1", "delete from core.subrogation_case where 1 = 0", "42501"),
            ("N2a", "update core.claim_recovery set note = note where 1 = 0", "42501"),
            ("N2b", "delete from core.claim_recovery where 1 = 0", "42501"),
            ("N3", "update core.audit_log set action = action where 1 = 0", "42501"),
            ("N5", "insert core.audit_log (table_name, row_id, action) select table_name, row_id, action from core.audit_log where 1 = 0", "ok"),
            ("N6", "update core.schema_migration set name = name where 1 = 0", "42501"),
        };
        foreach (var (id, sql, expected) in attempts)
        {
            await using var c = await db.OpenAppAsync();
            await using var t = (SqlTransaction)await c.BeginTransactionAsync();
            var outcome = await LegacyDbCheck.CodeOf(() => c.ExecuteAsync(sql, transaction: t));
            await LegacyDbCheck.RollbackQuietlyAsync(t);
            Assert.Equal($"{id} {expected}", $"{id} {outcome}");
        }
        Assert.Equal(before, await admin.ExecuteScalarAsync<string>(Fingerprint));
    }
}
