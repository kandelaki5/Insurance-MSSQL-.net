using Insurance.Application.Services;
using Insurance.Application.Texts;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Insurance.Web.Identity;

namespace Insurance.Web.Pages;

[AllowAnonymousPage]
public sealed class LoginModel(Auth auth, RequestUser requestUser) : PageModel
{
    public string? FormError { get; private set; }

    /// <summary>
    /// Where a sign-in goes: home, or back to the OpenID Connect authorization request that sent the visitor here (ADR 0003) —
    /// only that endpoint of this site, never another address.
    /// </summary>
    private string Next() => IdentityProvider.ReturnPath(Request.Query["returnUrl"].Count == 1 ? Request.Query["returnUrl"][0] : null) ?? "/";

    public async Task<IActionResult> OnGetAsync() =>
        await requestUser.GetAsync(HttpContext) is not null ? Redirect(Next()) : Page();

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
        return Redirect(Next());
    }

    /// <summary>
    /// The network source that login throttling counts: the connection's address, which the forwarded-headers middleware
    /// replaces with the client's only when the request came through a trusted proxy (Hosting). The legacy code took the
    /// first address of X-Forwarded-For from anyone, which let a client pick the key it was throttled by.
    /// </summary>
    private string? Source() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
