using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Insurance.Web.Pages;

[AllowAnonymousPage]
public sealed class LoginModel(Auth auth, RequestUser requestUser, IConfiguration configuration) : PageModel
{
    public string? FormError { get; private set; }

    public async Task<IActionResult> OnGetAsync() =>
        await requestUser.GetAsync(HttpContext) is not null ? Redirect("/") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var username = Request.Form["username"].Count == 1 ? Request.Form["username"][0] ?? "" : "";
        var password = Request.Form["password"].Count == 1 ? Request.Form["password"][0] ?? "" : "";
        var userId = await auth.LoginAsync(username, password, Source());
        if (userId is null)
        {
            FormError = Ka.Login.Invalid;
            return Page();
        }
        RequestUser.SetCookie(HttpContext, await auth.CreateSessionAsync(userId.Value));
        return Redirect("/");
    }

    /// <summary>
    /// The network source that login throttling counts. Behind a reverse proxy (Insurance:TrustForwardedFor) the first
    /// address of X-Forwarded-For, as the legacy code took it; otherwise the connection's own address, which the client
    /// cannot forge.
    /// </summary>
    private string? Source()
    {
        if (configuration.GetValue<bool>("Insurance:TrustForwardedFor"))
        {
            var forwarded = Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            if (forwarded.Length > 0) return forwarded;
            var real = Request.Headers["X-Real-IP"].ToString();
            if (real.Length > 0) return real;
        }
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
