using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Insurance.Web.Identity;

/// <summary>
/// Swagger UI at /api/swagger, where the OpenAPI document is served (Development, or Insurance:Api:OpenApi), with Authorize through
/// the embedded provider: Authorization Code with PKCE for the public client insurance-swagger, no secret in the page (ADR 0003).
/// </summary>
public static class SwaggerUi
{
    public const string Path = "api/swagger";

    public static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() || configuration.GetValue<bool>("Insurance:Api:OpenApi");

    /// <summary>The OpenAPI document's OAuth 2.0 scheme, when the provider runs: every /api/v1 operation may be called with its token.</summary>
    public static IServiceCollection AddSwaggerSecurity(this IServiceCollection services, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!IdentityProvider.Enabled(environment, configuration)) return services;
        services.Configure<OpenApiOptions>("v1", o => o.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["oauth2"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "პროგრამის საკუთარი შესვლა (OpenID Connect, PKCE)",
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl = new Uri(IdentityProvider.AuthorizePath, UriKind.Relative),
                        TokenUrl = new Uri(IdentityProvider.TokenPath, UriKind.Relative),
                        Scopes = new Dictionary<string, string> { [IdentityProvider.ApiScope] = "/api/v1" },
                    },
                },
            };
            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("oauth2", document)] = [IdentityProvider.ApiScope] });
            return Task.CompletedTask;
        }));
        return services;
    }

    public static IApplicationBuilder UseInsuranceSwaggerUi(this IApplicationBuilder app, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!Enabled(environment, configuration)) return app;
        return app.UseSwaggerUI(o =>
        {
            o.RoutePrefix = Path;
            o.DocumentTitle = "Insurance API";
            o.SwaggerEndpoint("/api/openapi/v1.json", "Insurance API v1");
            o.OAuthClientId(IdentityProvider.SwaggerClient);
            o.OAuthUsePkce();
            o.OAuthScopes(IdentityProvider.ApiScope);
        });
    }
}
