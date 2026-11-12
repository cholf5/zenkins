using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.Runs;

/// <summary>Composition root for card F-006's run services.</summary>
public static class RunsServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsRuns(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Immutable once read; the lease window and the sweep interval do not change at runtime.
        services.AddSingleton(RunOptions.FromConfiguration(configuration));

        // Scoped like the other request services: it reads the scoped request/circuit tenant,
        // sets the tenant for token-authenticated calls, and creates one context per operation.
        services.AddScoped<RunService>();
        services.AddHostedService<LeaseSweeper>();
        return services;
    }
}
