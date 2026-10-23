using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Insurance.Tools.DataMigration;

/// <summary>
/// Copies the data of the old PostgreSQL database into a SQL Server database built by the migrations, in one SQL Server
/// transaction: all of it or nothing (a failure leaves the target as it was). The source is read in one read-only,
/// repeatable-read snapshot and never changed.
///
/// - First run ("initial") into a database without business data: the reference rows the migrations seeded (currencies,
///   classes, roles, settings, number series, products) are replaced by the source's, so every id and value is the
///   source's own, then every table is copied as it is: ids, history, audit log (with its ids), closed months.
/// - A repeated run ("repeat") adds only rows the target does not have yet, by primary key, and refuses (rolling back)
///   when a row it has differs from the source — the same import never makes duplicates and never overwrites.
/// - Rows are copied as stored, without the guard and audit triggers: they passed the old database's guards when they
///   were written, and a closed month would refuse its own history. Every foreign key and check constraint is then
///   validated over all rows (WITH CHECK CHECK CONSTRAINT), and Reconcile compares the result with the source.
/// - Not copied: sessions (everyone signs in again) and the migration journal (the target has its own). Password hashes
///   are copied: the scrypt format is the same and verified by the new code (Insurance.Domain.Common.Password).
/// </summary>
public sealed class PostgresImport(string postgresConnection, string sqlServerAdminConnection, Action<string> log)
{
    /// <summary>Source tables that are not copied.</summary>
    public static readonly IReadOnlySet<string> NotCopied = new HashSet<string>(StringComparer.Ordinal) { "session", "schema_migration" };

    /// <summary>
    /// Target tables that have no source counterpart. ri_commission_settlement (0011) is the old program's 0039 of PR #362, which the
    /// baseline database the import reads does not have: an import leaves that ledger empty.
    /// </summary>
    public static readonly IReadOnlySet<string> TargetOnly = new HashSet<string>(StringComparer.Ordinal) { "audit_config", "data_import", "schema_migration", "case_money_pass", "ri_commission_settlement" };

