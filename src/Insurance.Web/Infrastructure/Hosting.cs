using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Insurance.Web.Infrastructure;

/// <summary>
/// HTTPS behind a reverse proxy and the cookies' Secure flag (Codex R03). The legacy code marked its session cookie Secure
/// in production whatever the request looked like (secure: NODE_ENV === 'production'); the rewrite does the same outside
/// Development, so a proxy or IIS that ends TLS and talks plain HTTP to the program still gets Secure cookies. A proxy's
/// X-Forwarded-For and X-Forwarded-Proto are believed only from the addresses configured as proxies
/// (Insurance:ForwardedHeaders:KnownProxies, KnownNetworks) and from this machine itself; from anyone else they are
/// ignored, so a client cannot change the scheme or the address that login throttling counts.
/// </summary>
public static class Hosting
{
    public static void AddTrustedProxies(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Insurance:ForwardedHeaders");
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // The defaults (this machine's loopback addresses) stay; the configured proxies are added to them.
            foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            foreach (var network in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        });
    }

    /// <summary>
    /// Outside Development the program answers HTTPS requests only: a request that is not HTTPS, nor forwarded as HTTPS by a
    /// trusted proxy, is refused with an explanation and sets no cookie. Behind a proxy that is not configured this fails at
    /// once and visibly, instead of handing out cookies a browser would send over plain HTTP.
    /// </summary>
    public static IApplicationBuilder RequireHttpsOutsideDevelopment(this WebApplication app)
    {
        if (app.Environment.IsDevelopment()) return app;
        return app.Use(async (context, next) =>
        {
            if (context.Request.IsHttps) { await next(); return; }
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(HttpsRequired);
        });
    }

    public const string HttpsRequired =
        "პროგრამა მხოლოდ HTTPS-ით მუშაობს. თუ ის reverse proxy-ს ან ბალანსერის უკანაა, proxy-ის მისამართი მიუთითეთ პარამეტრში Insurance:ForwardedHeaders:KnownProxies (იხ. README).";

    /// <summary>Secure on every cookie outside Development; in Development (a local HTTP test run) only on an HTTPS request.</summary>
    public static bool SecureCookies(HttpContext context) =>
        !context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment() || context.Request.IsHttps;

    public static CookieSecurePolicy AntiforgeryPolicy(IWebHostEnvironment environment) =>
        environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
}
