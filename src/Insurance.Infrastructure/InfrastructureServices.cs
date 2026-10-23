using Insurance.Application.Ports;
using Insurance.Infrastructure.Policies;
using Insurance.Infrastructure.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace Insurance.Infrastructure;

/// <summary>The adapters behind the Application's ports (ADR 0001/0002), registered by the host, the composition root.</summary>
public static class InfrastructureServices
{
    public static IServiceCollection AddInsuranceInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ISystemStatusQuery, SystemStatusQuery>();
        services.AddSingleton<IPolicyListQuery, PolicyListQuery>();
        return services;
    }
}
