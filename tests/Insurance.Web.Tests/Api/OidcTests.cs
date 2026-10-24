using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Insurance.Web.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace Insurance.Web.Tests.Api;

/// <summary>
/// The embedded OpenID Connect provider of ADR 0003 (INS-DOTNET-MSSQL-004 B3), over HTTP: a signed-in user's browser gets an
/// authorization code only with PKCE (S256), only for the Swagger client and only to its callback on this site; the code is exchanged
/// once, with its verifier; the access token reads /api/v1 as that user — and stops the moment the session it was issued in ends or
/// the user is disabled. Somebody not signed in is sent to the program's own sign-in and back, and the sign-in's return address can
/// be nothing but the authorization endpoint. Outside Development, without configuration, none of it is served.
/// </summary>
public sealed class OidcTests(WebApp app) : IClassFixture<WebApp>
{
    private const string Callback = "http://localhost" + IdentityProvider.SwaggerRedirectPath;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string AuthorizeUrl(string challenge, string state = "s-1", string client = IdentityProvider.SwaggerClient, string callback = Callback,
        string method = "S256", string scope = "openid api") =>
        QueryHelpers.AddQueryString(IdentityProvider.AuthorizePath, new Dictionary<string, string?>
        {
            ["client_id"] = client, ["redirect_uri"] = callback, ["response_type"] = "code", ["scope"] = scope, ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = method,
        });

    private static string? CodeOf(HttpResponseMessage r)
    {
        if (r.StatusCode != HttpStatusCode.Redirect || r.Headers.Location is not { } to || !to.ToString().StartsWith(Callback + "?", StringComparison.Ordinal)) return null;
        return QueryHelpers.ParseQuery(to.Query).TryGetValue("code", out var code) ? code.ToString() : null;
    }

