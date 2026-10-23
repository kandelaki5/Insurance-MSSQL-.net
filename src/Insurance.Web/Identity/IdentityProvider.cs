using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Insurance.Application.Services;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Insurance.Web.Identity;

/// <summary>
/// The embedded OpenID Connect provider of ADR 0003 (OpenIddict, degraded mode: no token store), over the program's own users and
/// sessions. It issues authorization codes only with PKCE (S256), to a client named here, at a redirect address on this same site;
/// access tokens are for the audience <see cref="Audience"/>, carry the user's id and the session they were issued in, and live
/// <see cref="AccessTokenMinutes"/> minutes — but count only while that session is open (Auth.UserForSessionAsync). No refresh token.
/// It runs in Development, and elsewhere only when Insurance:Oidc:Enabled is true with the signing and encryption certificates
/// configured (files outside Git, Insurance:Oidc:SigningCertificate / EncryptionCertificate with their passwords).
/// </summary>
public static class IdentityProvider
{
    public const string Audience = "insurance-api";
    public const string ApiScope = "api";
    public const string SwaggerClient = "insurance-swagger";
    public const string SwaggerRedirectPath = "/api/swagger/oauth2-redirect.html";
    public const string AuthorizePath = "/connect/authorize";
    public const string TokenPath = "/connect/token";
    public const int AccessTokenMinutes = 30;

    /// <summary>The claim of the session a token was issued in (OpenID Connect's "sid").</summary>
    public const string SessionClaim = "sid";

    public static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() || configuration.GetValue<bool>("Insurance:Oidc:Enabled");

    public static IServiceCollection AddInsuranceIdentity(this IServiceCollection services, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!Enabled(environment, configuration)) return services;
        services.AddOpenIddict()
            .AddServer(o =>
            {
                o.SetAuthorizationEndpointUris(AuthorizePath[1..]).SetTokenEndpointUris(TokenPath[1..]);
                o.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
                // S256 only: "plain" would send the verifier itself as the challenge.
                o.Configure(x => x.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));
                o.RegisterScopes(Scopes.OpenId, Scopes.Profile, ApiScope);
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(AccessTokenMinutes)).SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(2));
                o.EnableDegradedMode();
                if (environment.IsDevelopment()) o.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                else o.AddEncryptionCertificate(Certificate(configuration, "Encryption")).AddSigningCertificate(Certificate(configuration, "Signing"));
                var aspNet = o.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
                if (environment.IsDevelopment()) aspNet.DisableTransportSecurityRequirement();
                o.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(b => b.UseInlineHandler(ValidateAuthorization));
                o.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(b => b.UseInlineHandler(ValidateToken));
                o.AddEventHandler<OpenIddictServerEvents.HandleTokenRequestContext>(b => b.UseInlineHandler(UseCodeOnce));
            })
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.UseAspNetCore();
                o.AddAudiences(Audience);
            });
        return services;
    }

    private static X509Certificate2 Certificate(IConfiguration configuration, string use)
    {
        var path = configuration[$"Insurance:Oidc:{use}Certificate"]
            ?? throw new InvalidOperationException($"Insurance:Oidc:{use}Certificate is not set (ADR 0003): the OpenID Connect provider needs its own certificate outside Development");
        return X509CertificateLoader.LoadPkcs12FromFile(path, configuration[$"Insurance:Oidc:{use}CertificatePassword"]);
    }

    /// <summary>This site's address of a path, as the request reached it (the forwarded scheme and host only from a trusted proxy).</summary>
    private static string Here(HttpRequest request, string path) => $"{request.Scheme}://{request.Host}{request.PathBase}{path}";

    /// <summary>The clients: Swagger UI only (public, PKCE, its own callback on this site).</summary>
    private static ValueTask ValidateAuthorization(OpenIddictServerEvents.ValidateAuthorizationRequestContext context)
    {
        var request = context.Transaction.GetHttpRequest()!;
        if (context.ClientId != SwaggerClient)
            context.Reject(Errors.InvalidClient, "unknown client");
        else if (context.RedirectUri != Here(request, SwaggerRedirectPath))
            context.Reject(Errors.InvalidRequest, "the redirect address is not this client's");
        return default;
    }

    private static ValueTask ValidateToken(OpenIddictServerEvents.ValidateTokenRequestContext context)
    {
        if (context.ClientId != SwaggerClient || !string.IsNullOrEmpty(context.ClientSecret))
            context.Reject(Errors.InvalidClient, "unknown client");
        return default;
    }

    /// <summary>A valid code is exchanged once; its second exchange is refused, as an unknown code would be (0014).</summary>
    private static async ValueTask UseCodeOnce(OpenIddictServerEvents.HandleTokenRequestContext context)
    {
        if (!context.Request.IsAuthorizationCodeGrantType()) return;
        var auth = context.Transaction.GetHttpRequest()!.HttpContext.RequestServices.GetRequiredService<Auth>();
        if (!await auth.FirstUseOfCodeAsync(context.Request.Code!)) context.Reject(Errors.InvalidGrant, "the authorization code was already used");
    }

    public static IEndpointRouteBuilder MapInsuranceIdentity(this IEndpointRouteBuilder app, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!Enabled(environment, configuration)) return app;
        // The authorization endpoint: the program's own sign-in decides who the user is; nobody signed in is sent to it and back.
        app.MapMethods(AuthorizePath, [HttpMethods.Get, HttpMethods.Post], async (HttpContext context, Auth auth) =>
        {
            var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("not an OpenID Connect request");
            var token = context.Request.Cookies[Auth.Cookie];
            if (await auth.UserForTokenAsync(token) is not { } user)
                return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(context.Request.PathBase + context.Request.Path + context.Request.QueryString));
            var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, user.Id.ToString()).SetClaim(Claims.Name, user.Username).SetClaim(SessionClaim, Auth.SessionId(token!));
            identity.SetScopes(request.GetScopes().Intersect([Scopes.OpenId, Scopes.Profile, ApiScope]));
            identity.SetResources(Audience);
            identity.SetDestinations(claim => claim.Type == Claims.Name ? [Destinations.AccessToken, Destinations.IdentityToken] : [Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }).ExcludeFromDescription();
        return app;
    }

    /// <summary>The local path of a sign-in's return address, only the authorization endpoint's (so the login page is no open redirect).</summary>
    public static string? ReturnPath(string? returnUrl) =>
        returnUrl is not null && returnUrl.StartsWith(AuthorizePath + "?", StringComparison.Ordinal) ? returnUrl : null;
}
