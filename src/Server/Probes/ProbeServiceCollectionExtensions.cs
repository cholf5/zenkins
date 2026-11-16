using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.Probes;

/// <summary>Composition root for card I-005's Git remote probe.</summary>
public static class ProbeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the probe options, the <b>singleton</b> registry (the product's one in-memory probe
    /// queue - see <see cref="GitProbeRegistry"/> for why memory is the right store) and the scoped
    /// service (it reads the request/circuit tenant, sets it for token-authenticated calls, and creates
    /// one context per operation).
    /// </summary>
    public static IServiceCollection AddZenkinsProbes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Read from the fully loaded configuration at first resolution, like RunOptions: the eager read
        // would freeze the values before the host builder's extra sources land.
        services.AddSingleton(sp => ProbeOptions.FromConfiguration(
            sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<GitProbeRegistry>();
        services.AddScoped<GitProbeService>();
        return services;
    }
}
