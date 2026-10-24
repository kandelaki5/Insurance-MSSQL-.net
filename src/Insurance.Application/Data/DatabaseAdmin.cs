using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Insurance.Application.Data;

/// <summary>Server-level administration: a new database with the required settings, and the application's login.</summary>
public static partial class DatabaseAdmin
{
    /// <summary>Case-sensitive, accent-sensitive, supplementary characters: text compares as in PostgreSQL.</summary>
    public const string Collation = "Latin1_General_100_CS_AS_SC";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,99}$")]
    private static partial Regex SafeName();

    public static void CheckName(string name, string what)
    {
        if (!SafeName().IsMatch(name)) throw new ArgumentException($"{what} '{name}' must be letters, digits and _ (starting with a letter)");
    }

    /// <summary>Creates a new database for the application; refuses an existing one (nothing is reset or reused).</summary>
    public static async Task CreateDatabaseAsync(string masterConnection, string name, Action<string> log)
    {
        CheckName(name, "Database name");
        await using var db = new SqlConnection(masterConnection);
        await db.OpenAsync();
        if (await db.ExecuteScalarAsync<int?>("select 1 from sys.databases where name = @name", new { name }) is not null)
            throw new InvalidOperationException($"Database {name} already exists; it is not changed");
        await db.ExecuteAsync($"create database [{name}] collate {Collation}");
        await db.ExecuteAsync($"alter database [{name}] set read_committed_snapshot on with rollback immediate");
        // Snapshot isolation for reads that must see one consistent state across several statements (legacy
        // "repeatable read read only": the permission inputs, the reconciliation).
        await db.ExecuteAsync($"alter database [{name}] set allow_snapshot_isolation on");
        log($"database {name} created ({Collation}, read_committed_snapshot on, allow_snapshot_isolation on)");
    }

    /// <summary>A SQL login for the application, a user in <paramref name="database"/> and a member of role core_app only.</summary>
    public static async Task CreateAppLoginAsync(string masterConnection, string database, string login, string password, Action<string> log)
    {
        CheckName(login, "Login");
        CheckName(database, "Database name");
        await using var db = new SqlConnection(masterConnection);
        await db.OpenAsync();
        if (await db.ExecuteScalarAsync<int?>("select 1 from sys.server_principals where name = @login", new { login }) is null)
            await db.ExecuteAsync($"create login [{login}] with password = N'{password.Replace("'", "''", StringComparison.Ordinal)}', check_policy = on, default_database = [{database}]");
        await db.ExecuteAsync($"""
            use [{database}];
            if database_principal_id(N'{login}') is null create user [{login}] for login [{login}] with default_schema = core;
            if is_rolemember(N'core_app', N'{login}') = 0 alter role core_app add member [{login}];
            """);
        log($"login {login} is a member of core_app in {database}");
    }

    /// <summary>Drops a database and logins made for tests. Refuses any database whose name does not start with <paramref name="requiredPrefix"/>.</summary>
    public static async Task DropTestDatabaseAsync(string masterConnection, string name, string requiredPrefix, IEnumerable<string> logins)
    {
        CheckName(name, "Database name");
        if (!name.StartsWith(requiredPrefix, StringComparison.Ordinal)) throw new InvalidOperationException($"{name} is not a test database");
        SqlConnection.ClearAllPools();
        await using var db = new SqlConnection(masterConnection);
        await db.OpenAsync();
        await db.ExecuteAsync($"if db_id(N'{name}') is not null begin alter database [{name}] set single_user with rollback immediate; drop database [{name}]; end");
        foreach (var login in logins)
        {
            CheckName(login, "Login");
            await db.ExecuteAsync($"if exists (select 1 from sys.server_principals where name = N'{login}') drop login [{login}]");
        }
    }
}
