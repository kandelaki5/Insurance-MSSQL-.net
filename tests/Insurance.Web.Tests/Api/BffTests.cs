using System.Net;
using System.Text.Json;
using Dapper;
using Insurance.Web.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Web.Tests.Api;

/// <summary>
/// The backend for the browser of ADR 0003 (Codex 006, item 1 of the first slice), over HTTP: with it, the API no longer takes the
/// program's session cookie; the browser signs in to it at /bff/login, which goes through the provider's authorization endpoint (code
/// with PKCE, the client's secret only on the server) and back to /signin-oidc, where the server redeems the code and gives the browser
/// only its own HttpOnly cookie — no token in any answer. That cookie reads the API as the user until the session ends; the return
/// address is local only; the client is refused without its secret. The test host's own server answers the code exchange, as the
/// site answers itself when deployed.
/// </summary>
public sealed class BffTests(WebApp app) : IClassFixture<WebApp>
{
    private sealed class ToTheServer(IServiceProvider services) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new HttpMessageInvoker(((TestServer)services.GetRequiredService<IServer>()).CreateHandler()).SendAsync(request, ct);
    }

    private WebApplicationFactory<Program> Site() => app.Factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Insurance:Oidc:Authority", "http://localhost/");
        b.ConfigureTestServices(s => s.AddOptions<OpenIdConnectOptions>(Bff.Challenge)
            .Configure<IServiceProvider>((o, sp) => o.BackchannelHttpHandler = new ToTheServer(sp)));
    });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Follows the redirects by hand (so each step can be looked at), returning every answer on the way.</summary>
    private static async Task<List<HttpResponseMessage>> FollowAsync(HttpClient client, string url)
    {
        var answers = new List<HttpResponseMessage>();
        for (var i = 0; i < 10; i++)
        {
            var r = await client.GetAsync(url);
            answers.Add(r);
            if (r.StatusCode != HttpStatusCode.Redirect || r.Headers.Location is not { } next) break;
            url = next.ToString();
        }
        return answers;
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> site, WebApp app, string role)
    {
        var (_, username, password) = await app.UserAsync(role);
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        Assert.Equal(HttpStatusCode.Redirect, (await WebApp.PostFormAsync(client, "/login", new() { ["username"] = username, ["password"] = password })).StatusCode);
        return client;
    }

    [Fact]
    public async Task The_browser_signs_in_to_the_api_through_the_provider_and_gets_only_its_own_cookie_no_token()
    {
        await using var site = Site();
        var browser = await SignedInAsync(site, app, "accountant");
        // The program's session cookie alone does not read the API here; the answer says where to sign in.
        var first = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(Bff.LoginPath, (await JsonAsync(first)).GetProperty("signIn").GetString());

        var steps = await FollowAsync(browser, Bff.LoginPath + "?returnUrl=" + Uri.EscapeDataString("/policies?status=active"));
        var locations = steps.Select(s => s.Headers.Location?.ToString() ?? "").ToList();
        Assert.Contains(locations, l => l.Contains("/connect/authorize?", StringComparison.Ordinal) && l.Contains("client_id=insurance-bff", StringComparison.Ordinal)
            && l.Contains("code_challenge_method=S256", StringComparison.Ordinal));
        Assert.Contains(locations, l => l.StartsWith("http://localhost" + Bff.CallbackPath + "?code=", StringComparison.Ordinal));
        Assert.Equal("/policies?status=active", locations[^2]);
        var setCookies = steps.SelectMany(s => s.Headers.TryGetValues("Set-Cookie", out var v) ? v : []).ToList();
        var bff = Assert.Single(setCookies, c => c.StartsWith(Bff.Cookie + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", bff, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", bff, StringComparison.OrdinalIgnoreCase);
        foreach (var s in steps) Assert.DoesNotContain("access_token", await s.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var me = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("accountant", (await JsonAsync(me)).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/policies?limit=1")).StatusCode);
    }

    [Fact]
    public async Task The_cookie_ends_with_the_session_or_the_user_and_the_return_address_is_only_local()
    {
        await using var site = Site();
        var browser = await SignedInAsync(site, app, "viewer");
        var steps = await FollowAsync(browser, Bff.LoginPath + "?returnUrl=" + Uri.EscapeDataString("//attacker.example/x"));
        Assert.Equal("/", steps[^1].Headers.Location?.ToString() ?? steps[^2].Headers.Location?.ToString());
        var cookie = steps.SelectMany(s => s.Headers.TryGetValues("Set-Cookie", out var v) ? v : []).Single(c => c.StartsWith(Bff.Cookie + "=", StringComparison.Ordinal)).Split(';')[0];
        async Task<HttpStatusCode> WithOnlyTheCookieAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
            request.Headers.Add("Cookie", cookie);
            return (await site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false }).SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, await WithOnlyTheCookieAsync());
        // Signing out ends the provider's session, the program's: the same cookie, kept by hand, is refused from then on.
        Assert.Equal(HttpStatusCode.Redirect, (await WebApp.PostFormAsync(browser, "/logout", [], "/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await WithOnlyTheCookieAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/me")).StatusCode);

        // A disabled user's cookie as well.
        var (_, username, password) = await app.UserAsync("accountant");
        var other = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await WebApp.PostFormAsync(other, "/login", new() { ["username"] = username, ["password"] = password });
        await FollowAsync(other, Bff.LoginPath);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/v1/me")).StatusCode);
        await using (var c = await app.Db.OpenAdminAsync())
            await c.ExecuteAsync("update core.app_user set is_active = 0 where username = @username", new { username });
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task The_backends_client_is_refused_without_its_secret_and_only_with_its_own_callback()
    {
        await using var site = Site();
        var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // A real code of the backend's client, taken by a signed-in browser; exchanged without the secret, or with another, it gives nothing.
        var signedIn = await SignedInAsync(site, app, "viewer");
        var verifier = new string('v', 50);
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var answer = await signedIn.GetAsync(IdentityProvider.AuthorizePath + "?client_id=insurance-bff&response_type=code&scope=openid&code_challenge=" + challenge
            + "&code_challenge_method=S256&redirect_uri=" + Uri.EscapeDataString("http://localhost" + Bff.CallbackPath));
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(answer.Headers.Location!.Query)["code"].ToString();
        Assert.NotEmpty(code);
        foreach (var secret in new string?[] { null, "wrong-secret-wrong-secret-wrong-secret" })
        {
            var form = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = Bff.Client, ["redirect_uri"] = "http://localhost" + Bff.CallbackPath, ["code_verifier"] = verifier };
            if (secret is not null) form["client_secret"] = secret;
            var r = await client.PostAsync(IdentityProvider.TokenPath, new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("invalid_client", (await JsonAsync(r)).GetProperty("error").GetString());
        }
        var browser = await SignedInAsync(site, app, "viewer");
        var other = await browser.GetAsync(IdentityProvider.AuthorizePath + "?client_id=insurance-bff&response_type=code&scope=openid&code_challenge=" + new string('a', 43)
            + "&code_challenge_method=S256&redirect_uri=" + Uri.EscapeDataString("https://attacker.example/signin-oidc"));
        Assert.DoesNotContain("attacker.example", other.Headers.Location?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_sites_address_the_backend_is_off_and_the_api_takes_the_session_cookie_as_before()
    {
        var (browser, _) = await app.SignedInAsync("viewer");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(Bff.LoginPath)).StatusCode);
    }
}
