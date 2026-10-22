using Insurance.Application.Services;

namespace Insurance.Web.Infrastructure;

/// <summary>The signed-in user of this request, read once from the session cookie (legacy currentUser, cached per request).</summary>
public sealed class RequestUser(Auth auth)
{
    private CurrentUser? _user;
    private bool _read;

    public async Task<CurrentUser?> GetAsync(HttpContext context)
    {
        if (_read) return _user;
        _user = await auth.UserForTokenAsync(context.Request.Cookies[Auth.Cookie]);
        _read = true;
        return _user;
    }

    /// <summary>The user read by <see cref="GetAsync"/>; pages run after RequireUserFilter, so it is set there.</summary>
    public CurrentUser Current => _user ?? throw new InvalidOperationException("no signed-in user");

    public static void SetCookie(HttpContext context, string token) =>
        context.Response.Cookies.Append(Auth.Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromHours(Auth.SessionHours),
        });

    public static void ClearCookie(HttpContext context) => context.Response.Cookies.Delete(Auth.Cookie, new CookieOptions { Path = "/" });
}
