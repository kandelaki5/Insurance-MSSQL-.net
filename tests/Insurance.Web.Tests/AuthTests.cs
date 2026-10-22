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
}
