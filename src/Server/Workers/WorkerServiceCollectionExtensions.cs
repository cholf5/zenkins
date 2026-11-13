using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.Workers;

/// <summary>Composition root for card F-004's worker services and card F-010's offline monitor.</summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WorkerService"/> scoped (it reads the scoped request/circuit tenant,
    /// sets the tenant for token-authenticated calls, and creates one <c>ZenkinsDbContext</c> per
    /// operation through the factory), and card F-010's heartbeat-staleness monitor.
    /// <para>
    /// <see cref="WorkerMonitorOptions"/> is read from the fully loaded application configuration
    /// at first resolution (the same discipline as <c>RunOptions</c>): the eager read of the
    /// WebApplicationBuilder's Configuration would freeze the values before the host builder's
    /// extra configuration sources land.
    /// </para>
    /// </summary>
    public static IServiceCollection AddZenkinsWorkers(this IServiceCollection services)
    {
        services.AddScoped<WorkerService>();
        services.AddSingleton(sp => WorkerMonitorOptions.FromConfiguration(
            sp.GetRequiredService<IConfiguration>()));
        services.AddHostedService<WorkerOfflineMonitor>();
        return services;
    }
}
