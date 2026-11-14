using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.Artifacts;

/// <summary>Composition root for card F-016's artifact store, service and sweeper.</summary>
public static class ArtifactsServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsArtifacts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Resolved on first use, not at registration: the configuration sources are applied while the
        // host builds, and a path read too early would silently freeze a default. A relative root
        // resolves from AppContext.BaseDirectory (the publish/install directory) - the I-003 path
        // discipline, not the content root or the working directory.
        services.AddSingleton(sp => ArtifactOptions.FromConfiguration(
            sp.GetRequiredService<IConfiguration>()));

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
