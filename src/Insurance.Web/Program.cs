using Insurance.Application.Data;
using Insurance.Application.Services;
using Insurance.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The application's connection (a member of role core_app only) comes from configuration — environment variable
// ConnectionStrings__Insurance or user secrets — never from code or a committed file.
var connectionString = builder.Configuration.GetConnectionString("Insurance")
    ?? throw new InvalidOperationException("ConnectionStrings:Insurance is not set (see README.md)");
builder.Services.AddSingleton(new DbOptions { ConnectionString = connectionString });
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Auth>();
builder.Services.AddScoped<RequestUser>();
builder.Services.AddHttpClient();
builder.Services.AddApplicationServices();

builder.Services.AddRazorPages(options =>
{
    // Every page needs a signed-in user except the login page; each page checks its own permission on top.
    options.Conventions.ConfigureFilter(new RequireUserFilter());
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    // The pages hold personal and financial data: never cached, never framed.
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.Run();

public partial class Program;
