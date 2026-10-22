using Insurance.Application.Services;
using Insurance.Web.Infrastructure;
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
        return Redirect("/login");
    }
}
