using Insurance.Application.Ports;
using Insurance.Application.Services;
using Insurance.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Insurance.Web.Infrastructure;

/// <summary>
/// The request's user for the API and the use cases (ADR 0003): with an Authorization header, only its bearer access token counts — a
/// valid token of this site's provider for the API, whose session is still open, and which grants the API's scope (Codex R006-02) —
/// otherwise the session cookie, as the pages read it. A bad token never falls back to the cookie. Read once per request.
/// </summary>
public sealed class HttpCurrentUserSource(IHttpContextAccessor accessor, RequestUser requestUser, Auth auth) : ICurrentUserSource
{
    private Task<(CurrentUser? User, bool InsufficientScope)>? _read;

    public async Task<CurrentUser?> CurrentAsync() => (await (_read ??= ReadAsync())).User;

    public async Task<bool> InsufficientScopeAsync() => (await (_read ??= ReadAsync())).InsufficientScope;

    private async Task<(CurrentUser?, bool)> ReadAsync()
    {
        if (accessor.HttpContext is not { } context) return (null, false);
        if (!context.Request.Headers.Authorization.Any()) return (await requestUser.GetAsync(context), false);
        if (!IdentityProvider.Enabled(context.RequestServices.GetRequiredService<IHostEnvironment>(), context.RequestServices.GetRequiredService<IConfiguration>()))
            return (null, false);
        var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || !Guid.TryParse(result.Principal.FindFirst(Claims.Subject)?.Value, out var userId)) return (null, false);
        // A token of an ended session is no token at all (401); a live one without the API's scope is known but refused (403).
        if (await auth.UserForSessionAsync(userId, result.Principal.FindFirst(IdentityProvider.SessionClaim)?.Value) is not { } user) return (null, false);
        return result.Principal.HasScope(IdentityProvider.ApiScope) ? (user, false) : (null, true);
    }
}
