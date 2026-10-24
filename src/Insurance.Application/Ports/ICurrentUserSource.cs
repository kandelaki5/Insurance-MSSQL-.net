using Insurance.Application.Services;

namespace Insurance.Application.Ports;

/// <summary>
/// The signed-in user of the request, as the host's session gives it (ADR 0003): the actor of every use case is this server-side
/// app_user, never a value from a request body. Null when nobody is signed in.
/// </summary>
public interface ICurrentUserSource
{
    Task<CurrentUser?> CurrentAsync();

    /// <summary>
    /// True when the request carried a valid access token of a live session that does not grant the API's scope: the caller is known
    /// but may not use the API with that token (403 insufficient_scope, not 401). Always false for the session cookie.
    /// </summary>
    Task<bool> InsufficientScopeAsync();
}
