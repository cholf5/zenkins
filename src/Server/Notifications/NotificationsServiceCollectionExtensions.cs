using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Zenkins.Server.Notifications;

/// <summary>Composition root for card F-039's endpoints, sender and outbox consumer.</summary>
public static class NotificationsServiceCollectionExtensions
{
    public static IServiceCollection AddZenkinsNotifications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Read from the fully loaded configuration at first resolution, like RunOptions: an eager read of the
        // builder's configuration would freeze the values before the host's own sources land.
        services.AddSingleton(sp => NotificationOptions.FromConfiguration(
            sp.GetRequiredService<IConfiguration>()));

        // The purpose-bound cipher and the HTTP sender are process-wide: the key ring is shared, and one HttpClient
        // per process is the connection-pool discipline (the SSRF stance is inside the sender).
        services.AddSingleton<NotificationSecretProtector>();
        services.AddSingleton<NotificationSender>();

        // Scoped like the other request services: the tenant comes from the request (or from the row the pass names).
        services.AddScoped<NotificationService>();
        services.AddScoped<NotificationDispatchPass>();

        services.AddHostedService<NotificationDispatcher>();
        return services;
    }
}
