using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zenkins.Server.Schedules;

/// <summary>Composition root for card F-024's schedules: the options, the service and the sweeper.</summary>
public static class ScheduleServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsSchedules(this IServiceCollection services)
    {
        // Read from the fully loaded application configuration at first resolution (the RunsOptions
        // discipline): the host builder's extra sources land after the builder's own configuration.
        services.AddSingleton(serviceProvider => ScheduleOptions.FromConfiguration(
            serviceProvider.GetRequiredService<IConfiguration>()));

        // One clock for the product's time-reading services (F-017 registers the same instance through
        // TryAdd, so a later registration never replaces it).
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<ScheduleService>();
        services.AddHostedService<ScheduleSweeper>();
        return services;
    }
}
