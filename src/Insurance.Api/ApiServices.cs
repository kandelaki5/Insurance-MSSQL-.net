using Insurance.Application.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Insurance.Api;

/// <summary>
/// The HTTP API under /api/v1 (ADR 0001/0004), served by the same site as the Razor pages and the Angular build (one origin). Errors are
/// application/problem+json with a stable code; a request without a signed-in user is answered 401, never redirected to the login page.
/// </summary>
public static class ApiServices
{
    public const string Prefix = "/api/v1";

    public static IServiceCollection AddInsuranceApi(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddOpenApi("v1", o => o.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new() { Title = "Insurance API", Version = "v1", Description = "სადაზღვევო პროგრამის API (INS-DOTNET-MSSQL-004). Money is { minor, currency, scale } with minor as a string; dates are YYYY-MM-DD." };
            return Task.CompletedTask;
        }));
        return services;
    }

    /// <summary>
    /// Maps the API. The OpenAPI document is served in Development, or where Insurance:Api:OpenApi is true (UAT); not in production
    /// otherwise.
    /// </summary>
    public static IEndpointRouteBuilder MapInsuranceApi(this IEndpointRouteBuilder app, IHostEnvironment environment, IConfiguration configuration)
    {
        if (environment.IsDevelopment() || configuration.GetValue<bool>("Insurance:Api:OpenApi")) app.MapOpenApi("/api/openapi/{documentName}.json");
        var api = app.MapGroup(Prefix);
        api.MapGet("/health", async (ISystemStatusQuery status, CancellationToken ct) =>
            {
                var s = await status.ReadAsync(ct);
                return Results.Ok(new HealthResponse("ok", s.Migrations, s.LastMigration));
            })
            .WithSummary("The database is reachable; how many migrations are applied and the last of them.")
            .AllowAnonymous();

        var signedIn = api.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            var users = context.HttpContext.RequestServices.GetRequiredService<ICurrentUserSource>();
            if (await users.CurrentAsync() is not null) return await next(context);
            return await users.InsufficientScopeAsync() ? Problems.InsufficientScope(context.HttpContext) : Problems.NotSignedIn();
        });
        signedIn.MapGet("/me", async (ICurrentUserSource users) =>
            {
                var u = (await users.CurrentAsync())!;
                return Results.Ok(new MeResponse(u.Username, u.FullName, u.RoleCode, u.RoleName, u.CanWrite));
            })
            .WithSummary("The signed-in user: name, role and whether the role may write (finer rights are checked per action).")
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        PolicyEndpoints.Map(signedIn);
        PolicyCardEndpoints.Map(signedIn);
        return app;
    }
}

public sealed record HealthResponse(string Status, int Migrations, string? LastMigration);

public sealed record MeResponse(string Username, string FullName, string Role, string RoleName, bool CanWrite);

/// <summary>The API's refusals as problem details with a stable code (ADR 0004).</summary>
public static class Problems
{
    public const string NotSignedInCode = "not-signed-in";
    public const string InvalidPagingCode = "invalid-paging";
    public const string NoSuchPolicyCode = "no-such-policy";
    public const string InsufficientScopeCode = "insufficient-scope";

    /// <summary>The API's scope a bearer token must grant (ADR 0003).</summary>
    public const string ApiScope = "api";

    public static IResult NotSignedIn() => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "სისტემაში შესვლა საჭიროა",
        extensions: new Dictionary<string, object?> { ["code"] = NotSignedInCode });

    public static IResult InvalidPaging(int maxLimit) => Results.Problem(statusCode: StatusCodes.Status400BadRequest,
        title: $"გვერდის პარამეტრები არასწორია: offset ≥ 0, limit 1-დან {maxLimit}-მდე",
        extensions: new Dictionary<string, object?> { ["code"] = InvalidPagingCode });

    /// <summary>A valid token without the API's scope (RFC 6750 §3.1): 403 with the Bearer challenge naming the scope it lacks.</summary>
    public static IResult InsufficientScope(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"{ApiScope}\"";
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "ტოკენი API-ის უფლებას (scope \"api\") არ შეიცავს",
            extensions: new Dictionary<string, object?> { ["code"] = InsufficientScopeCode });
    }

    public static IResult NoSuchPolicy() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "ასეთი პოლისი არ არსებობს",
        extensions: new Dictionary<string, object?> { ["code"] = NoSuchPolicyCode });
}