    /// <summary>
    /// Nullable target columns the old database never had: left null on imported rows (sanctions fingerprints and the reuse
    /// of a decision, migration 0010 — an imported screening has no fingerprint and is never reused; a GEL claim's
    /// occurrence-day snapshot and a set-off's rate, migration 0012 — the old program recorded every claim in its policy's
    /// currency, so an imported claim has no snapshot and an imported set-off keeps its rate only in its allocations).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> TargetOnlyColumns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["sanction_list_version"] = new HashSet<string>(StringComparer.Ordinal) { "content_sha256" },
        ["sanctions_screening"] = new HashSet<string>(StringComparer.Ordinal) { "subject_sha256", "matches_sha256", "list_sha256", "decision", "reuses_screening_id" },
        ["claim"] = new HashSet<string>(StringComparer.Ordinal) { "limit_gel", "deductible_gel", "limit_rate", "limit_rate_on" },
        ["claim_payment"] = new HashSet<string>(StringComparer.Ordinal) { "offset_rate", "offset_currency" },
        ["claim_payment_request"] = new HashSet<string>(StringComparer.Ordinal) { "offset_rate", "offset_currency" },
    };

    /// <summary>Tables the migrations fill with reference rows, replaced by the source's on the first run (children first).</summary>
    public static readonly IReadOnlyList<string> Seeded =
        ["product_text", "product_coverage", "product", "number_sequence", "setting", "role", "branch", "insurance_class", "currency"];

    public sealed record TableResult(string Table, int SourceRows, int Added, int AlreadyThere);
    public sealed record Result(string Mode, IReadOnlyList<TableResult> Tables);

    private sealed record Column(string Name, string SqlType, bool IsIdentity);

    public async Task<Result> RunAsync()
    {
        var started = DateTimeOffset.UtcNow;
        await using var pg = new NpgsqlConnection(postgresConnection);
        await pg.OpenAsync();
        await using var pgTx = await pg.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await pg.ExecuteAsync("set transaction read only", transaction: pgTx);

        await using var db = new SqlConnection(sqlServerAdminConnection);
        await db.OpenAsync();
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable);
        await db.ExecuteAsync("set xact_abort on", transaction: tx);

        var sourceTables = (await pg.QueryAsync<string>(
            "select table_name from information_schema.tables where table_schema = 'core' and table_type = 'BASE TABLE' order by table_name", transaction: pgTx))
            .Where(t => !NotCopied.Contains(t)).ToList();
        var targetTables = (await db.QueryAsync<string>("select name from sys.tables where schema_id = schema_id(N'core') order by name", transaction: tx))
            .Where(t => !TargetOnly.Contains(t) && !NotCopied.Contains(t)).ToList();
        var missing = sourceTables.Except(targetTables).Concat(targetTables.Except(sourceTables)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"the schemas differ: tables {string.Join(", ", missing)}");

        var previous = await db.ExecuteScalarAsync<int>("select count(*) from core.data_import", transaction: tx);
        var holding = new List<string>();
        foreach (var t in targetTables.Where(t => !Seeded.Contains(t)))
            if (await db.ExecuteScalarAsync<int?>($"select top (1) 1 from core.[{t}]", transaction: tx) is not null) holding.Add(t);
        var mode = previous == 0 ? "initial" : "repeat";
        if (mode == "initial" && holding.Count > 0)
            throw new InvalidOperationException($"the target already holds data that did not come from an import ({string.Join(", ", holding)}); use a new database");
        log($"mode: {mode}");

        // The reference rows seeded by the migrations give way to the source's, without audit (the triggers are off for the
        // statement; DDL inside the transaction, so a failure restores them).
        if (mode == "initial")
            foreach (var t in Seeded)
            {
                await db.ExecuteAsync($"alter table core.[{t}] disable trigger all", transaction: tx);
                await db.ExecuteAsync($"delete from core.[{t}]", transaction: tx);
                await db.ExecuteAsync($"alter table core.[{t}] enable trigger all", transaction: tx);
            }

        var results = new List<TableResult>();
        foreach (var table in targetTables)
            results.Add(await CopyTableAsync(pg, pgTx, db, tx, table, mode));

        // Every constraint over every row, as if the rows had been inserted one by one (marks them trusted).
        foreach (var table in targetTables)
            await db.ExecuteAsync($"alter table core.[{table}] with check check constraint all", transaction: tx, commandTimeout: 0);

        var source = new NpgsqlConnectionStringBuilder(postgresConnection);
        await db.ExecuteAsync("insert into core.data_import (source, started_at, mode, added) values (@source, @started, @mode, @added)", new
        {
            source = $"postgresql://{source.Host}:{source.Port}/{source.Database}",
            started,
            mode,
            added = JsonSerializer.Serialize(results.Where(r => r.Added > 0).ToDictionary(r => r.Table, r => r.Added)),
        }, tx);
        await tx.CommitAsync();
        await pgTx.CommitAsync();
        return new Result(mode, results);
    }

    private async Task<TableResult> CopyTableAsync(NpgsqlConnection pg, NpgsqlTransaction pgTx, SqlConnection db, SqlTransaction tx, string table, string mode)
    {
        var columns = (await db.QueryAsync<Column>("""
            select c.name as Name, t.name as SqlType, c.is_identity as IsIdentity
            from sys.columns c join sys.types t on t.user_type_id = c.user_type_id
            where c.object_id = object_id(@name) and c.is_computed = 0
            order by c.column_id
            """, new { name = $"core.{table}" }, tx))
            .Where(c => !TargetOnlyColumns.TryGetValue(table, out var only) || !only.Contains(c.Name)).ToList();
        var sourceColumns = (await pg.QueryAsync<(string Name, string Type, string IsGenerated)>("""
            select column_name, data_type, is_generated from information_schema.columns where table_schema = 'core' and table_name = @table
            """, new { table }, pgTx)).ToDictionary(c => c.Name, StringComparer.Ordinal);
        var absent = columns.Where(c => !sourceColumns.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        var unknown = sourceColumns.Values.Where(c => c.IsGenerated != "ALWAYS" && columns.All(t => t.Name != c.Name)).Select(c => c.Name).ToList();
        if (absent.Count > 0 || unknown.Count > 0)
            throw new InvalidOperationException($"core.{table}: columns differ (only in SQL Server: {string.Join(", ", absent)}; only in PostgreSQL: {string.Join(", ", unknown)})");

        var keyColumns = (await db.QueryAsync<string>("""
            select c.name from sys.indexes i join sys.index_columns ic on ic.object_id = i.object_id and ic.index_id = i.index_id
            join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
            where i.object_id = object_id(@name) and i.is_primary_key = 1 order by ic.key_ordinal
            """, new { name = $"core.{table}" }, tx)).ToList();
        var keyIndexes = keyColumns.Select(k => columns.FindIndex(c => c.Name == k)).ToArray();

        // What the target already has, by key, as comparable text (a repeated run).
        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mode == "repeat")
        {
            await using var cmd = new SqlCommand($"select {string.Join(", ", columns.Select(c => $"[{c.Name}]"))} from core.[{table}]", db, tx) { CommandTimeout = 0 };
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = Enumerable.Range(0, columns.Count).Select(i => Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i), columns[i].SqlType)).ToArray();
                existing[Canon.Row(keyIndexes.Select(i => values[i]))] = Canon.Row(values);
            }
        }

        var data = new DataTable();
        foreach (var c in columns) data.Columns.Add(c.Name, ClrType(c.SqlType));
        var sourceRows = 0;
        var already = 0;
        var conflicts = new List<string>();
        await using (var cmd = new NpgsqlCommand($"select {string.Join(", ", columns.Select(c => $"\"{c.Name}\""))} from core.\"{table}\"", pg, pgTx) { CommandTimeout = 0 })
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                sourceRows++;
                var values = Enumerable.Range(0, columns.Count).Select(i => Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i), columns[i].SqlType)).ToArray();
                var key = Canon.Row(keyIndexes.Select(i => values[i]));
                if (existing.TryGetValue(key, out var there))
                {
                    already++;
                    if (there != Canon.Row(values)) conflicts.Add(key);
                    continue;
                }
                data.Rows.Add(values.Select(v => v ?? DBNull.Value).ToArray());
            }
        }
        if (conflicts.Count > 0)
            throw new InvalidOperationException($"core.{table}: {conflicts.Count} rows differ from the source (first: {conflicts[0]}); the import changes nothing");

        if (data.Rows.Count > 0)
        {
            using var bulk = new SqlBulkCopy(db, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, tx)
            {
                DestinationTableName = $"core.[{table}]",
                BulkCopyTimeout = 0,
                BatchSize = 5000,
            };
            foreach (var c in columns) bulk.ColumnMappings.Add(c.Name, c.Name);
            await bulk.WriteToServerAsync(data);
        }
        log($"core.{table}: {sourceRows} rows in the source, {data.Rows.Count} added, {already} already there");
        return new TableResult(table, sourceRows, data.Rows.Count, already);
    }

    private static Type ClrType(string sqlType) => sqlType switch
    {
        "uniqueidentifier" => typeof(Guid),
        "date" => typeof(DateTime),
        "datetimeoffset" => typeof(DateTimeOffset),
        "decimal" or "numeric" => typeof(decimal),
        "bit" => typeof(bool),
        "int" => typeof(int),
        "bigint" => typeof(long),
        "smallint" => typeof(short),
        _ => typeof(string),
    };

    /// <summary>A value of either database as the target column holds it.</summary>
    public static object? Normalize(object? value, string sqlType) => value switch
    {
        null or DBNull => null,
        string[] array => JsonSerializer.Serialize(array, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        DateTime t when sqlType == "datetimeoffset" => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)),
        DateTime t when sqlType == "date" => t.Date,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        string s when sqlType is "nchar" or "char" => s.TrimEnd(),
        _ => value,
    };
}
