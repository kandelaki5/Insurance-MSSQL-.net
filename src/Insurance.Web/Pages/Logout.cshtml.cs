using Insurance.Application.Services;
using Insurance.Web.Identity;
using Insurance.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Insurance.Web.Pages;

[AllowAnonymousPage]
public sealed class LogoutModel(Auth auth) : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        if (Request.Cookies[Auth.Cookie] is { Length: > 0 } token) await auth.DestroySessionAsync(token);
        RequestUser.ClearCookie(HttpContext);
        // The backend for the browser's cookie goes with it (it would be refused anyway: its session has just ended).
        if (Bff.Enabled(HttpContext.RequestServices.GetRequiredService<IHostEnvironment>(), HttpContext.RequestServices.GetRequiredService<IConfiguration>()))
            await HttpContext.SignOutAsync(Bff.Scheme);
        return Redirect("/login");
    }
}
