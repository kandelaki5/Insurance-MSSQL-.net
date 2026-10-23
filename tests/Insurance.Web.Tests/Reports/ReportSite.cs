using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Domain.Common;
using Insurance.Integration.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Web.Tests.Reports;

/// <summary>
/// The real web application over a report world of its own (a fresh ins_it_* database, ReportWorld): sign in as a fictional user of a role,
/// open pages and downloads over HTTP. The National Bank is unreachable (every request refused and counted), as in the legacy tests.
/// </summary>
public sealed partial class ReportSite : IAsyncDisposable
{
    public required ReportWorld World { get; init; }
    public required WebApplicationFactory<Program> Factory { get; init; }
    public required RefusingNbg Nbg { get; init; }

    public static async Task<ReportSite> CreateAsync()
    {
        var world = await ReportWorld.CreateAsync();
        var nbg = new RefusingNbg();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Insurance", world.Database.AppConnection);
            b.ConfigureTestServices(s => s.AddSingleton<IHttpClientFactory>(nbg));
        });
        return new ReportSite { World = world, Factory = factory, Nbg = nbg };
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await World.DisposeAsync();
    }

    /// <summary>A fictional user of the role with a random password.</summary>
    public async Task<(Guid Id, string Username, string Password)> UserAsync(string role)
    {
        var id = Guid.NewGuid();
        var username = $"test.{role}.{id.ToString("N")[..8]}";
        var password = "Pw-" + Guid.NewGuid().ToString("N");
        await using var c = await World.Database.OpenAdminAsync();
        await c.ExecuteAsync("""
            insert core.app_user (id, username, full_name, password_hash, role_code, branch_id)
            values (@id, @username, @fullName, @hash, @role, (select id from core.branch where code = N'HQ'))
            """, new { id, username, fullName = $"სატესტო {role}", hash = Password.HashPassword(password), role });
        return (id, username, password);
    }

    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

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

    /// <summary>Posts a form with the anti-forgery token of the page it is on.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, Dictionary<string, string> fields, string? pageUrl = null)
    {
        var page = await client.GetAsync(pageUrl ?? url);
        var html = await page.Content.ReadAsStringAsync();
        var token = Token().Match(html);
        Assert.True(token.Success, $"no anti-forgery token on {pageUrl ?? url} ({(int)page.StatusCode})");
        var form = new List<KeyValuePair<string, string>>(fields) { new("__RequestVerificationToken", WebUtility.HtmlDecode(token.Groups[1].Value)) };
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    /// <summary>The page's HTML (200 asserted).</summary>
    public static async Task<string> PageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadAsStringAsync();
    }

    [GeneratedRegex(@"<td[^>]*>([\s\S]*?)</td>")]
    private static partial Regex CellRe();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    private static string CellText(Match m) => WebUtility.HtmlDecode(Tag().Replace(m.Groups[1].Value, "")).Trim();

    /// <summary>The cells of the first table row whose first cell is <paramref name="first"/> (tags inside cells stripped to their first text).</summary>
    public static string[] Cells(string html, string first) => Rows(html, first).FirstOrDefault() ?? [];

    /// <summary>The cells of every table row whose first cell is <paramref name="first"/>, in order.</summary>
    public static List<string[]> Rows(string html, string first) =>
        Regex.Matches(html, "<tr[^>]*>([\\s\\S]*?)</tr>")
            .Select(row => CellRe().Matches(row.Groups[1].Value).Select(CellText).ToArray())
            .Where(cells => cells.Length > 0 && cells[0] == first).ToList();

    /// <summary>The cells of every total row of the page, in order.</summary>
    public static List<string[]> TotalRows(string html) =>
        Regex.Matches(html, "<tr class=\"total-row\">([\\s\\S]*?)</tr>")
            .Select(row => CellRe().Matches(row.Groups[1].Value).Select(CellText).ToArray()).ToList();

    public static string Decode(string html) => WebUtility.HtmlDecode(html);
}
