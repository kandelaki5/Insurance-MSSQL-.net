using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Domain.Common;
using Insurance.Integration.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insurance.Web.Tests;

/// <summary>
/// The real web application on a fresh test database (TestDatabase), driven over HTTP: sign in as a fictional user,
/// open pages, post forms with their anti-forgery token, and check what the database holds afterwards.
/// </summary>
public sealed partial class WebApp : IAsyncLifetime
{
    public TestDatabase Db { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Insurance", Db.AppConnection);
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }

    /// <summary>A fictional user with a random password; returns the user's id and password.</summary>
    public async Task<(Guid Id, string Username, string Password)> UserAsync(string role, string? username = null)
    {
        var id = Guid.NewGuid();
        username ??= $"test.{role}.{id.ToString("N")[..8]}";
        var password = "Pw-" + Guid.NewGuid().ToString("N");
        await using var c = await Db.OpenAdminAsync();
        await c.ExecuteAsync("""
            insert core.app_user (id, username, full_name, password_hash, role_code, branch_id)
            values (@id, @username, @fullName, @hash, @role, (select id from core.branch where code = N'HQ'))
            """, new { id, username, fullName = $"სატესტო {role}", hash = Password.HashPassword(password), role });
        return (id, username, password);
    }

    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A client signed in as a new user of the role.</summary>
    public async Task<(HttpClient Client, Guid UserId)> SignedInAsync(string role)
    {
        var (id, username, password) = await UserAsync(role);
        var client = Client();
        var response = await PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return (client, id);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    /// <summary>Posts a form to <paramref name="url"/> with the anti-forgery token of the page it is on.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, Dictionary<string, string> fields, string? pageUrl = null)
    {
        var page = await client.GetAsync(pageUrl ?? url);
        var html = await page.Content.ReadAsStringAsync();
        var token = Token().Match(html);
        Assert.True(token.Success, $"no anti-forgery token on {pageUrl ?? url} ({(int)page.StatusCode})");
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) };
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }
}
