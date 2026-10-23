using Dapper;
using Insurance.Application.Services;
using Insurance.Domain.Common;

namespace Insurance.Integration.Tests;

/// <summary>
/// The admin command <c>seed-admin</c> (src/Insurance.Tools, replacing legacy scripts/seed.ts), run as the real command line in a
/// process of its own (its environment its own, so no other test sees the password variable) on a fresh test database: it creates an
/// administrator who can sign in; run again it resets the password (and activates the user), as the command says, keeping the one
/// row; it never prints the password; an empty or missing INSURANCE_ADMIN_PASSWORD is refused with nothing written — legacy seed.ts
/// threw on <c>!ADMIN_PASSWORD</c>, which is true for the empty string too.
/// </summary>
public sealed class SeedAdminToolTests(TestDatabase db) : IClassFixture<TestDatabase>
{
    private static Task<ToolsProcess.Run> SeedAsync(string connection, string? password, params string[] args) =>
        ToolsProcess.RunAsync(new Dictionary<string, string?> { ["INSURANCE_ADMIN_CONNECTION"] = connection, ["INSURANCE_ADMIN_PASSWORD"] = password }, args);

    private async Task<(Guid Id, string Username, string FullName, string Role, bool Active, string Hash, string Branch)?> RowAsync(string username)
    {
        await using var c = await db.OpenAdminAsync();
        return await c.QuerySingleOrDefaultAsync<(Guid, string, string, string, bool, string, string)?>("""
            select u.id, u.username, u.full_name, u.role_code, u.is_active, u.password_hash, b.code
            from core.app_user u left join core.branch b on b.id = u.branch_id where u.username = @username
            """, new { username });
    }

    private Task<Guid?> SignInAsync(string username, string password) => new Auth(db.App).LoginAsync(username, password, "seed-admin test");

    [Fact]
    public async Task Seed_admin_creates_an_administrator_who_can_sign_in_again_resets_the_password_never_prints_it_and_refuses_an_empty_password()
    {
        var name = $"Seed.Admin{Guid.NewGuid().ToString("N")[..6]}";
        var username = name.ToLowerInvariant();
        var first = "Pw1-" + Guid.NewGuid().ToString("N");
        var second = "Pw2-" + Guid.NewGuid().ToString("N");

        // Created: the username as the legacy lower-cased it, an admin of the head office, active; the password works.
        var run = await SeedAsync(db.AdminConnection, first, "seed-admin", name, "ადმინისტრატორი ტესტი");
        Assert.True(run.Exit == 0, run.Error);
        Assert.Equal($"administrator \"{username}\" is ready", run.Output.Trim());
        Assert.DoesNotContain(first, run.Output + run.Error, StringComparison.Ordinal);
        var row = await RowAsync(username);
        Assert.NotNull(row);
        Assert.Equal((username, "ადმინისტრატორი ტესტი", "admin", true, "HQ"), (row.Value.Username, row.Value.FullName, row.Value.Role, row.Value.Active, row.Value.Branch));
        Assert.True(Password.VerifyPassword(first, row.Value.Hash));
        Assert.DoesNotContain(first, row.Value.Hash, StringComparison.Ordinal);
        Assert.Equal(row.Value.Id, await SignInAsync(username, first));

        // Again, the user deactivated meanwhile: the password reset and the user active again; one row, the same id and name.
        await using (var c = await db.OpenAdminAsync())
            await c.ExecuteAsync("update core.app_user set is_active = 0 where username = @username", new { username });
        run = await SeedAsync(db.AdminConnection, second, "seed-admin", name, "სხვა სახელი");
        Assert.True(run.Exit == 0, run.Error);
        Assert.DoesNotContain(second, run.Output + run.Error, StringComparison.Ordinal);
        var again = await RowAsync(username);
        Assert.Equal((row.Value.Id, "ადმინისტრატორი ტესტი", "admin", true), (again!.Value.Id, again.Value.FullName, again.Value.Role, again.Value.Active));
        Assert.Null(await SignInAsync(username, first));
        Assert.Equal(row.Value.Id, await SignInAsync(username, second));
        await using (var c = await db.OpenAdminAsync())
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.app_user where lower(username) = @username", new { username }));

        // An empty password, or none at all: refused (exit 1, the variable named), nothing written — the hash as it was.
        foreach (var empty in new[] { "", null })
        {
            run = await SeedAsync(db.AdminConnection, empty, "seed-admin", name, "ადმინისტრატორი ტესტი");
            Assert.Equal(1, run.Exit);
            Assert.Contains("INSURANCE_ADMIN_PASSWORD is not set", run.Error, StringComparison.Ordinal);
            Assert.Equal(again.Value.Hash, (await RowAsync(username))!.Value.Hash);
        }
        // A new name with an empty password creates nobody.
        run = await SeedAsync(db.AdminConnection, "", "seed-admin", "nobody.seeded", "არავინ");
        Assert.Equal(1, run.Exit);
        Assert.Null(await RowAsync("nobody.seeded"));
        Assert.Equal(row.Value.Id, await SignInAsync(username, second));
    }
}
