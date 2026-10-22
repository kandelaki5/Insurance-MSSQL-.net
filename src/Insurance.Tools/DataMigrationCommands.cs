using Insurance.Tools.DataMigration;

namespace Insurance.Tools;

/// <summary>
/// Data migration from the old PostgreSQL database (docs/MIGRATION.md). The source connection comes from
/// INSURANCE_PG_CONNECTION; it is only read. The target is the database of INSURANCE_ADMIN_CONNECTION.
/// </summary>
public static class DataMigrationCommands
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var source = Cli.Env("INSURANCE_PG_CONNECTION");
        var target = Cli.Env("INSURANCE_ADMIN_CONNECTION");
        if (args[0] == "import-postgres")
        {
            var result = await new PostgresImport(source, target, output.WriteLine).RunAsync();
            await output.WriteLineAsync($"import ({result.Mode}) committed: {result.Tables.Sum(t => t.Added)} rows added, {result.Tables.Sum(t => t.AlreadyThere)} already there");
            return 0;
        }
        var checks = await new Reconcile(source, target).RunAsync();
        foreach (var c in checks)
            await output.WriteLineAsync($"{(c.Equal ? "EQUAL" : "DIFFERENT")}  {c.Name}{(c.FirstDifference is null ? "" : "  — " + c.FirstDifference)}");
        var different = checks.Count(c => !c.Equal);
        await output.WriteLineAsync(different == 0 ? $"reconciliation: all {checks.Count} checks equal" : $"reconciliation: {different} of {checks.Count} checks differ");
        return different == 0 ? 0 : 1;
    }
}
