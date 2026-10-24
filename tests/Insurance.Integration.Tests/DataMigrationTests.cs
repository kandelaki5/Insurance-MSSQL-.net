using System.Text.Json;
using Dapper;
using Insurance.Application.Services;
using Insurance.Tools.DataMigration;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// The data migration on real databases: a world built in PostgreSQL by the old program's own actions
/// (tests/parity/legacy-world/build-world.ts) is imported into a fresh SQL Server database and reconciled. Needs
/// INSURANCE_TEST_PG_WORLD; without it every test here is reported as skipped, never as passed.
/// </summary>
public sealed class DataMigrationTests
{
    private static readonly string? World = TestDatabase.Setting("INSURANCE_TEST_PG_WORLD");

    private static async Task<TestDatabase> FreshAsync()
    {
        var db = new TestDatabase();
        await db.InitializeAsync();
        return db;
    }

    private static Task<PostgresImport.Result> ImportAsync(TestDatabase db, string? source = null) => new PostgresImport(source ?? World!, db.AdminConnection, _ => { }).RunAsync();

    private static async Task<IReadOnlyList<Reconcile.Check>> ReconcileAsync(TestDatabase db, string? source = null) => await new Reconcile(source ?? World!, db.AdminConnection).RunAsync();

    private static async Task PgAdminAsync(string sql)
    {
        await using var pg = new Npgsql.NpgsqlConnection(World);
        await pg.OpenAsync();
        await pg.ExecuteAsync(sql);
    }

    private static string Login(string role, string password) =>
        new Npgsql.NpgsqlConnectionStringBuilder(World) { Username = role, Password = password, Pooling = false }.ConnectionString;

