using Insurance.Api;
using Insurance.Application;
using Insurance.Application.Data;
using Insurance.Application.Ports;
using Insurance.Infrastructure;
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
// The layers (docs/architecture, ADR 0001): the adapters behind the Application's ports and the /api/v1 endpoints, in this one site.
builder.Services.AddInsuranceInfrastructure();
builder.Services.AddInsuranceApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserSource, HttpCurrentUserSource>();
builder.Services.Configure<SanctionsOptions>(builder.Configuration.GetSection("Insurance:Sanctions"));

// Georgian (and every other) text is written as it is, not as &#x...; entities.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(System.Text.Unicode.UnicodeRanges.All));
builder.Services.AddRazorPages(options =>
{
    // Every page needs a signed-in user except the login page; each page checks its own permission on top.
    options.Conventions.ConfigureFilter(new RequireUserFilter());
});
builder.AddTrustedProxies();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "csrf";
    options.Cookie.SecurePolicy = Hosting.AntiforgeryPolicy(builder.Environment);
    options.Cookie.SameSite = SameSiteMode.Lax;
});

var app = builder.Build();

// First, so the scheme and the client's address are the proxy's word only when the proxy is a trusted one (Hosting).
app.UseForwardedHeaders();
app.RequireHttpsOutsideDevelopment();
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
app.MapInsuranceApi(app.Environment, app.Configuration);
app.Run();

public partial class Program;
