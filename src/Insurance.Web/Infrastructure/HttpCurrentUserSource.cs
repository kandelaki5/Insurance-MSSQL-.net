using Insurance.Application.Ports;
using Insurance.Application.Services;
using Insurance.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Insurance.Web.Infrastructure;

/// <summary>
/// The request's user for the API and the use cases (ADR 0003): with an Authorization header, only its bearer access token counts — a
/// valid token of this site's provider for the API, whose session is still open — otherwise the session cookie, as the pages read it.
/// Read once per request.
/// </summary>
public sealed class HttpCurrentUserSource(IHttpContextAccessor accessor, RequestUser requestUser, Auth auth) : ICurrentUserSource
{
    private Task<CurrentUser?>? _user;

    public Task<CurrentUser?> CurrentAsync() => _user ??= ReadAsync();

    private async Task<CurrentUser?> ReadAsync()
    {
        if (accessor.HttpContext is not { } context) return null;
        if (!context.Request.Headers.Authorization.Any()) return await requestUser.GetAsync(context);
        if (!IdentityProvider.Enabled(context.RequestServices.GetRequiredService<IHostEnvironment>(), context.RequestServices.GetRequiredService<IConfiguration>()))
            return null;
        var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || !Guid.TryParse(result.Principal.FindFirst(Claims.Subject)?.Value, out var userId)) return null;
        return await auth.UserForSessionAsync(userId, result.Principal.FindFirst(IdentityProvider.SessionClaim)?.Value);
    }
}
