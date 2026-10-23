using System.Security.Cryptography;
using Dapper;
using Insurance.Domain.Common;
using Microsoft.Data.SqlClient;

namespace Insurance.Tools;

/// <summary>
/// The claude.* test users (legacy scripts/claude-test-users.ts and claude-test-users-plan.ts; owner, 05.10.2026): one user
/// per role, separate from the owner's users, so the audit shows who did what. Passwords are generated here, written only
/// to the local env file (git-ignored, never printed) and stored as hashes. Running it again only creates missing users; a
/// user that already exists keeps its password, and when the local file lacks it or differs, nothing is written (another
/// checkout or session may share the users). Only a test database: its name must start with ins_it_ or contain "test".
/// </summary>
public static class TestUsers
{
    public sealed record Plan(IReadOnlyDictionary<string, string> Generated, IReadOnlyList<string> Create, IReadOnlyList<string> Problems);

    /// <summary>What may be done, given the local passwords and the users already on the database (legacy planTestUsers).</summary>
    public static Plan PlanTestUsers(IReadOnlyList<string> usernames, Func<string, string> key, IReadOnlyDictionary<string, string> local,
        IReadOnlyDictionary<string, string> existing, Func<string, string, bool> verify, Func<string> generate)
    {
        var generated = new Dictionary<string, string>(StringComparer.Ordinal);
        var create = new List<string>();
        var problems = new List<string>();
        foreach (var username in usernames)
        {
            var k = key(username);
            local.TryGetValue(k, out var password);
            if (existing.TryGetValue(username, out var hash))
            {
                if (password is null) problems.Add($"{username} already exists on the database but the env file has no {k}; copy the env file from the environment that created it");
                else if (!verify(password, hash)) problems.Add($"{k} in the env file does not match {username} on the database; copy the env file from the environment that created it");
                continue;
            }
            if (password is null) generated[k] = generate();
            create.Add(username);
        }
        return new Plan(generated, create, problems);
    }

    /// <summary>KEY=value lines of the env file.</summary>
    public static IReadOnlyDictionary<string, string> ParseEnv(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            var eq = l.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || eq == l.Length - 1 || !l[..eq].All(c => c is >= 'A' and <= 'Z' or '_')) continue;
            map[l[..eq]] = l[(eq + 1)..];
        }
        return map;
    }

    /// <summary>
    /// Text to append to the env file (current: its contents, or null when it does not exist). A file that does not end with
    /// a newline gets one first, so a new key is never glued onto the last value.
    /// </summary>
    public static string EnvAppendText(string? current, IReadOnlyDictionary<string, string> generated, string header)
    {
        var lead = current is null ? header : current.Length > 0 && !current.EndsWith('\n') ? "\n" : "";
        return lead + string.Concat(generated.Select(p => $"{p.Key}={p.Value}\n"));
    }

    public static readonly IReadOnlyList<(string Username, string Role, string Name)> Users =
    [
        ("claude.admin", "admin", "Claude — ადმინისტრატორი (ტესტი)"),
        ("claude.operator", "sales", "Claude — ოპერატორი (ტესტი)"),
        ("claude.head", "operations_head", "Claude — საოპერაციო უფროსი (ტესტი)"),
        ("claude.accountant", "accountant", "Claude — ბუღალტერი (ტესტი)"),
        ("claude.viewer", "viewer", "Claude — მხოლოდ ნახვა (ტესტი)"),
    ];

    public static string Key(string username) => $"CLAUDE_{username.Split('.')[1].ToUpperInvariant()}_PASSWORD";

    public static bool IsTestDatabase(string name) =>
        name.StartsWith("ins_it_", StringComparison.Ordinal) || name.Contains("test", StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates the missing claude.* users on the test database of <paramref name="adminConnection"/>; the passwords go to <paramref name="envFile"/>.</summary>
    public static async Task RunAsync(string adminConnection, string envFile, Action<string> log)
    {
        await using var db = new SqlConnection(adminConnection);
        await db.OpenAsync();
        var name = await db.ExecuteScalarAsync<string>("select db_name()") ?? "";
        if (!IsTestDatabase(name)) throw new InvalidOperationException($"connected to {name}: the test users are made only on a test database (ins_it_* or a name with \"test\")");
        IReadOnlyDictionary<string, string> Stored() => ParseEnv(File.Exists(envFile) ? File.ReadAllText(envFile) : "");

        var roles = (await db.QueryAsync<string>("select code from core.role")).ToHashSet(StringComparer.Ordinal);
        foreach (var u in Users)
            if (!roles.Contains(u.Role)) throw new InvalidOperationException($"role {u.Role} does not exist in {name}");
        var usernames = Users.Select(u => u.Username).ToList();
        var existing = (await db.QueryAsync<(string Username, string Hash)>("select username, password_hash from core.app_user where username in @usernames", new { usernames }))
            .ToDictionary(r => r.Username, r => r.Hash, StringComparer.Ordinal);
        var plan = PlanTestUsers(usernames, Key, Stored(), existing, Password.VerifyPassword, () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(15)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        if (plan.Problems.Count > 0) throw new InvalidOperationException("nothing written:\n" + string.Join("\n", plan.Problems));
        if (plan.Generated.Count > 0)
        {
            const string header = "# Test-only passwords of the claude.* users (Insurance.Tools test-users).\n# Never real passwords; never committed.\nCLAUDE_TEST_USERS=claude.admin,claude.operator,claude.head,claude.accountant,claude.viewer\n";
            File.AppendAllText(envFile, EnvAppendText(File.Exists(envFile) ? File.ReadAllText(envFile) : null, plan.Generated, header));
        }
        var passwords = Stored();
        foreach (var u in Users)
        {
            if (!plan.Create.Contains(u.Username))
            {
                await db.ExecuteAsync("update core.app_user set role_code = @role, is_active = 1 where username = @username", new { role = u.Role, username = u.Username });
                continue;
            }
            var password = passwords.GetValueOrDefault(Key(u.Username)) ?? throw new InvalidOperationException($"no password for {u.Username} in the env file");
            // A user created meanwhile by another environment keeps its password; the check below reports it.
            await db.ExecuteAsync("""
                if not exists (select 1 from core.app_user where username = @username)
                  insert into core.app_user (username, full_name, password_hash, role_code, branch_id)
                  values (@username, @name, @hash, @role, (select id from core.branch where code = N'HQ'))
                """, new { username = u.Username, name = u.Name, hash = Password.HashPassword(password), role = u.Role });
        }
        foreach (var (username, hash) in await db.QueryAsync<(string, string)>("select username, password_hash from core.app_user where username in @usernames", new { usernames }))
            if (!Password.VerifyPassword(passwords.GetValueOrDefault(Key(username)) ?? "", hash))
                throw new InvalidOperationException($"{username} on {name} does not match the env file (created meanwhile elsewhere?); copy that environment's env file");
        log($"{plan.Generated.Count} new password(s) written to the env file, {plan.Create.Count} user(s) created; users ready on {name}:");
        log(string.Join(", ", await db.QueryAsync<string>("select username + N' (' + role_code + N')' from core.app_user where username like N'claude.%' order by username")));
    }
}
