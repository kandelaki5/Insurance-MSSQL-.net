using System.Net;
using Dapper;
using Insurance.Application.Texts;
using Insurance.Domain.Common;

namespace Insurance.Web.Tests;

/// <summary>Users of the system over HTTP, admin only (legacy createUser, setUserActive, resetPassword).</summary>
public sealed class UserTests(WebApp app) : IClassFixture<WebApp>
{
    [Fact]
    public async Task The_admin_creates_a_user_with_the_name_and_password_rules_and_a_taken_name_is_refused()
    {
        var (client, _) = await app.SignedInAsync("admin");
        var name = $"new.user-{Guid.NewGuid().ToString("N")[..6]}";
        var refused = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/users?handler=Create",
            new() { ["username"] = "Ab", ["full_name"] = " ", ["password"] = "short", ["role_code"] = "sales" }, "/users"));
        Assert.Contains(Ka.User.Errors.Username, refused);
        Assert.Contains(Ka.User.Errors.FullName, refused);
        Assert.Contains(Ka.User.Errors.Password, refused);
        Assert.Contains("value=\"Ab\"", refused);

        var created = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/users?handler=Create",
            new() { ["username"] = $"  {name.ToUpperInvariant()} ", ["full_name"] = "ახალი თანამშრომელი", ["password"] = "Long-enough-1", ["role_code"] = "claims" }, "/users"));
        Assert.Contains(Ka.User.Created, created);
        await using var c = await app.Db.OpenAdminAsync();
        var row = await c.QuerySingleAsync<(Guid Id, string Role, string Hash, bool Active, Guid? Branch)>("select id, role_code, password_hash, is_active, branch_id from core.app_user where username = @name", new { name });
        Assert.Equal(("claims", true, (Guid?)null), (row.Role, row.Active, row.Branch));
        Assert.True(Password.VerifyPassword("Long-enough-1", row.Hash));
        // As before, the new user is written without an actor.
        Assert.Null(await c.ExecuteScalarAsync<Guid?>("select user_id from core.audit_log where table_name = N'app_user' and row_id = @id and action = N'insert'", new { id = row.Id.ToString() }));
        // The new user signs in with the password.
        var login = await WebApp.PostFormAsync(app.Client(), "/login", new() { ["username"] = name, ["password"] = "Long-enough-1" });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var duplicate = await Html.BodyAsync(await WebApp.PostFormAsync(client, "/users?handler=Create",
            new() { ["username"] = name, ["full_name"] = "მეორე", ["password"] = "Long-enough-2", ["role_code"] = "sales" }, "/users"));
        Assert.Contains(Ka.User.Errors.Duplicate, duplicate);
    }

    [Fact]
    public async Task Deactivating_ends_the_users_sessions_and_a_reset_password_ends_them_too_but_the_admin_never_deactivates_itself()
    {
        var (admin, adminId) = await app.SignedInAsync("admin");
        var (user, userId) = await app.SignedInAsync("sales");
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/parties")).StatusCode);

        Assert.Equal("/users", Html.Location(await WebApp.PostFormAsync(admin, "/users?handler=SetActive", new() { ["id"] = userId.ToString(), ["active"] = "false" }, "/users")));
        Assert.False(await c.ExecuteScalarAsync<bool>("select is_active from core.app_user where id = @userId", new { userId }));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.session where user_id = @userId", new { userId }));
        Assert.Equal("/login", Html.Location(await user.GetAsync("/parties")));
        Assert.Contains(Ka.User.Inactive, await Html.GetAsync(admin, "/users"));
        await WebApp.PostFormAsync(admin, "/users?handler=SetActive", new() { ["id"] = userId.ToString(), ["active"] = "true" }, "/users");
        Assert.True(await c.ExecuteScalarAsync<bool>("select is_active from core.app_user where id = @userId", new { userId }));

        // The admin's own row has no deactivate button, and a forged post changes nothing.
        await WebApp.PostFormAsync(admin, "/users?handler=SetActive", new() { ["id"] = adminId.ToString(), ["active"] = "false" }, "/users");
        Assert.True(await c.ExecuteScalarAsync<bool>("select is_active from core.app_user where id = @adminId", new { adminId }));

        var shortPassword = await Html.BodyAsync(await WebApp.PostFormAsync(admin, "/users?handler=ResetPassword", new() { ["id"] = userId.ToString(), ["password"] = "123" }, "/users"));
        Assert.Contains($"<span class=\"field-error\">{Ka.User.Errors.Password}</span>", shortPassword);
        var reset = await Html.BodyAsync(await WebApp.PostFormAsync(admin, "/users?handler=ResetPassword", new() { ["id"] = userId.ToString(), ["password"] = "Brand-new-pass" }, "/users"));
        Assert.Contains($"<span class=\"badge low\">{Ka.Party.Saved}</span>", reset);
        Assert.True(Password.VerifyPassword("Brand-new-pass", (await c.ExecuteScalarAsync<string>("select password_hash from core.app_user where id = @userId", new { userId }))!));

        // The admin's own new password keeps the admin signed in (a fresh session), every old session is gone.
        var oldSessions = await c.ExecuteScalarAsync<int>("select count(*) from core.session where user_id = @adminId", new { adminId });
        Assert.True(oldSessions >= 1);
        var self = await WebApp.PostFormAsync(admin, "/users?handler=ResetPassword", new() { ["id"] = adminId.ToString(), ["password"] = "Admin-new-pass" }, "/users");
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.session where user_id = @adminId", new { adminId }));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/users")).StatusCode);
    }

    [Fact]
    public async Task Users_and_settings_are_for_the_admin_only()
    {
        foreach (var role in new[] { "accountant", "operations_head", "viewer" })
        {
            var (client, _) = await app.SignedInAsync(role);
            foreach (var page in new[] { "/users", "/users/permissions", "/settings", "/settings/products" })
                Assert.Equal("/", Html.Location(await client.GetAsync(page)));
            var name = $"forged.{Guid.NewGuid().ToString("N")[..6]}";
            var response = await WebApp.PostFormAsync(client, "/users?handler=Create",
                new() { ["username"] = name, ["full_name"] = "x", ["password"] = "Long-enough-1", ["role_code"] = "admin" }, "/parties");
            Assert.Equal("/", Html.Location(response));
            await using var c = await app.Db.OpenAdminAsync();
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.app_user where username = @name", new { name }));
        }
    }
}
