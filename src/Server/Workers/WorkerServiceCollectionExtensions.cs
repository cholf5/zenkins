using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.Workers;

/// <summary>Composition root for card F-004's worker services.</summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WorkerService"/> scoped: it reads the scoped request/circuit tenant,
    /// sets the tenant for token-authenticated calls, and creates one <c>ZenkinsDbContext</c> per
    /// operation through the factory.
    /// </summary>
    public static IServiceCollection AddZenkinsWorkers(this IServiceCollection services)
    {
        services.AddScoped<WorkerService>();
        return services;
    }
}
