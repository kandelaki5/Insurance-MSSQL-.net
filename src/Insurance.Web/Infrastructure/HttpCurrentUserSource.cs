using Insurance.Application.Ports;
using Insurance.Application.Services;

namespace Insurance.Web.Infrastructure;

/// <summary>The session's user for the API and the use cases: the same read as the pages' RequireUserFilter, once per request.</summary>
public sealed class HttpCurrentUserSource(IHttpContextAccessor accessor, RequestUser requestUser) : ICurrentUserSource
{
    public Task<CurrentUser?> CurrentAsync() =>
        accessor.HttpContext is { } context ? requestUser.GetAsync(context) : Task.FromResult<CurrentUser?>(null);
}
