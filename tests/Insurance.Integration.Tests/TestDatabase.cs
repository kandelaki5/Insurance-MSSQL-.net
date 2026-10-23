using System.Security.Cryptography;
using Insurance.Application.Data;
using Microsoft.Data.SqlClient;

namespace Insurance.Integration.Tests;

/// <summary>
/// A fresh database on the local SQL Server, built by the real migrations and dropped afterwards. Its name
/// starts with ins_it_, and nothing else on the server is touched. The application side connects through a
/// login that is a member of core_app only, so every test also checks the application's rights.
/// The server comes from INSURANCE_TEST_SQL_MASTER (see .env.test.example).
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    public const string Prefix = "ins_it_";

    public string Name { get; } = $"{Prefix}{DateTime.UtcNow:yyyyMMddHHmmss}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
    public string AppLogin => Name + "_app";
    public string MasterConnection { get; } = MasterFromEnvironment();
    public string AdminConnection => new SqlConnectionStringBuilder(MasterConnection) { InitialCatalog = Name }.ConnectionString;
    public string AppConnection { get; private set; } = "";
    public List<string> Log { get; } = [];

    /// <summary>The clock of this database's application (TestDatabase.App and the web hosts over it): the system clock until a test sets it.</summary>
    public TestClock Clock { get; } = new();

    /// <summary>The application's options over this database: its connection and its clock.</summary>
    public DbOptions Options => new() { ConnectionString = AppConnection, Clock = Clock };

    public Db App => new(Options);

    public static string MasterFromEnvironment() =>
        Setting("INSURANCE_TEST_SQL_MASTER")
        ?? throw new InvalidOperationException("INSURANCE_TEST_SQL_MASTER is not set: these tests need a local SQL Server (see .env.test.example)");

    /// <summary>A test setting from the environment or, failing that, from .env.test (git-ignored).</summary>
    public static string? Setting(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            var file = FindUp(".env.test");
            if (file is not null)
                foreach (var line in File.ReadAllLines(file))
                    if (line.StartsWith(name + "=", StringComparison.Ordinal))
                        value = line[(name.Length + 1)..].Trim('"');
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? FindUp(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task InitializeAsync() => await CreateAsync(until: null);

    public async Task CreateAsync(string? until)
    {
        await DatabaseAdmin.CreateDatabaseAsync(MasterConnection, Name, Log.Add);
        await Migrator.MigrateAsync(AdminConnection, Log.Add, until);
        var password = "It1!" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await DatabaseAdmin.CreateAppLoginAsync(MasterConnection, Name, AppLogin, password, Log.Add);
        AppConnection = new SqlConnectionStringBuilder(MasterConnection) { InitialCatalog = Name, UserID = AppLogin, Password = password }.ConnectionString;
    }

    public async Task DisposeAsync() =>
        await DatabaseAdmin.DropTestDatabaseAsync(MasterConnection, Name, Prefix, [AppLogin]);

    public async Task<SqlConnection> OpenAdminAsync()
    {
        var c = new SqlConnection(AdminConnection);
        await c.OpenAsync();
        return c;
    }

    public async Task<SqlConnection> OpenAppAsync()
    {
        var c = new SqlConnection(AppConnection);
        await c.OpenAsync();
        return c;
    }
}
