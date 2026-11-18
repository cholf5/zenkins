using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zenkins.Server.Runs;

namespace Zenkins.Server.Polling;

/// <summary>Composition root for card F-025's SCM polling.</summary>
public static class PollingServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsPolling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The knobs are read from the fully loaded configuration at first resolution (the other option
        // types' discipline: an eager read would freeze before the test/deployment sources land).
        services.AddSingleton(sp => PollOptions.FromConfiguration(key =>
            sp.GetRequiredService<IConfiguration>()[key]));
        services.TryAddSingleton(TimeProvider.System);

        // The one shared change-trigger path: webhook and poll both resolve it scoped.
        services.AddScoped<ChangeTriggerService>();
        services.AddScoped<PollService>();

        // The interval pass. Tests drive SweepOnceAsync instead of waiting on real time.
        services.AddHostedService<PollSweeper>();
        return services;
    }
}
