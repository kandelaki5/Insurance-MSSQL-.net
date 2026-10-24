using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Domain.Common;
using Insurance.Integration.Tests;
using Insurance.Integration.Tests.Policies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Web.Tests.Policies;

/// <summary>
/// The real web application on a fresh test database, as WebApp, with the National Bank unreachable: a rate the test did
/// not store is "not published" (the page's or action's own answer), never fetched from the network.
/// </summary>
public sealed partial class PolicyWebApp : IAsyncLifetime
{
    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in tests");
    }

    public TestDatabase Db { get; } = new();
    public World W { get; private set; } = null!;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        W = new World(Db);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Insurance", Db.AppConnection);
            b.ConfigureTestServices(s => s.AddSingleton(Db.Options).AddHttpClient("nbg").ConfigurePrimaryHttpMessageHandler(() => new Refuse()));
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }

    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A client signed in as a new user of the role, and the user's id.</summary>
    public async Task<(HttpClient Client, Guid UserId)> SignedInAsync(string role)
    {
        var id = Guid.NewGuid();
        var username = $"test.{role}.{id.ToString("N")[..8]}";
        var password = "Pw-" + Guid.NewGuid().ToString("N");
        await using (var c = await Db.OpenAdminAsync())
            await c.ExecuteAsync("""
                insert into core.app_user (id, username, full_name, password_hash, role_code, branch_id)
                values (@id, @username, @fullName, @hash, @role, (select id from core.branch where code = N'HQ'))
                """, new { id, username, fullName = $"სატესტო {role}", hash = Password.HashPassword(password), role });
        var client = Client();
        var response = await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return (client, id);
    }

    /// <summary>The application's CurrentUser for a signed-in test user (for World's helpers).</summary>
    public async Task<Insurance.Application.Services.CurrentUser> UserOfAsync(Guid id)
    {
        await using var c = await Db.OpenAdminAsync();
        var (username, fullName, role) = await c.QuerySingleAsync<(string, string, string)>("select username, full_name, role_code from core.app_user where id = @id", new { id });
        return new Insurance.Application.Services.CurrentUser(id, username, fullName, role, role);
    }

    public static async Task<string> HtmlAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    /// <summary>A multipart form (a file upload) posted with the anti-forgery token of the page it is on.</summary>
    public static async Task<HttpResponseMessage> PostMultipartAsync(HttpClient client, string url, Dictionary<string, string> fields, (string Name, string FileName, byte[] Bytes)? file, string pageUrl)
    {
        var html = await (await client.GetAsync(pageUrl)).Content.ReadAsStringAsync();
        var token = Token().Match(html);
        Assert.True(token.Success, $"no anti-forgery token on {pageUrl}");
        var content = new MultipartFormDataContent { { new StringContent(WebUtility.HtmlDecode(token.Groups[1].Value)), "__RequestVerificationToken" } };
        foreach (var (k, v) in fields) content.Add(new StringContent(v), k);
        if (file is { } f) content.Add(new ByteArrayContent(f.Bytes), f.Name, f.FileName);
        return await client.PostAsync(url, content);
    }

    public static string Location(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }
}
