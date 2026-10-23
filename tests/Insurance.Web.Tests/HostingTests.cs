using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Web.Tests;

/// <summary>
/// Codex R03: outside Development the session and anti-forgery cookies are always Secure and the program answers HTTPS only —
/// directly (IIS in-process, Kestrel with a certificate) or through a configured proxy that ended TLS and says so in
/// X-Forwarded-Proto. A proxy's X-Forwarded-For and X-Forwarded-Proto are believed only from a configured proxy, never from
/// any client. The test server has no network peer, so a startup filter stands in for the TCP connection: it sets the
/// connection's address from X-Test-Peer before the application's middleware. The cookies are carried by hand, as a browser
/// carries them, Secure ones included, so that the scheme the program sees is only the one under test.
/// </summary>
public sealed partial class HostingTests(WebApp app) : IClassFixture<WebApp>
{
    private const string Proxy = "10.20.30.40";

    private sealed class PeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, inner) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Peer", out var peer)) context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                await inner();
            });
            next(builder);
        };
    }

    private WebApplicationFactory<Program> Host(string environment) => app.Factory.WithWebHostBuilder(b =>
    {
        b.UseEnvironment(environment);
        b.UseSetting("Insurance:ForwardedHeaders:KnownProxies:0", Proxy);
        b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, PeerFilter>());
    });

    /// <summary>A browser behind <paramref name="peer"/>: keeps the cookies it is given and sends them back.</summary>
    private sealed class Browser(HttpClient client)
    {
        public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);
        public List<string> SetCookies { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Dictionary<string, string>? form = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (Cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
            if (form is not null) request.Content = new FormUrlEncodedContent(form);
            var response = await client.SendAsync(request);
            SetCookies.Clear();
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var v in values)
                {
                    SetCookies.Add(v);
                    var pair = v.Split(';')[0].Split('=', 2);
                    if (v.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)) Cookies.Remove(pair[0]);
                    else Cookies[pair[0]] = pair[1];
                }
            return response;
        }

        public string? SetCookie(string name) => SetCookies.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal));

        /// <summary>Posts a form of the page at <paramref name="pageUrl"/> with its anti-forgery token.</summary>
        public async Task<HttpResponseMessage> PostFormAsync(string pageUrl, string url, Dictionary<string, string> fields)
        {
            var page = await SendAsync(HttpMethod.Get, pageUrl);
            var token = Token().Match(await page.Content.ReadAsStringAsync());
            Assert.True(token.Success, $"no anti-forgery token on {pageUrl} ({(int)page.StatusCode})");
            var pageCsrf = SetCookie("csrf");
            var answer = await SendAsync(HttpMethod.Post, url, new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) });
            LastPageCsrf = pageCsrf;
            return answer;
        }

        public string? LastPageCsrf { get; private set; }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private static Browser Open(WebApplicationFactory<Program> host, string peer, string? forwardedFor = null, string? forwardedProto = null, string scheme = "http")
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri($"{scheme}://localhost") });
        client.DefaultRequestHeaders.Add("X-Test-Peer", peer);
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null) client.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);
        return new Browser(client);
    }

    private static bool IsSecure(string? cookie) => cookie is not null && cookie.Split(';').Any(p => p.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase));

    private async Task<string?> LastSourceAsync(string username)
    {
        await using var c = await app.Db.OpenAdminAsync();
        return await c.ExecuteScalarAsync<string?>("select top 1 source from core.login_attempt where username = @username order by attempted_at desc", new { username });
    }

    private async Task<int> SessionsAsync(Guid userId)
    {
        await using var c = await app.Db.OpenAdminAsync();
        return await c.ExecuteScalarAsync<int>("select count(*) from core.session where user_id = @userId", new { userId });
    }

    [Fact]
    public async Task On_a_production_host_plain_HTTP_is_refused_with_an_explanation_and_no_cookie_even_when_a_client_claims_HTTPS()
    {
        await using var host = Host("Production");
        foreach (var browser in new[] { Open(host, "203.0.113.9"), Open(host, "203.0.113.9", forwardedFor: "198.51.100.1", forwardedProto: "https") })
        {
            var page = await browser.SendAsync(HttpMethod.Get, "/login");
            Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
            Assert.Equal(Hosting.HttpsRequired, await page.Content.ReadAsStringAsync());
            Assert.Empty(browser.SetCookies);
        }
    }

    [Fact]
    public async Task On_a_production_host_behind_a_trusted_proxy_that_ended_TLS_the_cookies_are_Secure_and_logout_removes_them_so()
    {
        await using var host = Host("Production");
        var (userId, username, password) = await app.UserAsync("viewer");
        // The proxy ended TLS and passes the request on as HTTP, saying it came over HTTPS from 198.51.100.7.
        var browser = Open(host, Proxy, forwardedFor: "198.51.100.7", forwardedProto: "https");
        var login = await browser.PostFormAsync("/login", "/login", new() { ["username"] = username, ["password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.True(IsSecure(browser.LastPageCsrf), browser.LastPageCsrf);
        var session = browser.SetCookie("session");
        Assert.True(IsSecure(session), session);
        Assert.Contains("httponly", session!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", session!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("198.51.100.7", await LastSourceAsync(username));
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, "/reference/classes")).StatusCode);

        var logout = await browser.PostFormAsync("/reference/classes", "/logout", []);
        Assert.Equal("/login", logout.Headers.Location?.ToString());
        var removal = browser.SetCookie("session");
        Assert.True(IsSecure(removal), removal);
        Assert.Contains("expires=Thu, 01 Jan 1970", removal!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await SessionsAsync(userId));
    }

    [Fact]
    public async Task On_a_production_host_reached_directly_over_HTTPS_the_cookies_are_Secure()
    {
        await using var host = Host("Production");
        var (userId, username, password) = await app.UserAsync("viewer");
        var browser = Open(host, "203.0.113.9", scheme: "https");
        var login = await browser.PostFormAsync("/login", "/login", new() { ["username"] = username, ["password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.True(IsSecure(browser.LastPageCsrf), browser.LastPageCsrf);
        Assert.True(IsSecure(browser.SetCookie("session")), browser.SetCookie("session"));
        Assert.Equal("203.0.113.9", await LastSourceAsync(username));
        Assert.Equal(1, await SessionsAsync(userId));
    }

    [Fact]
    public async Task Forwarded_headers_count_only_from_a_configured_proxy()
    {
        await using var host = Host("Development");
        var (_, username, _) = await app.UserAsync("viewer");

        // A client that is not a proxy claims HTTPS and another address: both ignored.
        var stranger = Open(host, "203.0.113.9", forwardedFor: "198.51.100.1", forwardedProto: "https");
        await stranger.PostFormAsync("/login", "/login", new() { ["username"] = username, ["password"] = "wrong" });
        Assert.False(IsSecure(stranger.LastPageCsrf), stranger.LastPageCsrf);
        Assert.Equal("203.0.113.9", await LastSourceAsync(username));

        // The configured proxy says the same: believed.
        var proxied = Open(host, Proxy, forwardedFor: "198.51.100.1", forwardedProto: "https");
        await proxied.PostFormAsync("/login", "/login", new() { ["username"] = username, ["password"] = "wrong" });
        Assert.True(IsSecure(proxied.LastPageCsrf), proxied.LastPageCsrf);
        Assert.Equal("198.51.100.1", await LastSourceAsync(username));

        // A chain through an untrusted hop: only the last address, the one the trusted proxy saw, is taken.
        await Open(host, Proxy, forwardedFor: "192.0.2.77, 198.51.100.2").PostFormAsync("/login", "/login", new() { ["username"] = username, ["password"] = "wrong" });
        Assert.Equal("198.51.100.2", await LastSourceAsync(username));

        // Development over plain HTTP from no proxy: cookies without Secure, so a local test run still signs in.
        var local = Open(host, "203.0.113.9");
        await local.PostFormAsync("/login", "/login", new() { ["username"] = username, ["password"] = "wrong" });
        Assert.False(IsSecure(local.LastPageCsrf), local.LastPageCsrf);
    }
}
