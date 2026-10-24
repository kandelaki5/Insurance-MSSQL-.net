using System.Net;
using System.Text.Json;
using Insurance.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insurance.Web.Tests.Api;

/// <summary>
/// The /api/v1 foundation (INS-DOTNET-MSSQL-004 B1/B4): the health check reads the database's migration journal, the signed-in user is
/// the session's, a request without a session is answered 401 as problem details (never the login page's redirect), and the OpenAPI
/// document is served in Development only.
/// </summary>
public sealed class ApiFoundationTests(WebApp app) : IClassFixture<WebApp>
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task The_health_check_is_anonymous_and_names_the_last_migration_of_the_database()
    {
        var r = await app.Client().GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await JsonAsync(r);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        var migrations = Directory.GetFiles(Path.Combine(Root(), "database", "migrations"), "*.sql").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(migrations.Length, body.GetProperty("migrations").GetInt32());
        Assert.Equal(migrations[^1], body.GetProperty("lastMigration").GetString());
    }

    [Fact]
    public async Task Without_a_session_me_is_401_problem_details_not_a_redirect()
    {
        var r = await app.Client().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(r);
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Equal(Problems.NotSignedInCode, body.GetProperty("code").GetString());
        Assert.Null(r.Headers.Location);
    }

    [Theory]
    [InlineData("viewer", false)]
    [InlineData("accountant", true)]
    public async Task With_a_session_me_is_the_sessions_user_and_role(string role, bool canWrite)
    {
        var (client, _) = await app.SignedInAsync(role);
        var r = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await JsonAsync(r);
        Assert.Equal(role, body.GetProperty("role").GetString());
        Assert.StartsWith($"test.{role}.", body.GetProperty("username").GetString());
        Assert.Equal(canWrite, body.GetProperty("canWrite").GetBoolean());
        // After the logout the same client is a stranger again.
        Assert.Equal(HttpStatusCode.Redirect, (await WebApp.PostFormAsync(client, "/logout", [], "/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task The_OpenAPI_document_lists_the_endpoints_in_Development_and_is_not_served_in_production()
    {
        var doc = await JsonAsync(await app.Client().GetAsync("/api/openapi/v1.json"));
        var paths = doc.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("/api/v1/health", paths);
        Assert.Contains("/api/v1/me", paths);
        await using var production = app.Factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = production.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health")).StatusCode);
    }

    [Fact]
    public async Task Outside_Development_an_unexpected_error_of_the_api_is_500_problem_details_with_nothing_of_the_error()
    {
        // A USD policy with no USD rate stored at all: the card's revaluation cannot be made (the Razor card fails the same way).
        var w = new Insurance.Integration.Tests.Policies.World(app.Db);
        var usd = await w.PendingPolicyAsync(await w.ClientAsync(), w.App.Today(), "EUR", 2.9m);
        var (_, username, password) = await app.UserAsync("viewer");
        await using var production = app.Factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = production.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        Assert.Equal(HttpStatusCode.Redirect, (await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = password })).StatusCode);
        var r = await client.GetAsync($"/api/v1/policies/{usd.Id}");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("EUR", text);
        Assert.DoesNotContain("InvalidOperationException", text);
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Insurance.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
