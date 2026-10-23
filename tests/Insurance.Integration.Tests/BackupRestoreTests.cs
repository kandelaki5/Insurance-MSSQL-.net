using Dapper;
using Insurance.Application.Data;
using Insurance.Integration.Tests.Policies;
using Insurance.Tools;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// Backup and restore with the README's procedure ("სარეზერვო ასლი და აღდგენა"; legacy scripts/recovery-observer.ts and
/// history-fingerprint.ts), on a fresh test database with fictional rows written by the real actions: BACKUP DATABASE … WITH
/// CHECKSUM, RESTORE VERIFYONLY, RESTORE under a new ins_it_ name with its files moved, then the read-only <c>fingerprint</c>
/// command on the source and on the restored copy — every table of core with the same row count and the same SHA-256. Negative
/// control: one changed column of one row in the copy changes that table's fingerprint (and only that table).
/// The restored database and the backup file are removed afterwards.
/// </summary>
public sealed class BackupRestoreTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    [Fact]
    public async Task A_backup_restored_under_another_name_has_the_same_fingerprint_as_its_source_and_one_changed_row_differs()
    {
        // Fictional rows through the real actions: a policy approved, a receipt recorded on it, a cancellation.
        var w = new World(db);
        var admin = await w.UserAsync();
        var start = new DateOnly(2026, 1, 1);
        var p = await w.ActivePolicyAsync(admin, await w.ClientAsync(), start);
        Assert.NotNull((await w.PayAsync(admin, p.Id, "100.00", start.AddDays(3), reference: "გადახდა")).Redirect);
        Assert.NotNull((await w.CancelAsync(admin, p.Id, start.AddDays(100))).Redirect);

        var source = await Fingerprint.ReadAsync(db.AdminConnection);
        Assert.Contains(source, t => t.Name == "premium_transaction" && t.Rows == 2);
        Assert.Contains(source, t => t.Name == "audit_log" && t.Rows > 0);
        // Reading changed nothing: a second read is the same.
        Assert.Equal(source, await Fingerprint.ReadAsync(db.AdminConnection));

        // The command prints the same, one line per table and one over all; exit 0; no column value.
        var run = await ToolsProcess.RunAsync(new Dictionary<string, string?> { ["INSURANCE_ADMIN_CONNECTION"] = db.AdminConnection }, "fingerprint");
        Assert.True(run.Exit == 0, run.Error);
        Assert.Equal(Fingerprint.Lines(source), run.Output.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')));
        Assert.DoesNotContain("გადახდა", run.Output, StringComparison.Ordinal);

        var restored = $"{TestDatabase.Prefix}restore_{Guid.NewGuid().ToString("N")[..8]}";
        await using var master = new SqlConnection(db.MasterConnection);
        await master.OpenAsync();
        var (backupDir, dataDir, logDir) = await master.QuerySingleAsync<(string, string, string)>("""
            select convert(nvarchar(4000), serverproperty('InstanceDefaultBackupPath')), convert(nvarchar(4000), serverproperty('InstanceDefaultDataPath')),
                   convert(nvarchar(4000), serverproperty('InstanceDefaultLogPath'))
            """);
        static string Join(string dir, string file) => dir.TrimEnd('/', '\\') + (dir.Contains('\\') ? "\\" : "/") + file;
        var file = Join(backupDir, $"{db.Name}.bak");
        try
        {
            await master.ExecuteAsync("backup database @name to disk = @file with checksum, init", new { name = db.Name, file }, commandTimeout: 600);
            await master.ExecuteAsync("restore verifyonly from disk = @file with checksum", new { file }, commandTimeout: 600);
            var files = (await master.QueryAsync("restore filelistonly from disk = @file", new { file }, commandTimeout: 600))
                .Select(r => ((string)r.LogicalName, (string)r.Type)).ToList();
            var data = files.Single(f => f.Item2 == "D").Item1;
            var log = files.Single(f => f.Item2 == "L").Item1;
            await master.ExecuteAsync("""
                restore database @restored from disk = @file
                with checksum, move @data to @dataFile, move @log to @logFile, recovery
                """, new { restored, file, data, dataFile = Join(dataDir, $"{restored}.mdf"), log, logFile = Join(logDir, $"{restored}_log.ldf") }, commandTimeout: 600);

            var copy = new SqlConnectionStringBuilder(db.MasterConnection) { InitialCatalog = restored }.ConnectionString;
            var restoredPrint = await Fingerprint.ReadAsync(copy);
            Assert.Equal(source.Select(t => t.Name), restoredPrint.Select(t => t.Name));
            Assert.Equal(source, restoredPrint);
            Assert.Equal(Fingerprint.Lines(source), Fingerprint.Lines(restoredPrint));

            // Negative control: one column of one row changed in the copy — that table's fingerprint differs, its count does not.
            await using (var c = new SqlConnection(copy))
            {
                await c.OpenAsync();
                await c.ExecuteAsync("update core.payment set reference = N'შეცვლილი' where reference = N'გადახდა'");
            }
            var changed = await Fingerprint.ReadAsync(copy);
            Assert.NotEqual(Fingerprint.Lines(source).Last(), Fingerprint.Lines(changed).Last());
            var payment = (source.Single(t => t.Name == "payment"), changed.Single(t => t.Name == "payment"));
            Assert.Equal(payment.Item1.Rows, payment.Item2.Rows);
            Assert.NotEqual(payment.Item1.Sha256, payment.Item2.Sha256);
            // Only that table moved; every other table as in the source (core.payment has no audit trigger: it is a ledger the
            // application only inserts into; the server's administrator changed it here, in the copy).
            Assert.Equal(["payment"], source.Zip(changed).Where(x => x.First != x.Second).Select(x => x.First.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            await DatabaseAdmin.DropTestDatabaseAsync(db.MasterConnection, restored, TestDatabase.Prefix, []);
            try
            {
                await master.ExecuteAsync("exec master.dbo.xp_delete_file 0, @file", new { file });
            }
            catch (SqlException)
            {
                // The file stays in the server's backup folder; it holds fictional rows only.
            }
        }
        // The restored database is gone.
        Assert.Equal(0, await master.ExecuteScalarAsync<int>("select count(*) from sys.databases where name = @restored", new { restored }));
    }
}