    private static Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string code, string verifier, string clientId = IdentityProvider.SwaggerClient) =>
        client.PostAsync(IdentityProvider.TokenPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Callback, ["client_id"] = clientId, ["code_verifier"] = verifier,
        }));

    private async Task<(HttpClient Browser, Guid UserId, string AccessToken)> TokenAsync(string role, string scope = "openid api")
    {
        var (browser, userId) = await app.SignedInAsync(role);
        var (verifier, challenge) = Pkce();
        var code = CodeOf(await browser.GetAsync(AuthorizeUrl(challenge, scope: scope)));
        Assert.NotNull(code);
        var r = await ExchangeAsync(app.Client(), code, verifier);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.InRange(body.GetProperty("expires_in").GetInt32(), 1, IdentityProvider.AccessTokenMinutes * 60);
        Assert.False(body.TryGetProperty("refresh_token", out _));
        return (browser, userId, body.GetProperty("access_token").GetString()!);
    }

    private Task<HttpResponseMessage> MeAsync(string token, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client ?? app.Client()).SendAsync(request);
    }

    [Fact]
    public async Task The_discovery_document_offers_the_code_flow_with_S256_only()
    {
        var doc = JsonDocument.Parse(await (await app.Client().GetAsync("/.well-known/openid-configuration")).Content.ReadAsStringAsync()).RootElement;
        Assert.EndsWith(IdentityProvider.AuthorizePath, doc.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith(IdentityProvider.TokenPath, doc.GetProperty("token_endpoint").GetString());
        Assert.Equal(["code"], doc.GetProperty("response_types_supported").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["authorization_code"], doc.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["S256"], doc.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task A_signed_in_user_gets_a_code_with_PKCE_its_token_reads_the_api_as_that_user_and_the_code_is_used_once()
    {
        var (browser, _) = await app.SignedInAsync("accountant");
        var (verifier, challenge) = Pkce();
        var answer = await browser.GetAsync(AuthorizeUrl(challenge, state: "state-42"));
        var code = CodeOf(answer);
        Assert.NotNull(code);
        Assert.Equal("state-42", QueryHelpers.ParseQuery(answer.Headers.Location!.Query)["state"].ToString());

        // A wrong verifier gets nothing (and does not spend the code).
        Assert.Equal(HttpStatusCode.BadRequest, (await ExchangeAsync(app.Client(), code, Pkce().Verifier)).StatusCode);
        var r = await ExchangeAsync(app.Client(), code, verifier);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var token = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
        var me = await MeAsync(token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("accountant", JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("role").GetString());

        // The same code a second time, with the right verifier: refused, and no row of the code itself is kept, only its hash.
        var again = await ExchangeAsync(app.Client(), code, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_grant", JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        await using var c = await app.Db.OpenAdminAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("select count(*) from core.oidc_code_use where code_hash = @code", new { code }));
    }

    [Fact]
    public async Task No_code_without_S256_for_another_client_or_to_another_address()
    {
        var (browser, _) = await app.SignedInAsync("viewer");
        var (_, challenge) = Pkce();
        // No PKCE, or "plain": an error to the client's own callback, never a code.
        var noPkce = await browser.GetAsync(AuthorizeUrl(challenge).Replace("&code_challenge=" + challenge, "", StringComparison.Ordinal).Replace("&code_challenge_method=S256", "", StringComparison.Ordinal));
        Assert.Null(CodeOf(noPkce));
        Assert.Null(CodeOf(await browser.GetAsync(AuthorizeUrl(challenge, method: "plain"))));
        // An unknown client, or a callback on another site: no redirect there at all.
        foreach (var r in new[] { await browser.GetAsync(AuthorizeUrl(challenge, client: "someone-else")),
                                  await browser.GetAsync(AuthorizeUrl(challenge, callback: "https://attacker.example/cb")) })
        {
            Assert.Null(CodeOf(r));
            Assert.DoesNotContain("attacker.example", r.Headers.Location?.ToString() ?? "", StringComparison.Ordinal);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
    }

    [Fact]
    public async Task The_token_ends_when_its_session_ends_or_its_user_is_disabled_and_a_forged_token_is_nothing()
    {
        var (browser, _, token) = await TokenAsync("viewer");
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(token)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await WebApp.PostFormAsync(browser, "/logout", [], "/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(token)).StatusCode);

        var (_, userId, other) = await TokenAsync("accountant");
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(other)).StatusCode);
        await using (var c = await app.Db.OpenAdminAsync())
            await c.ExecuteAsync("update core.app_user set is_active = 0 where id = @userId", new { userId });
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(other)).StatusCode);

        // A changed token, or a bad token beside a valid session cookie: the header is the only credential then.
        var (signedIn, _, good) = await TokenAsync("viewer");
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(good[..^4] + "AAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync("not-a-token", signedIn)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Not_signed_in_the_user_signs_in_and_comes_back_and_the_return_address_is_only_the_authorization_endpoint()
    {
        var (_, username, password) = await app.UserAsync("viewer");
        var browser = app.Client();
        var (verifier, challenge) = Pkce();
        var authorize = AuthorizeUrl(challenge);
        var toLogin = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, toLogin.StatusCode);
        var login = toLogin.Headers.Location!.ToString();
        Assert.StartsWith("/login?returnUrl=", login, StringComparison.Ordinal);
        var signedIn = await WebApp.PostFormAsync(browser, login, new() { ["username"] = username, ["password"] = password }, login);
        Assert.Equal(authorize, signedIn.Headers.Location!.ToString());
        var code = CodeOf(await browser.GetAsync(authorize));
        Assert.NotNull(code);
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(app.Client(), code, verifier)).StatusCode);

        // Any other return address is ignored: the sign-in goes home.
        var (_, u2, p2) = await app.UserAsync("viewer");
        foreach (var evil in new[] { "https://attacker.example/", "//attacker.example/", "/connect/authorizeX?x=1", "/\\attacker.example" })
        {
            var page = "/login?returnUrl=" + Uri.EscapeDataString(evil);
            var r = await WebApp.PostFormAsync(app.Client(), page, new() { ["username"] = u2, ["password"] = p2 }, page);
            Assert.Equal("/", r.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Outside_Development_without_configuration_there_is_no_provider_and_no_swagger_and_a_bearer_is_refused()
    {
        await using var production = app.Factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = production.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.well-known/openid-configuration")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/swagger/index.html")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync(AuthorizeUrl(Pkce().Challenge))).StatusCode);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anything");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Swagger_UI_is_served_in_Development_and_the_document_names_the_code_flow()
    {
        Assert.Equal(HttpStatusCode.OK, (await app.Client().GetAsync("/api/swagger/index.html")).StatusCode);
        var doc = JsonDocument.Parse(await (await app.Client().GetAsync("/api/openapi/v1.json")).Content.ReadAsStringAsync()).RootElement;
        var flow = doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("oauth2").GetProperty("flows").GetProperty("authorizationCode");
        Assert.Equal(IdentityProvider.AuthorizePath, flow.GetProperty("authorizationUrl").GetString());
        Assert.Equal(IdentityProvider.TokenPath, flow.GetProperty("tokenUrl").GetString());
    }

    /// <summary>
    /// Codex R006-02: a real token of a live session obtained with scope=openid only is known but not let in — 403 insufficient_scope
    /// with the Bearer challenge naming "api", never a fallback to the session cookie beside it; with "api" it is let in. A bad token
    /// stays 401.
    /// </summary>
    [Fact]
    public async Task A_token_without_the_api_scope_is_403_insufficient_scope_even_beside_a_valid_cookie_and_one_with_it_reads_the_api()
    {
        var (browser, _, openIdOnly) = await TokenAsync("accountant", scope: "openid");
        foreach (var client in new[] { app.Client(), browser })
        {
            var r = await MeAsync(openIdOnly, client);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Insurance.Api.Problems.InsufficientScopeCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
            Assert.Equal("Bearer error=\"insufficient_scope\", scope=\"api\"", r.Headers.WwwAuthenticate.ToString());
            var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/policies");
            list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openIdOnly);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(list)).StatusCode);
        }
        var (_, _, withApi) = await TokenAsync("accountant");
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(withApi)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(openIdOnly[..^4] + "AAAA", browser)).StatusCode);
        // Once its session has ended, the openid-only token is nothing (401), not merely short of a scope.
        Assert.Equal(HttpStatusCode.Redirect, (await WebApp.PostFormAsync(browser, "/logout", [], "/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(openIdOnly)).StatusCode);
    }
}
