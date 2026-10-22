using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Insurance.Web.Infrastructure;

/// <summary>Sends a visitor without a valid session to /login (legacy requireUser), except on pages marked [AllowAnonymousPage].</summary>
public sealed class RequireUserFilter : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var requestUser = context.HttpContext.RequestServices.GetRequiredService<RequestUser>();
        var user = await requestUser.GetAsync(context.HttpContext);
        if (user is null && context.HandlerInstance.GetType().GetCustomAttributes(typeof(AllowAnonymousPageAttribute), true).Length == 0)
        {
            context.Result = new RedirectResult("/login");
            return;
        }
        await next();
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class AllowAnonymousPageAttribute : Attribute;
