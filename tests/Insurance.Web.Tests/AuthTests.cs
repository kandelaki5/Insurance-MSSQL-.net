using System.Net;
using Dapper;

namespace Insurance.Web.Tests;

/// <summary>Sign-in, sessions, throttling and the page guard over HTTP on a real database.</summary>
public sealed class AuthTests(WebApp app) : IClassFixture<WebApp>
{
    [Fact]
    public async Task Pages_need_a_session_and_login_sets_one()
    {
        var anonymous = app.Client();
        var refused = await anonymous.GetAsync("/reference/classes");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal("/login", refused.Headers.Location?.ToString());

        var (client, _) = await app.SignedInAsync("viewer");
        var page = await client.GetAsync("/reference/classes");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("სახმელეთო ავტოსატრანსპორტო საშუალებათა დაზღვევა (ავტოკასკო)", html);
        // The viewer's menu has no administration or accounting.
        Assert.DoesNotContain("href=\"/users\"", html);
        Assert.DoesNotContain("href=\"/accounting/periods\"", html);
    }

    [Fact]
    public async Task Wrong_password_is_refused_with_one_message_and_recorded()
    {
        var (_, username, _) = await app.UserAsync("sales");
        var client = app.Client();
        var response = await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = "wrong" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("მომხმარებელი ან პაროლი არასწორია", await response.Content.ReadAsStringAsync());
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.login_attempt where username = @username and succeeded = 0", new { username }));
    }

    [Fact]
    public async Task Five_failures_lock_the_account_for_the_window_even_with_the_right_password()
    {
        var (_, username, password) = await app.UserAsync("sales");
        var client = app.Client();
        for (var i = 0; i < 5; i++)
            await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = "wrong" + i });
        var response = await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("მომხმარებელი ან პაროლი არასწორია", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logout_ends_the_session_in_the_database()
    {
        var (client, userId) = await app.SignedInAsync("accountant");
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.session where user_id = @userId", new { userId }));
        var response = await WebApp.PostFormAsync(client, "/logout", [], pageUrl: "/reference/classes");
        Assert.Equal("/login", response.Headers.Location?.ToString());
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.session where user_id = @userId", new { userId }));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/reference/classes")).StatusCode);
    }

    [Fact]
    public async Task A_form_without_its_anti_forgery_token_is_refused()
    {
        var (_, username, password) = await app.UserAsync("sales");
        var response = await app.Client().PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = password }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    /// <summary>
    /// Codex R01 on the real sign-in path: a user whose stored hash is damaged gets no session with any password, the one
    /// the hash was made from included, and sees the one refusal message; a hash written by the old program still signs in.
    /// </summary>
    [Fact]
    public async Task A_damaged_stored_hash_signs_no_one_in_and_an_old_programs_hash_still_does()
    {
        // Made by the old Node hashPassword for the fictional password "Tbilisi-2026!" (tests/parity/cases/common.json).
        const string oldHash = "scrypt$ad3d1f2bdfb13819772390679970e9c3$5a99e0c2bdd0b922e4bf1f66a2878554a6b13dfbd74d2015cb7613427ca68f5cbd9918c52b21cd062f7fc88a94900270f968029d4090601d35a8582d0de30b43";
        const string oldPassword = "Tbilisi-2026!";
        var damaged = new[] { "scrypt$aa$zz", "scrypt$aa$z", "scrypt$xx$zz", oldHash[..^2], oldHash[..^1], oldHash + "$extra", oldHash[..^2] + "zz" };
        await using var c = await app.Db.OpenAdminAsync();
        foreach (var hash in damaged)
        {
            var (id, username, _) = await app.UserAsync("sales");
            await c.ExecuteAsync("update core.app_user set password_hash = @hash where id = @id", new { hash, id });
            foreach (var password in new[] { oldPassword, "anything", "" })
            {
                var response = await WebApp.PostFormAsync(app.Client(), "/login", new() { ["username"] = username, ["password"] = password });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var html = await response.Content.ReadAsStringAsync();
                Assert.Contains("მომხმარებელი ან პაროლი არასწორია", html);
                Assert.DoesNotContain("scrypt", html);
            }
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.session where user_id = @id", new { id }));
        }

        // Positive control: the same user row with the old program's hash intact signs in and gets its session.
        var (goodId, goodName, _) = await app.UserAsync("sales");
        await c.ExecuteAsync("update core.app_user set password_hash = @oldHash where id = @goodId", new { oldHash, goodId });
        var wrong = await WebApp.PostFormAsync(app.Client(), "/login", new() { ["username"] = goodName, ["password"] = oldPassword + "x" });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        var signedIn = await WebApp.PostFormAsync(app.Client(), "/login", new() { ["username"] = goodName, ["password"] = oldPassword });
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("select count(*) from core.session where user_id = @goodId", new { goodId }));
    }
}
