using Insurance.Application.Data;

namespace Insurance.Tools;

public static class Cli
{
    private const string Usage = """
        Insurance.Tools <command> [options]

          create-database <name>                 creates a new, empty database with the required settings
                                                 (case-sensitive collation, row-versioned read committed)
          migrate [--until <prefix>]             applies the migrations not yet applied and re-asserts the rights
          create-app-login <login>               creates a SQL login + user in role core_app in the database of
                                                 INSURANCE_ADMIN_CONNECTION; the password is read from INSURANCE_APP_PASSWORD
          seed-admin <username> <full name>      creates (or resets the password of) an administrator; the password
                                                 is read from INSURANCE_ADMIN_PASSWORD
          import-postgres                        copies the data of the old PostgreSQL database (see docs/MIGRATION.md)
          reconcile                              compares the old and the new database (see docs/MIGRATION.md)
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "create-database" when args.Length == 2:
                    await DatabaseAdmin.CreateDatabaseAsync(Env("INSURANCE_MASTER_CONNECTION"), args[1], output.WriteLine);
                    return 0;
                case "migrate":
                    var until = args.Length == 3 && args[1] == "--until" ? args[2] : null;
                    await Migrator.MigrateAsync(Env("INSURANCE_ADMIN_CONNECTION"), output.WriteLine, until);
                    return 0;
                case "create-app-login" when args.Length == 2:
                    await DatabaseAdmin.CreateAppLoginAsync(Env("INSURANCE_MASTER_CONNECTION"), new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Env("INSURANCE_ADMIN_CONNECTION")).InitialCatalog, args[1], Env("INSURANCE_APP_PASSWORD"), output.WriteLine);
                    return 0;
                case "seed-admin" when args.Length == 3:
                    await DatabaseCommands.SeedAdminAsync(Env("INSURANCE_ADMIN_CONNECTION"), args[1], args[2], Env("INSURANCE_ADMIN_PASSWORD"), output.WriteLine);
                    return 0;
                default:
                    if (args.FirstOrDefault() is "import-postgres" or "reconcile")
                        return await DataMigrationCommands.RunAsync(args, output, error);
                    await error.WriteLineAsync(Usage);
                    return 2;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.SqlClient.SqlException or ArgumentException)
        {
            await error.WriteLineAsync(e.Message);
            return 1;
        }
    }

    public static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set");
}
