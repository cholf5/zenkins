using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Zenkins.Server.Artifacts;

/// <summary>Composition root for card F-016's artifact store, service and sweeper.</summary>
public static class ArtifactsServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsArtifacts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Resolved on first use, not at registration: the content root and the configuration sources are
        // applied while the host builds, and a path read too early would silently freeze a default.
        services.AddSingleton(sp => ArtifactOptions.FromConfiguration(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>().ContentRootPath));

        // The transfer counters are process-wide on purpose: the streaming test needs a number that says
        // "this many bytes crossed the boundary while the largest buffer stayed at 64 KiB".
        services.AddSingleton<ArtifactTransferTelemetry>();
        services.AddSingleton<ArtifactStore>();

        // Scoped like the other request services (token authentication selects the tenant for worker calls;
        // the download reads through the request's tenant filter).
        services.AddScoped<ArtifactService>();

        services.AddHostedService<ArtifactSweeper>();
        return services;
    }
}
