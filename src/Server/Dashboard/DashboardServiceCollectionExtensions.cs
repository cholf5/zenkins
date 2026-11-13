using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.Dashboard;

/// <summary>Composition root for card D-002's dashboard read model.</summary>
public static class DashboardServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DashboardQuery"/> scoped (it reads the scoped request/circuit tenant and
    /// creates one <c>ZenkinsDbContext</c> per operation through the factory, like the other
    /// request-scoped services).
    /// </summary>
    public static IServiceCollection AddZenkinsDashboard(this IServiceCollection services)
    {
        services.AddScoped<DashboardQuery>();
        return services;
    }
}
