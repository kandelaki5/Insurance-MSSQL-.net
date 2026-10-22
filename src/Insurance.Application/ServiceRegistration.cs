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
        return services;
    }
}
