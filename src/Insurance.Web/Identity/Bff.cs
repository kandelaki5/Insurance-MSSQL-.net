using System.Security.Claims;
using System.Security.Cryptography;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Insurance.Web.Identity;

/// <summary>
/// The browser's side of ADR 0003, a same-origin backend for frontend. The server is the OpenID Connect client <see cref="Client"/>
/// (confidential: its secret never leaves the server) of the provider at Insurance:Oidc:Authority — today the embedded one, an
/// external provider later behind the same handler. /bff/login sends the browser to the provider (Authorization Code with PKCE); at
/// /signin-oidc the server redeems the code, checks the ID token, keeps no token at all and gives the browser only its own
/// HttpOnly cookie <see cref="Cookie"/>, which holds the user's id and the provider session it was issued in. The API takes that
/// cookie (or a bearer token), never the program's own session cookie; and since the provider's session is the program's, signing
/// out or disabling the user ends the cookie's access at once (Auth.UserForSessionAsync), whatever its lifetime.
/// It runs where the provider runs and Insurance:Oidc:Authority (this site's public address) is set; elsewhere the API keeps taking
/// the program's session cookie, as before.
/// </summary>
public static class Bff
{
    public const string Client = "insurance-bff";
    public const string CallbackPath = "/signin-oidc";
    public const string LoginPath = "/bff/login";
    public const string Scheme = "bff";
    public const string Cookie = "bff";
    public const string Challenge = OpenIdConnectDefaults.AuthenticationScheme;

    public static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        IdentityProvider.Enabled(environment, configuration) && !string.IsNullOrEmpty(configuration["Insurance:Oidc:Authority"]);

    /// <summary>
    /// The client's secret: Insurance:Oidc:BffSecret (outside Git, like the certificates), or in Development one made up for this
    /// process (the provider and its client are the same process, so nothing else ever needs it).
    /// </summary>
    public sealed class Secret(IHostEnvironment environment, IConfiguration configuration)
    {
        public string Value { get; } = configuration["Insurance:Oidc:BffSecret"] is { Length: >= 32 } s ? s
            : environment.IsDevelopment() ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : throw new InvalidOperationException("Insurance:Oidc:BffSecret (32 characters or more) is not set (ADR 0003)");

        public bool Matches(string? presented) =>
            presented is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(presented), System.Text.Encoding.UTF8.GetBytes(Value));
    }

    public static IServiceCollection AddInsuranceBff(this IServiceCollection services, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!Enabled(environment, configuration)) return services;
        services.AddSingleton<Secret>();
        services.AddAuthentication()
            .AddCookie(Scheme, o =>
            {
                o.Cookie.Name = Cookie;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.ExpireTimeSpan = TimeSpan.FromHours(Insurance.Application.Services.Auth.SessionHours);
                o.SlidingExpiration = false;
                // The API answers for itself (401 problem details); the cookie handler never redirects.
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            })
            .AddOpenIdConnect(Challenge, o =>
            {
                o.SignInScheme = Scheme;
                o.Authority = configuration["Insurance:Oidc:Authority"];
                o.RequireHttpsMetadata = !environment.IsDevelopment();
                o.ClientId = Client;
                o.ResponseType = OpenIdConnectResponseType.Code;
                // A top-level GET back to the site, so the Lax correlation and nonce cookies come with it.
                o.ResponseMode = OpenIdConnectResponseMode.Query;
                o.UsePkce = true;
                foreach (var cookie in new[] { o.CorrelationCookie, o.NonceCookie })
                {
                    cookie.SameSite = SameSiteMode.Lax;
                    cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                }
                o.CallbackPath = CallbackPath;
                o.Scope.Clear();
                o.Scope.Add(Scopes.OpenId);
                o.Scope.Add(IdentityProvider.ApiScope);
                // No token is kept: the server needs none (the API is this site), the browser never gets one.
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.MapInboundClaims = false;
                o.TokenValidationParameters.NameClaimType = Claims.Name;
                o.Events.OnAuthorizationCodeReceived = c =>
                {
                    c.TokenEndpointRequest!.ClientSecret = c.HttpContext.RequestServices.GetRequiredService<Secret>().Value;
                    return Task.CompletedTask;
                };
                o.Events.OnTokenValidated = c =>
                {
                    // Only what the API needs to find the user and the session again: the subject and the session.
                    var principal = c.Principal!;
                    var identity = new ClaimsIdentity(Scheme, Claims.Name, Claims.Role);
                    foreach (var type in new[] { Claims.Subject, Claims.Name, IdentityProvider.SessionClaim })
                        if (principal.FindFirst(type) is { } claim) identity.AddClaim(new Claim(type, claim.Value));
                    c.Principal = new ClaimsPrincipal(identity);
                    return Task.CompletedTask;
                };
            });
        return services;
    }

    public static IEndpointRouteBuilder MapInsuranceBff(this IEndpointRouteBuilder app, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!Enabled(environment, configuration)) return app;
        // Sign-in to the API for the browser: a local return address only.
        app.MapGet(LoginPath, (string? returnUrl) =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = LocalPath(returnUrl) }, [Challenge])).ExcludeFromDescription();
        return app;
    }

    /// <summary>A path of this site ("/…", never "//…" or "/\…"), else the home page.</summary>
    public static string LocalPath(string? returnUrl) =>
        returnUrl is { Length: > 0 } r && r[0] == '/' && (r.Length == 1 || (r[1] != '/' && r[1] != '\\')) ? r : "/";
}
