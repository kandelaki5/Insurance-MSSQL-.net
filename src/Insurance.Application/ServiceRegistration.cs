using Insurance.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Application;

/// <summary>The application's services for the web host.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddHttpClient("nbg", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<Rates>();
        // The UN consolidated sanctions list is large; its address is Insurance:Sanctions:UnListUrl (SanctionsOptions).
        services.AddHttpClient("sanctions", c => c.Timeout = TimeSpan.FromSeconds(120));
        return services;
    }
}
