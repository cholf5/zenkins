using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zenkins.Server.Branches;

/// <summary>Composition root for card F-023's branch registry.</summary>
public static class BranchesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the branch service scoped: it reads the scoped request/circuit tenant and creates one
    /// <c>ZenkinsDbContext</c> per operation through the factory, like the other request services. The
    /// writes a trigger or a run performs are static helpers on the same class and take the caller's
    /// context, so discovery and the run/delivery it belongs to commit as one unit where that matters.
    /// </summary>
    public static IServiceCollection AddZenkinsBranches(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The background knobs are read from the fully loaded configuration at first resolution (the
        // other option types' discipline: an eager read would freeze before the test/deployment sources land).
        services.AddSingleton(sp => BranchDiscoveryOptions.FromConfiguration(key =>
            sp.GetRequiredService<IConfiguration>()[key]));
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<BranchService>();
        services.AddScoped<BranchDiscoveryService>();

        // The interval pass and the observation retry clock. Tests drive SweepOnceAsync instead of time.
        services.AddHostedService<BranchDiscoverySweeper>();
        return services;
    }
}
