using Insurance.Application.Services;

namespace Insurance.Application.Ports;

/// <summary>
/// The signed-in user of the request, as the host's session gives it (ADR 0003): the actor of every use case is this server-side
/// app_user, never a value from a request body. Null when nobody is signed in.
/// </summary>
public interface ICurrentUserSource
{
    Task<CurrentUser?> CurrentAsync();
}