    private static string Script(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "postgres", name));

    [SkippableFact]
    public async Task Import_reconciles_every_table_figure_and_relation()
    {
        Skip.If(World is null, "INSURANCE_TEST_PG_WORLD is not set");
        var db = await FreshAsync();
        try
        {
            var result = await ImportAsync(db);
            Assert.Equal("initial", result.Mode);
            Assert.True(result.Tables.Sum(t => t.Added) > 0);
            var checks = await ReconcileAsync(db);
            Assert.All(checks, c => Assert.True(c.Equal, $"{c.Name}: {c.FirstDifference}"));
            Assert.True(checks.Count > 60);

            await using var c = await db.OpenAdminAsync();
            // The closed months are closed here too, and their history is there.
            Assert.True(await c.ExecuteScalarAsync<int>("select count(*) from core.accounting_period where status = N'closed'") > 0);
            // Every constraint was validated over the imported rows (trusted).
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from sys.foreign_keys where schema_id = schema_id(N'core') and is_not_trusted = 1"));
            // New rows continue the imported identities (audit log ids are kept).
            var maxAudit = await c.ExecuteScalarAsync<long>("select max(id) from core.audit_log");
            await c.ExecuteAsync("update core.setting set value = value + N'' where [key] = N'company_name'");
            await c.ExecuteAsync("update core.setting set value = N'შემოწმება' where [key] = N'company_name'");
            Assert.True(await c.ExecuteScalarAsync<long>("select max(id) from core.audit_log") > maxAudit);
        }
        finally { await db.DisposeAsync(); }
    }

    [SkippableFact]
    public async Task Repeated_import_adds_nothing_and_refuses_a_row_that_differs()
    {
        Skip.If(World is null, "INSURANCE_TEST_PG_WORLD is not set");
        var db = await FreshAsync();
        try
        {
            await ImportAsync(db);
            var again = await ImportAsync(db);
            Assert.Equal("repeat", again.Mode);
            Assert.Equal(0, again.Tables.Sum(t => t.Added));
            Assert.All(await ReconcileAsync(db), c => Assert.True(c.Equal, c.Name));

            // Negative control: a premium row changed in the target is reported by the reconciliation...
            await using var c = await db.OpenAdminAsync();
            var row = await c.ExecuteScalarAsync<Guid>("select top (1) id from core.premium_transaction order by id");
            await c.ExecuteAsync("alter table core.premium_transaction disable trigger all; update core.premium_transaction set amount = amount + 0.01 where id = @row; alter table core.premium_transaction enable trigger all;", new { row });
            var checks = await ReconcileAsync(db);
            Assert.Contains(checks, x => !x.Equal && x.Name.StartsWith("premium by line", StringComparison.Ordinal));
            // ...and a repeated import refuses the difference and writes nothing.
            var imports = await c.ExecuteScalarAsync<int>("select count(*) from core.data_import");
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(db));
            Assert.Contains("core.premium_transaction: 1 rows differ", e.Message);
            Assert.Equal(imports, await c.ExecuteScalarAsync<int>("select count(*) from core.data_import"));
        }
        finally { await db.DisposeAsync(); }
    }

    [SkippableFact]
    public async Task A_failing_import_leaves_the_target_as_it_was()
    {
        Skip.If(World is null, "INSURANCE_TEST_PG_WORLD is not set");
        var db = await FreshAsync();
        try
        {
            await using var c = await db.OpenAdminAsync();
            // A target column too narrow for a source value makes the bulk copy fail halfway (after earlier tables were copied).
            await c.ExecuteAsync("alter table core.policy drop constraint policy_number_key; alter table core.policy alter column number nvarchar(3) not null;");
            await Assert.ThrowsAnyAsync<Exception>(() => ImportAsync(db));
            foreach (var table in new[] { "party", "app_user", "audit_log", "contract", "data_import", "exchange_rate" })
                Assert.Equal(0, await c.ExecuteScalarAsync<int>($"select count(*) from core.[{table}]"));
            // The seeded reference rows the import would have replaced are still the migrations' own.
            Assert.Equal(21, await c.ExecuteScalarAsync<int>("select count(*) from core.setting"));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.product where code = N'CASCO'"));
        }
        finally { await db.DisposeAsync(); }
    }

    [SkippableFact]
    public async Task A_target_with_its_own_data_is_refused()
    {
        Skip.If(World is null, "INSURANCE_TEST_PG_WORLD is not set");
        var db = await FreshAsync();
        try
        {
            await Fixtures.UserAsync(db);
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(db));
            Assert.Contains("already holds data", e.Message);
        }
        finally { await db.DisposeAsync(); }
    }

    [SkippableFact]
    public async Task An_imported_password_hash_made_by_the_old_code_signs_in()
    {
        Skip.If(World is null, "INSURANCE_TEST_PG_WORLD is not set");
        var db = await FreshAsync();
        try
        {
            await ImportAsync(db);
            // A hash the old Node code made (tests/parity/cases/common.json), set on an imported user.
            using var cases = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "parity", "common.json")));
            var sample = cases.RootElement.GetProperty("passwordHashes").EnumerateArray().First(x => x.GetProperty("password").GetString()!.Length > 3);
            await using var c = await db.OpenAdminAsync();
            var username = await c.ExecuteScalarAsync<string>("select top (1) username from core.app_user where is_active = 1 order by username");
            await c.ExecuteAsync("update core.app_user set password_hash = @hash where username = @username", new { hash = sample.GetProperty("stored").GetString(), username });
            var auth = new Auth(db.App);
            Assert.NotNull(await auth.LoginAsync(username!, sample.GetProperty("password").GetString()!, "test"));
            Assert.Null(await auth.LoginAsync(username!, "wrong-password", "test"));
            // Sessions are not imported: everyone signs in again.
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.session"));
        }
        finally { await db.DisposeAsync(); }
    }

    /// <summary>
    /// Codex R06: the migration's documented PostgreSQL reader (database/postgres/migration-reader.sql), and only its rights,
    /// imports and reconciles the whole world, cannot write, and is removed by its drop script. The old core_reader, which
    /// docs/MIGRATION.md first named, cannot: it does not see core.app_user, so the import refuses before writing anything.
    /// </summary>
    [SkippableFact]
    public async Task R06_the_documented_migration_reader_alone_imports_and_reconciles_and_core_reader_is_not_enough()
    {
        Skip.If(World is null, "INSURANCE_TEST_PG_WORLD is not set");
        const string reader = "insurance_migration_reader", oldReader = "ins_it_core_reader_login";
        var password = "Pw-" + Guid.NewGuid().ToString("N");
        await PgAdminAsync($"""
            do $$ begin
              if exists (select from pg_roles where rolname = '{reader}') then execute 'drop owned by {reader}; drop role {reader}'; end if;
              if exists (select from pg_roles where rolname = '{oldReader}') then execute 'drop owned by {oldReader}; drop role {oldReader}'; end if;
            end $$;
            """);
        await PgAdminAsync(Script("migration-reader.sql"));
        // The old core_reader's rights as the old db/README.md (lines 27–32) and migration 0017 give them, on a login of its own.
        await PgAdminAsync($"""
            alter role {reader} password '{password}';
            create role {oldReader} login password '{password}';
            grant usage on schema core to {oldReader};
            grant select on all tables in schema core to {oldReader};
            revoke select on core.app_user, core.session from {oldReader};
            revoke select on core.sanctions_screening from {oldReader};
            alter role {oldReader} set default_transaction_read_only = on;
            """);
        try
        {
            var asReader = Login(reader, password);
            await using (var pg = new Npgsql.NpgsqlConnection(asReader))
            {
                await pg.OpenAsync();
                Assert.True(await pg.ExecuteScalarAsync<int>("select count(*) from core.app_user") > 0);
                foreach (var sql in new[] { "update core.setting set value = value", "select count(*) from core.session" })
                    await Assert.ThrowsAsync<Npgsql.PostgresException>(() => pg.ExecuteAsync(sql));
            }

            var db = await FreshAsync();
            try
            {
                var result = await ImportAsync(db, asReader);
                Assert.Equal("initial", result.Mode);
                Assert.All(await ReconcileAsync(db, asReader), c => Assert.True(c.Equal, $"{c.Name}: {c.FirstDifference}"));
            }
            finally { await db.DisposeAsync(); }

            // Negative control: core_reader's rights stop the import before it writes anything.
            var other = await FreshAsync();
            try
            {
                var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(other, Login(oldReader, password)));
                Assert.Contains("app_user", e.Message);
                await using var c = await other.OpenAdminAsync();
                Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.data_import"));
            }
            finally { await other.DisposeAsync(); }
        }
        finally
        {
            await PgAdminAsync(Script("migration-reader-drop.sql") + $"\ndrop owned by {oldReader}; drop role if exists {oldReader};");
        }
        await using var check = new Npgsql.NpgsqlConnection(World);
        await check.OpenAsync();
        Assert.Equal(0, await check.ExecuteScalarAsync<int>($"select count(*) from pg_roles where rolname in ('{reader}', '{oldReader}')"));
    }
}
