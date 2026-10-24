using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Insurance.Application.Data;

/// <summary>
/// Applies database/migrations/*.sql (embedded in this assembly) that are not yet recorded in
/// core.schema_migration, each file in its own transaction together with its record, then re-asserts the
/// rights so the final privileges never depend on the order in which grants and migrations ran (legacy
/// scripts/migrate-core.ts): ledgers append-only and the journal read-only for core_app, every table but
/// users and sessions readable for core_reader, every audit trigger regenerated from the current columns.
/// Needs an administrative connection; the application role cannot change the schema.
/// </summary>
public static partial class Migrator
{
    /// <summary>Tables the application may only add to (legacy LEDGERS plus every append-only table of later migrations).</summary>
    public static readonly IReadOnlyList<string> Ledgers =
    [
        "audit_log", "premium_transaction", "payment", "payment_allocation", "installment", "installment_adjustment", "premium_cession",
        "claim_payment", "claim_payment_request", "claim_recovery", "claim_reserve", "claim_reserve_request", "party_risk_assessment",
        "premium_refund_request", "premium_refund_request_line", "receipt_reversal_request", "receipt_reversal_request_line", "subrogation_movement",
        "ri_commission_settlement",
    ];

    /// <summary>Tables the application may only read: the migration journal is written by this runner alone (legacy M1, 0031).</summary>
    public static readonly IReadOnlyList<string> ReadOnly = ["schema_migration", "audit_config", "data_import"];

    /// <summary>Tables the application may not touch at all: reached only through the modules that own them (0009, Codex R02).</summary>
    public static readonly IReadOnlyList<string> Closed = ["case_money_pass"];

    /// <summary>Tables the application may change but never remove rows from.</summary>
    public static readonly IReadOnlyList<string> NoDelete = ["accounting_period", "subrogation_case", "subrogation_party"];

    public sealed record Migration(string Name, string Sql);

