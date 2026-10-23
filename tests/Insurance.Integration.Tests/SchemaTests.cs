using Dapper;
using Insurance.Application.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>The schema on a real SQL Server: clean build, repeated migration, rights, audit, closed periods, rollback.</summary>
public sealed class SchemaTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    [Fact]
    public async Task Clean_build_applies_every_migration_and_records_it()
    {
        await using var c = await db.OpenAdminAsync();
        var applied = (await c.QueryAsync<string>("select name from core.schema_migration order by name")).ToList();
        Assert.Equal(Migrator.Embedded().Select(m => m.Name).ToList(), applied);
        // The 59 legacy tables (schema_migration included), core.audit_config (drives the generated audit triggers),
        // core.data_import (the record of imports from the old database), core.case_money_pass (SG034's pass, 0009) and
        // core.ri_commission_settlement (0011: the old program's 0039 of PR #362, after the baseline) and core.oidc_code_use (0014: the
        // embedded OpenID Connect provider's used codes, ADR 0003).
        Assert.Equal(64, await c.ExecuteScalarAsync<int>("select count(*) from sys.tables where schema_id = schema_id(N'core')"));
        Assert.Equal("Latin1_General_100_CS_AS_SC", await c.ExecuteScalarAsync<string>("select convert(nvarchar(128), databasepropertyex(db_name(), 'Collation'))"));
        Assert.True(await c.ExecuteScalarAsync<bool>("select is_read_committed_snapshot_on from sys.databases where name = db_name()"));
        // Reference data of the legacy migrations.
        Assert.Equal(39, await c.ExecuteScalarAsync<int>("select count(*) from core.insurance_class"));
        Assert.Equal(7, await c.ExecuteScalarAsync<int>("select count(*) from core.role"));
        Assert.Equal(21, await c.ExecuteScalarAsync<int>("select count(*) from core.setting"));
        Assert.Equal(7, await c.ExecuteScalarAsync<int>("select count(*) from core.number_sequence where code in (N'contract', N'policy:CASCO', N'claim', N'complaint', N'treaty', N'aml_case', N'subrogation')"));
        Assert.Equal(7, await c.ExecuteScalarAsync<int>("select count(*) from core.product_coverage"));
        Assert.Equal(8, await c.ExecuteScalarAsync<int>("select count(*) from core.product_text"));
    }

    [Fact]
    public async Task Repeated_migration_changes_nothing()
    {
        var log = new List<string>();
        await Migrator.MigrateAsync(db.AdminConnection, log.Add);
        Assert.DoesNotContain(log, l => l.StartsWith("applied ", StringComparison.Ordinal));
        Assert.Contains("up to date", log);
        await using var c = await db.OpenAdminAsync();
        Assert.Equal(Migrator.Embedded().Count, await c.ExecuteScalarAsync<int>("select count(*) from core.schema_migration"));
    }

    [Theory]
    [InlineData("update core.premium_transaction set amount = amount")]
    [InlineData("delete from core.payment")]
    [InlineData("update core.payment_allocation set amount = amount")]
    [InlineData("delete from core.installment")]
    [InlineData("delete from core.audit_log")]
    [InlineData("update core.audit_log set action = action")]
    [InlineData("insert core.schema_migration (name) values (N'9999_fake.sql')")]
    [InlineData("delete from core.schema_migration")]
    [InlineData("delete from core.accounting_period")]
    [InlineData("delete from core.claim_payment")]
    [InlineData("update core.subrogation_movement set note = note")]
    [InlineData("create table core.intruder (id int)")]
    [InlineData("alter table core.policy add intruder int")]
    [InlineData("drop trigger core.payment_guard")]
    [InlineData("alter table core.payment disable trigger all")]
    [InlineData("alter role db_owner add member core_app_rights_probe")]
    public async Task Application_role_cannot_change_ledgers_journal_or_schema(string statement)
    {
        await using var c = await db.OpenAppAsync();
        var e = await Assert.ThrowsAsync<SqlException>(() => c.ExecuteAsync(statement));
        Assert.Contains(e.Number, new[] { 229, 230, 262, 1088, 3701, 4902, 15151, 15247, 2760 });
    }

    [Fact]
    public async Task Application_role_reads_the_journal()
    {
        await using var c = await db.OpenAppAsync();
        Assert.True(await c.ExecuteScalarAsync<int>("select count(*) from core.schema_migration") > 0);
    }

    [Fact]
    public async Task Audit_records_who_changed_what_and_skips_password_and_no_op()
    {
        var actor = await Fixtures.UserAsync(db);
        var partyId = await db.App.AsUserAsync(actor, async tx =>
        {
            var id = await Fixtures.PersonAsync(tx);
            await tx.ExecuteAsync("update core.party set phone = N'+995 555 000 000' where id = @id", new { id });
            await tx.ExecuteAsync("update core.party set phone = N'+995 555 000 000' where id = @id", new { id });
            await tx.ExecuteAsync("update core.app_user set password_hash = N'scrypt$11$11' where id = @actor", new { actor });
            return id;
        });
        await using var c = await db.OpenAdminAsync();
        var rows = (await c.QueryAsync<(string Action, Guid? UserId, string? OldRow, string? NewRow)>(
            "select action, user_id, old_row, new_row from core.audit_log where table_name = N'party' and row_id = @id order by id",
            new { id = partyId.ToString() })).ToList();
        Assert.Equal(["insert", "update"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal(actor, r.UserId));
        Assert.Contains("\"phone\":\"+995 555 000 000\"", rows[1].NewRow);
        Assert.Contains($"\"id\":\"{partyId}\"", rows[0].NewRow);
        Assert.Contains("\"segment\":\"retail\"", rows[0].NewRow);
        // The password hash never reaches the log; a password-only change is therefore no change at all.
        var userRows = (await c.QueryAsync<string?>("select coalesce(old_row, '') + coalesce(new_row, '') from core.audit_log where table_name = N'app_user'")).ToList();
        Assert.All(userRows, r => Assert.DoesNotContain("password_hash", r));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.audit_log where table_name = N'app_user' and action = N'update' and row_id = @id", new { id = actor.ToString() }));
    }

    [Fact]
    public async Task Movement_in_a_closed_month_is_refused_and_the_month_cannot_reopen()
    {
        var actor = await Fixtures.UserAsync(db);
        var month = new DateOnly(2031, 3, 1);
        var policy = await db.App.AsUserAsync(actor, tx => Fixtures.ActivePolicyAsync(tx, actor, new DateOnly(2031, 3, 5)));
        await db.App.AsUserAsync(actor, tx => tx.ExecuteAsync(
            "insert core.accounting_period (period_start, status, closed_at, closed_by) values (@month, N'closed', sysdatetimeoffset(), @actor)", new { month, actor }));

        var refused = await Assert.ThrowsAsync<SqlException>(() => db.App.AsUserAsync(actor, tx => Fixtures.ReceiptAsync(tx, new DateOnly(2031, 3, 31), 100m)));
        var error = DbError.FromSql(refused)!;
        Assert.Equal(DbError.CheckViolation, error.Code);
        Assert.Null(error.Constraint);
        Assert.Contains("period 2031-03 is closed", error.Message);

        // The next month is open.
        await db.App.AsUserAsync(actor, tx => Fixtures.ReceiptAsync(tx, new DateOnly(2031, 4, 1), 100m));

        foreach (var statement in new[]
                 {
                     "update core.accounting_period set status = N'open', closed_at = null, closed_by = null where period_start = @month",
                     "update core.accounting_period set closed_at = sysdatetimeoffset() where period_start = @month",
                     "update core.accounting_period set period_start = dateadd(month, 1, period_start) where period_start = @month",
                 })
        {
            var e = await Assert.ThrowsAsync<SqlException>(() => db.App.AsUserAsync(actor, tx => tx.ExecuteAsync(statement, new { month })));
            Assert.Equal(DbError.CheckViolation, DbError.FromSql(e)!.Code);
        }
        // An update that changes nothing passes.
        await db.App.AsUserAsync(actor, tx => tx.ExecuteAsync("update core.accounting_period set status = status where period_start = @month", new { month }));
        _ = policy;
    }

    [Fact]
    public async Task A_failed_statement_rolls_the_whole_transaction_back()
    {
        var actor = await Fixtures.UserAsync(db);
        var taxId = Fixtures.RandomTaxId();
        await Assert.ThrowsAsync<SqlException>(() => db.App.AsUserAsync(actor, async tx =>
        {
            await Fixtures.PersonAsync(tx, taxId);
            await Fixtures.PersonAsync(tx, taxId); // unique tax id
            return 0;
        }));
        await using var c = await db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.party where tax_id = @taxId", new { taxId }));
    }

    [Fact]
    public async Task Unique_and_check_violations_map_to_the_legacy_codes()
    {
        var actor = await Fixtures.UserAsync(db);
        var taxId = Fixtures.RandomTaxId();
        await db.App.AsUserAsync(actor, tx => Fixtures.PersonAsync(tx, taxId));
        var dup = await Assert.ThrowsAsync<SqlException>(() => db.App.AsUserAsync(actor, tx => Fixtures.PersonAsync(tx, taxId)));
        Assert.Equal((DbError.UniqueViolation, "party_tax_id_key"), (DbError.FromSql(dup)!.Code, DbError.FromSql(dup)!.Constraint));
        var bad = await Assert.ThrowsAsync<SqlException>(() => db.App.AsUserAsync(actor, tx => Fixtures.PersonAsync(tx, "123")));
        Assert.Equal((DbError.CheckViolation, "party_tax_id_format"), (DbError.FromSql(bad)!.Code, DbError.FromSql(bad)!.Constraint));
    }

    [Fact]
    public async Task Georgian_text_and_case_sensitive_keys_are_kept()
    {
        var actor = await Fixtures.UserAsync(db);
        await using var c = await db.OpenAppAsync();
        var name = await c.ExecuteScalarAsync<string>("select name_ka from core.insurance_class where code = N'5.1'");
        Assert.Equal("სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", name);
        // Policy numbers differing only in case are different numbers, as in PostgreSQL.
        await db.App.AsUserAsync(actor, async tx =>
        {
            var party = await Fixtures.PersonAsync(tx);
            await tx.ExecuteAsync("insert core.contract (number, signed_on, policyholder_id) values (N'case-x', '2030-01-01', @party), (N'CASE-X', '2030-01-01', @party)", new { party });
            return 0;
        });
        Assert.Equal(2, await c.ExecuteScalarAsync<int>("select count(*) from core.contract where lower(number) = N'case-x'"));
    }

    [Fact]
    public async Task Number_series_refuses_when_it_runs_out()
    {
        var actor = await Fixtures.UserAsync(db);
        var series = "it:" + Guid.NewGuid().ToString("N")[..8];
        await db.App.AsUserAsync(actor, tx => tx.ExecuteAsync("insert core.number_sequence (code, prefix, next_value, padding) values (@series, N'T', 99, 2)", new { series }));
        Assert.Equal("T99", await db.App.AsUserAsync(actor, tx => tx.NextNumberAsync(series)));
        var e = await Assert.ThrowsAsync<SqlException>(() => db.App.AsUserAsync(actor, tx => tx.NextNumberAsync(series)));
        Assert.Equal("NU001", DbError.FromSql(e)!.Code);
        Assert.Null(await db.App.AsUserAsync(actor, tx => tx.NextNumberAsync("it:missing")));
        await using var c = await db.OpenAppAsync();
        Assert.Equal(100, await c.ExecuteScalarAsync<long>("select next_value from core.number_sequence where code = @series", new { series }));
    }
}
