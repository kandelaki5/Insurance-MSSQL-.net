using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Insurance.Web.Pages;

[AllowAnonymousPage]
public sealed class LoginModel(Auth auth, RequestUser requestUser) : PageModel
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
    /// The network source that login throttling counts: the connection's address, which the forwarded-headers middleware
    /// replaces with the client's only when the request came through a trusted proxy (Hosting). The legacy code took the
    /// first address of X-Forwarded-For from anyone, which let a client pick the key it was throttled by.
    /// </summary>
    private string? Source() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