    /// <summary>The migrations shipped with this build, in name order.</summary>
    public static IReadOnlyList<Migration> Embedded()
    {
        var assembly = typeof(Migrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => new Migration(n["Migrations.".Length..], Read(assembly, n)))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A file's batches: the text between lines that hold only GO.</summary>
    public static IReadOnlyList<string> Batches(string sql) =>
        GoLine().Split(sql).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();

    [GeneratedRegex(@"^[ \t]*GO[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    /// <param name="until">Stop after the migration whose name starts with this prefix (an older schema for upgrade tests).</param>
    public static async Task MigrateAsync(string adminConnectionString, Action<string> log, string? until = null, IReadOnlyList<Migration>? migrations = null)
    {
        await using var db = new SqlConnection(adminConnectionString);
        await db.OpenAsync();
        await CheckDatabaseAsync(db);

        await db.ExecuteAsync("if schema_id(N'core') is null exec (N'create schema core')");
        await db.ExecuteAsync("""
            if object_id(N'core.schema_migration') is null
              create table core.schema_migration (
                name       nvarchar(260) not null constraint schema_migration_pkey primary key,
                applied_at datetimeoffset(6) not null constraint schema_migration_applied_at_df default sysdatetimeoffset())
            """);
        var applied = (await db.QueryAsync<string>("select name from core.schema_migration")).ToHashSet(StringComparer.Ordinal);

        foreach (var migration in migrations ?? Embedded())
        {
            if (until is not null && string.CompareOrdinal(migration.Name[..Math.Min(until.Length, migration.Name.Length)], until) > 0) break;
            if (applied.Contains(migration.Name)) continue;
            await using var tx = (SqlTransaction)await db.BeginTransactionAsync();
            try
            {
                await db.ExecuteAsync("set xact_abort on", transaction: tx);
                foreach (var batch in Batches(migration.Sql))
                    await db.ExecuteAsync(batch, transaction: tx, commandTimeout: 600);
                await db.ExecuteAsync("insert into core.schema_migration (name) values (@name)", new { name = migration.Name }, tx);
                await tx.CommitAsync();
            }
            catch (SqlException e)
            {
                throw new InvalidOperationException($"Migration {migration.Name} failed: {e.Message}", e);
            }
            log($"applied {migration.Name}");
        }

        await ReassertAsync(db, log);
        log("up to date");
    }

    /// <summary>
    /// The database must compare text case-sensitively (as PostgreSQL does) and read committed rows with
    /// versioning (PostgreSQL read committed). Both are database settings, made by "create-database".
    /// </summary>
    private static async Task CheckDatabaseAsync(SqlConnection db)
    {
        var settings = await db.QuerySingleAsync<(string Collation, bool Rcsi, int Snapshot)>("""
            select convert(nvarchar(128), databasepropertyex(db_name(), 'Collation')), is_read_committed_snapshot_on, convert(int, snapshot_isolation_state)
            from sys.databases where name = db_name()
            """);
        if (!settings.Collation.Contains("_CS_", StringComparison.Ordinal))
            throw new InvalidOperationException($"Database collation {settings.Collation} is not case-sensitive; create the database with Latin1_General_100_CS_AS_SC (Insurance.Tools create-database).");
        if (!settings.Rcsi)
            throw new InvalidOperationException("READ_COMMITTED_SNAPSHOT is off; run: alter database <name> set read_committed_snapshot on (Insurance.Tools create-database does it).");
        if (settings.Snapshot != 1)
            throw new InvalidOperationException("ALLOW_SNAPSHOT_ISOLATION is off; run: alter database <name> set allow_snapshot_isolation on (Insurance.Tools create-database does it).");
    }

    private static async Task ReassertAsync(SqlConnection db, Action<string> log)
    {
        if (await db.ExecuteScalarAsync<int?>("select 1 from sys.database_principals where name = N'core_app' and type = N'R'") is null)
        {
            log("role core_app does not exist yet");
            return;
        }
        if (await db.ExecuteScalarAsync<int?>("select 1 from sys.procedures where object_id = object_id(N'core.refresh_audit_triggers')") is not null)
        {
            await db.ExecuteAsync("exec core.refresh_audit_triggers", commandTimeout: 600);
            log("audit triggers cover every current column");
        }

        var present = (await db.QueryAsync<string>("select name from sys.tables where schema_id = schema_id(N'core')")).ToHashSet(StringComparer.Ordinal);
        foreach (var table in Ledgers.Where(present.Contains))
            await db.ExecuteAsync($"revoke update, delete on core.{Quote(table)} from core_app");
        foreach (var table in NoDelete.Where(present.Contains))
            await db.ExecuteAsync($"revoke delete on core.{Quote(table)} from core_app");
        foreach (var table in Closed.Where(present.Contains))
            await db.ExecuteAsync($"deny select, insert, update, delete on core.{Quote(table)} to core_app");
        foreach (var table in ReadOnly.Where(present.Contains))
        {
            await db.ExecuteAsync($"revoke insert, update, delete on core.{Quote(table)} from core_app");
            await db.ExecuteAsync($"grant select on core.{Quote(table)} to core_app");
        }
        await db.ExecuteAsync("""
            declare @sql nvarchar(max) = (
              select string_agg(convert(nvarchar(max), N'grant select on core.' + quotename(o.name) + N' to core_reader;'), char(10))
              from sys.objects o
              where o.schema_id = schema_id(N'core') and o.type in (N'U', N'V') and o.name not in (N'app_user', N'session'));
            exec sys.sp_executesql @sql;
            revoke select on core.app_user from core_reader;
            revoke select on core.session from core_reader;
            """);

        // The effective rights, whatever grants them (a direct grant, public, another role): checked as a member of core_app.
        await db.ExecuteAsync("""
            if database_principal_id(N'core_app_rights_probe') is null create user core_app_rights_probe without login;
            if is_rolemember(N'core_app', N'core_app_rights_probe') = 0 alter role core_app add member core_app_rights_probe;
            """);
        var leaks = (await db.QueryAsync<string>("""
            execute as user = N'core_app_rights_probe';
            select t.name + N':' + p.permission
            from sys.tables t
            cross apply (values (N'UPDATE'), (N'DELETE'), (N'INSERT'), (N'SELECT')) p(permission)
            where t.schema_id = schema_id(N'core')
              and has_perms_by_name(N'core.' + quotename(t.name), N'OBJECT', p.permission) = 1;
            revert;
            """)).ToHashSet(StringComparer.Ordinal);
        var changeable = Ledgers.Where(present.Contains).SelectMany(t => new[] { $"{t}:UPDATE", $"{t}:DELETE" })
            .Concat(ReadOnly.Where(present.Contains).SelectMany(t => new[] { $"{t}:INSERT", $"{t}:UPDATE", $"{t}:DELETE" }))
            .Concat(NoDelete.Where(present.Contains).Select(t => $"{t}:DELETE"))
            .Where(leaks.Contains).ToList();
        changeable.AddRange(Closed.Where(present.Contains).SelectMany(t => new[] { "SELECT", "INSERT", "UPDATE", "DELETE" }.Select(p => $"{t}:{p}")).Where(leaks.Contains));
        if (changeable.Count > 0)
            throw new InvalidOperationException($"core_app can still change: {string.Join(", ", changeable)}");
        var unreadable = ReadOnly.Where(present.Contains).Where(t => !leaks.Contains($"{t}:SELECT")).ToList();
        if (unreadable.Count > 0)
            throw new InvalidOperationException($"core_app cannot read: {string.Join(", ", unreadable)}");
        log("ledger tables are append-only and the migration journal read-only for core_app");
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
